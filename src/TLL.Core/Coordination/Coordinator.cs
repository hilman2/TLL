using System;
using System.Collections.Generic;
using TLL.Core.Control;
using TLL.Core.Optimization;

namespace TLL.Core.Coordination
{
    /// <summary>One junction of a corridor, as the coordinator needs it.</summary>
    public sealed class CorridorMember
    {
        public PhaseData[] Phases;

        /// <summary>For each phase, bit i set: movement i has green.</summary>
        public ulong[] PhaseMovements;

        public int Intergreen;

        /// <summary>Movement of the corridor's through traffic in direction A (first to last junction), or -1.</summary>
        public int MovementA = -1;

        /// <summary>Same for direction B.</summary>
        public int MovementB = -1;

        /// <summary>Measured flow ratio per phase (see <see cref="SplitOptimizer.FlowRatios"/>), or null to keep the current proportions.</summary>
        public float[] Ratios;

        /// <summary>The cycle this junction would choose on its own, in steps, or 0 if unknown.</summary>
        public int DesiredCycle;
    }

    /// <summary>Timing for all junctions of a corridor.</summary>
    public sealed class CoordinationPlan
    {
        public int Cycle;

        /// <summary>New greens per member and phase.</summary>
        public ushort[][] Greens;

        /// <summary>Cycle start per member, in steps within the cycle.</summary>
        public int[] Offsets;

        /// <summary>Per member and phase: the phase carries the corridor's through traffic.</summary>
        public bool[][] Coordinated;

        public int BandwidthA;
        public int BandwidthB;
    }

    /// <summary>
    /// Puts the junctions of a corridor on one cycle and finds offsets for a
    /// green wave in both directions.
    /// </summary>
    public static class Coordinator
    {
        /// <summary>Share of the speed limit that traffic actually drives between junctions.</summary>
        public const float SpeedFactor = 0.85f;

        public static CoordinationPlan Plan(CorridorPath path, IList<CorridorMember> members, OptimizerLimits limits)
        {
            int n = members.Count;
            if (n != path.Junctions.Count)
                throw new ArgumentException("One member per corridor junction expected.", nameof(members));

            // The busiest junction sets the cycle; the others get more green
            // than they need, which the green wave uses.
            int cycle = limits.MinCycle;
            foreach (CorridorMember m in members)
                cycle = Math.Max(cycle, m.DesiredCycle > 0 ? m.DesiredCycle : CurrentCycle(m));
            cycle = Math.Min(cycle, limits.MaxCycle);

            var greens = new ushort[n][];
            for (int pass = 0; pass < 2; pass++)
            {
                int needed = cycle;
                for (int i = 0; i < n; i++)
                {
                    SplitResult split = SplitOptimizer.Splits(members[i].Phases, RatiosOf(members[i]), cycle, members[i].Intergreen);
                    greens[i] = split.Green;
                    needed = Math.Max(needed, split.Cycle);
                }
                // A junction whose minimum greens do not fit forces a longer
                // cycle on the whole corridor; one more pass spreads it.
                if (needed == cycle)
                    break;
                cycle = needed;
            }

            var corridor = new Corridor { Cycle = cycle };
            var coordinated = new bool[n][];
            for (int i = 0; i < n; i++)
            {
                CorridorMember m = members[i];
                bool[] hasA = PhasesWith(m, m.MovementA);
                bool[] hasB = PhasesWith(m, m.MovementB);
                PhaseWindow a = m.MovementA >= 0 ? PhaseWindow.Of(greens[i], m.Intergreen, hasA) : new PhaseWindow { Length = cycle };
                PhaseWindow b = m.MovementB >= 0 ? PhaseWindow.Of(greens[i], m.Intergreen, hasB) : new PhaseWindow { Length = cycle };
                corridor.Junctions.Add(new CorridorJunction
                {
                    WindowStartA = a.Start,
                    WindowLengthA = a.Length,
                    WindowStartB = b.Start,
                    WindowLengthB = b.Length,
                });
                coordinated[i] = new bool[m.Phases.Length];
                for (int p = 0; p < m.Phases.Length; p++)
                    coordinated[i][p] = hasA[p] || hasB[p];
            }
            foreach (SignalLink link in path.Links)
            {
                int travel = SimTime.ToSteps(link.Length / Math.Max(1f, link.Speed * SpeedFactor));
                corridor.TravelA.Add(travel);
                corridor.TravelB.Add(travel);
            }

            int[] offsets = GreenWave.Optimize(corridor);
            return new CoordinationPlan
            {
                Cycle = cycle,
                Greens = greens,
                Offsets = offsets,
                Coordinated = coordinated,
                BandwidthA = GreenWave.BandwidthA(corridor, offsets),
                BandwidthB = GreenWave.BandwidthB(corridor, offsets),
            };
        }

        private static int CurrentCycle(CorridorMember m)
        {
            int cycle = 0;
            foreach (PhaseData p in m.Phases)
                cycle += p.Green + m.Intergreen;
            return cycle;
        }

        /// <summary>The measured ratios, or the current greens as ratios when nothing was measured.</summary>
        private static float[] RatiosOf(CorridorMember m)
        {
            if (m.Ratios != null)
            {
                float sum = 0f;
                foreach (float r in m.Ratios)
                    sum += r;
                if (sum > 0f)
                    return m.Ratios;
            }
            var ratios = new float[m.Phases.Length];
            for (int p = 0; p < ratios.Length; p++)
                ratios[p] = m.Phases[p].Green;
            return ratios;
        }

        private static bool[] PhasesWith(CorridorMember m, int movement)
        {
            var has = new bool[m.Phases.Length];
            if (movement < 0)
                return has;
            for (int p = 0; p < has.Length; p++)
                has[p] = (m.PhaseMovements[p] & (1UL << movement)) != 0;
            return has;
        }
    }
}
