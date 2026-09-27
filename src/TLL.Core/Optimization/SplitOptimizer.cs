using System;
using TLL.Core.Control;

namespace TLL.Core.Optimization
{
    public struct OptimizerLimits
    {
        /// <summary>Shortest and longest cycle in steps.</summary>
        public int MinCycle;

        public int MaxCycle;

        /// <summary>Share of the old value kept per optimisation round, 0 to 1. Damps oscillation.</summary>
        public float Inertia;

        public static OptimizerLimits Default => new OptimizerLimits
        {
            MinCycle = SimTime.ToSteps(40f),
            MaxCycle = SimTime.ToSteps(120f),
            Inertia = 0.6f,
        };
    }

    /// <summary>Result of an optimisation round: new greens and the cycle they add up to.</summary>
    public struct SplitResult
    {
        public int Cycle;
        public ushort[] Green;
    }

    /// <summary>
    /// Chooses cycle length and green splits from what the controller measured.
    ///
    /// The demand of a phase is the share of time its lanes carried moving
    /// traffic during green (<see cref="PhaseStatistics.BusySteps"/>). That is
    /// the green the phase actually used, which the game lets us observe
    /// directly, where flows in vehicles per hour would have to be estimated.
    /// A phase that kept hitting its maximum has more demand than it could
    /// show, so its share is scaled up by how often that happened.
    ///
    /// With these shares as flow ratios, cycle and splits follow Webster:
    /// cycle = (1.5 L + 5 s) / (1 - Y), greens proportional to the ratios.
    /// </summary>
    public static class SplitOptimizer
    {
        /// <summary>Above this total flow ratio the junction is treated as saturated and gets the longest cycle.</summary>
        public const float SaturatedRatio = 0.9f;

        /// <summary>Flow ratio of each phase, from the statistics since the last round.</summary>
        /// <param name="elapsedSteps">Steps over which the statistics were collected.</param>
        public static float[] FlowRatios(PhaseData[] phases, int elapsedSteps)
        {
            var ratios = new float[phases.Length];
            if (elapsedSteps <= 0)
                return ratios;
            for (int i = 0; i < phases.Length; i++)
            {
                PhaseStatistics s = phases[i].Stats;
                float used = s.BusySteps / (float)elapsedSteps;
                float maxOutShare = s.Greens > 0 ? s.MaxOuts / (float)s.Greens : 0f;
                ratios[i] = used * (1f + 0.5f * maxOutShare);
            }
            return ratios;
        }

        /// <summary>Webster's optimum cycle for the given ratios, within the limits.</summary>
        public static int OptimalCycle(float[] ratios, int intergreen, OptimizerLimits limits)
        {
            float y = 0f;
            foreach (float r in ratios)
                y += r;
            if (y >= SaturatedRatio)
                return limits.MaxCycle;
            float lost = ratios.Length * intergreen;
            float cycle = (1.5f * lost + SimTime.ToSteps(5f)) / (1f - y);
            return Clamp((int)Math.Round(cycle), limits.MinCycle, limits.MaxCycle);
        }

        /// <summary>
        /// Divides the green time of <paramref name="cycle"/> among the phases
        /// in proportion to their ratios, keeping every minimum green.
        /// The greens plus the intergreens add up to the cycle exactly, unless
        /// the minimum greens alone need more, in which case the cycle grows.
        /// </summary>
        public static SplitResult Splits(PhaseData[] phases, float[] ratios, int cycle, int intergreen)
        {
            int n = phases.Length;
            var green = new ushort[n];
            int available = cycle - n * intergreen;
            int minimumSum = 0;
            for (int i = 0; i < n; i++)
                minimumSum += phases[i].MinGreen;
            if (available <= minimumSum)
            {
                for (int i = 0; i < n; i++)
                    green[i] = phases[i].MinGreen;
                return new SplitResult { Cycle = minimumSum + n * intergreen, Green = green };
            }

            float total = 0f;
            foreach (float r in ratios)
                total += Math.Max(r, 0f);

            // Hand out the time in proportion to the ratios. A phase whose
            // proportional share is below its minimum is pinned at the minimum
            // and drops out; the others share what remains. Pinning can push
            // another phase under its minimum, so repeat until nothing changes.
            var pinned = new bool[n];
            bool changed = true;
            while (changed)
            {
                changed = false;
                Remaining(phases, ratios, total, pinned, available, out int free, out float freeRatio);
                for (int i = 0; i < n; i++)
                {
                    if (!pinned[i] && free * Share(ratios, total, i) / freeRatio < phases[i].MinGreen)
                    {
                        pinned[i] = true;
                        changed = true;
                    }
                }
            }

            int assigned = 0;
            int largest = -1;
            {
                Remaining(phases, ratios, total, pinned, available, out int free, out float freeRatio);
                for (int i = 0; i < n; i++)
                {
                    int g = pinned[i] ? phases[i].MinGreen : (int)Math.Floor(free * Share(ratios, total, i) / freeRatio);
                    green[i] = (ushort)g;
                    assigned += g;
                    if (!pinned[i] && (largest < 0 || g > green[largest]))
                        largest = i;
                }
            }
            // Rounding down leaves a few steps; they go to the largest phase so
            // the cycle comes out exact.
            if (largest >= 0)
                green[largest] = (ushort)(green[largest] + available - assigned);
            return new SplitResult { Cycle = cycle, Green = green };
        }

        /// <summary>
        /// One full round for a junction running on its own: new cycle, new
        /// splits, blended with the current greens by the inertia.
        /// </summary>
        public static SplitResult Optimize(PhaseData[] phases, int elapsedSteps, int intergreen, OptimizerLimits limits)
        {
            float[] ratios = FlowRatios(phases, elapsedSteps);
            int currentCycle = 0;
            for (int i = 0; i < phases.Length; i++)
                currentCycle += phases[i].Green + intergreen;
            int target = OptimalCycle(ratios, intergreen, limits);
            int cycle = currentCycle > 0
                ? (int)Math.Round(limits.Inertia * currentCycle + (1f - limits.Inertia) * target)
                : target;
            cycle = Clamp(cycle, limits.MinCycle, limits.MaxCycle);
            SplitResult fresh = Splits(phases, ratios, cycle, intergreen);
            return Blend(phases, fresh, limits.Inertia);
        }

        /// <summary>
        /// Moves the greens part of the way from their current values to the
        /// new ones. The sum is kept equal to the new cycle.
        /// </summary>
        private static SplitResult Blend(PhaseData[] phases, SplitResult fresh, float inertia)
        {
            int n = phases.Length;
            int currentSum = 0;
            int freshSum = 0;
            for (int i = 0; i < n; i++)
            {
                currentSum += phases[i].Green;
                freshSum += fresh.Green[i];
            }
            if (currentSum == 0 || inertia <= 0f)
                return fresh;

            var green = new ushort[n];
            int assigned = 0;
            int largest = 0;
            for (int i = 0; i < n; i++)
            {
                // Scale the current green to the new total first, so blending
                // only moves time between phases and does not change the cycle.
                float scaled = phases[i].Green * (freshSum / (float)currentSum);
                int g = (int)Math.Floor(inertia * scaled + (1f - inertia) * fresh.Green[i]);
                g = Math.Max(g, phases[i].MinGreen);
                green[i] = (ushort)g;
                assigned += g;
                if (green[i] > green[largest])
                    largest = i;
            }
            int diff = freshSum - assigned;
            green[largest] = (ushort)Math.Max(phases[largest].MinGreen, green[largest] + diff);
            return new SplitResult { Cycle = fresh.Cycle, Green = green };
        }

        /// <summary>A phase's weight in the split. Without any measured demand all phases weigh the same.</summary>
        private static float Share(float[] ratios, float total, int i)
        {
            return total > 0f ? Math.Max(ratios[i], 0f) : 1f;
        }

        /// <summary>Green time and total weight left for the phases not pinned at their minimum.</summary>
        private static void Remaining(PhaseData[] phases, float[] ratios, float total, bool[] pinned, int available,
            out int free, out float freeRatio)
        {
            free = available;
            freeRatio = 0f;
            for (int i = 0; i < phases.Length; i++)
            {
                if (pinned[i])
                    free -= phases[i].MinGreen;
                else
                    freeRatio += Share(ratios, total, i);
            }
            // All phases pinned cannot happen while available exceeds the sum
            // of minimums, but a zero weight must not divide.
            if (freeRatio <= 0f)
                freeRatio = 1f;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
