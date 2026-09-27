using System;
using TLL.Core.Planning;

namespace TLL.Core.Advisor
{
    /// <summary>
    /// Chooses the phase layout of a junction from its traffic, by comparing
    /// the expected delay of every layout (see <see cref="DelayModel"/>).
    /// </summary>
    public static class JunctionAdvisor
    {
        public static readonly PlanStrategy[] Strategies =
        {
            PlanStrategy.Permissive,
            PlanStrategy.ProtectedTurns,
            PlanStrategy.Split,
            PlanStrategy.ExclusivePedestrian,
        };

        /// <summary>A change must save at least this share of the total delay.</summary>
        public const float SwitchMargin = 0.15f;

        /// <summary>And at least this many seconds per road user; small absolute gains are noise.</summary>
        public const float SwitchSeconds = 1.5f;

        /// <summary>Estimates every layout. Index i belongs to <see cref="Strategies"/>[i].</summary>
        public static PlanEstimate[] EvaluateAll(JunctionModel junction, float[] volumes, DelayParameters p)
        {
            var result = new PlanEstimate[Strategies.Length];
            for (int i = 0; i < Strategies.Length; i++)
            {
                PhasePlan plan = PhasePlanner.Build(junction, Strategies[i]);
                result[i] = DelayModel.Estimate(junction, plan, volumes, p);
            }
            return result;
        }

        /// <summary>A layout counts as coping when no movement exceeds this volume-to-capacity ratio.</summary>
        public const float Capacity = 1f;

        /// <summary>Among layouts that all overload, a switch must lower the worst ratio by this much.</summary>
        public const float SaturationMargin = 0.05f;

        /// <summary>
        /// The layout to run: the current one, unless another is better by a
        /// clear margin. The margin keeps a junction from switching back and
        /// forth between layouts that are about equal.
        ///
        /// "Better" first means coping: a movement above capacity queues up
        /// without end and sooner or later blocks the lanes next to it, which
        /// the average delay does not show, since it is diluted by the
        /// movements that flow. So a layout in which every movement copes
        /// beats any in which one does not; among layouts that cope the delay
        /// decides; among layouts that all overload, the lowest overload.
        /// </summary>
        public static PlanStrategy Choose(PlanStrategy current, PlanEstimate[] estimates)
        {
            int best = 0;
            for (int i = 1; i < estimates.Length; i++)
            {
                if (Better(estimates[i], estimates[best]))
                    best = i;
            }
            int currentIndex = Array.IndexOf(Strategies, current);
            if (currentIndex < 0)
                return Strategies[best];
            return ClearlyBetter(estimates[best], estimates[currentIndex]) ? Strategies[best] : current;
        }

        private static bool Copes(PlanEstimate e)
        {
            return e.WorstSaturation <= Capacity;
        }

        private static bool Better(PlanEstimate a, PlanEstimate b)
        {
            if (Copes(a) != Copes(b))
                return Copes(a);
            return Copes(a) ? a.TotalDelay < b.TotalDelay : a.WorstSaturation < b.WorstSaturation;
        }

        private static bool ClearlyBetter(PlanEstimate candidate, PlanEstimate now)
        {
            if (Copes(candidate) != Copes(now))
                return Copes(candidate);
            if (!Copes(now))
                return candidate.WorstSaturation < now.WorstSaturation - SaturationMargin;
            return candidate.TotalDelay < now.TotalDelay * (1f - SwitchMargin)
                && now.AverageDelay - candidate.AverageDelay >= SwitchSeconds;
        }
    }

    /// <summary>
    /// Whether a junction may run on flashing yellow: the main road passes,
    /// the side roads give way as at a yield sign. Judged by the capacity the
    /// side road has in the gaps of the main road (HCM two-way stop control).
    /// </summary>
    public static class FlashAdvisor
    {
        /// <summary>Critical gap and follow-up time for a side road crossing or joining a main road, in seconds (HCM).</summary>
        private const float CriticalGap = 6.5f;
        private const float FollowUp = 3.5f;

        /// <summary>Total vehicles per hour below which flashing starts, and above which it ends again.</summary>
        public const float StartBelow = 400f;
        public const float EndAbove = 650f;

        /// <summary>Side road load (volume over gap capacity) below which flashing starts, and above which it ends.</summary>
        public const float StartSaturation = 0.35f;
        public const float EndSaturation = 0.6f;

        /// <param name="flashing">Whether the junction flashes now; the thresholds differ by direction so it does not flicker.</param>
        /// <param name="major">Vehicles per hour on the main road, both directions.</param>
        /// <param name="minor">Vehicles per hour on the busiest side road approach.</param>
        public static bool Decide(bool flashing, float major, float minor, float totalMinor)
        {
            float total = major + totalMinor;
            float load = minor / Math.Max(1f, DelayModel.GapCapacity(major, CriticalGap, FollowUp));
            if (flashing)
                return total <= EndAbove && load <= EndSaturation;
            return total < StartBelow && load < StartSaturation;
        }
    }

    public enum SignalAdvice
    {
        Keep,

        /// <summary>The side roads could not cope with priority rules; signals are needed.</summary>
        AddSignals,

        /// <summary>Priority rules would serve everyone with less delay than the signals do.</summary>
        RemoveSignals,
    }

    /// <summary>
    /// Whether a junction needs traffic lights at its peak, by comparing the
    /// side road delay under priority rules (HCM two-way stop control) with
    /// the delay of the best signal plan. Meant for peak volumes, so a
    /// junction that needs signals at any time of day keeps them.
    /// </summary>
    public static class SignalAdvisor
    {
        private const float CriticalGap = 6.5f;
        private const float FollowUp = 3.5f;

        /// <summary>Side road load above which signals are needed regardless of delay.</summary>
        public const float MaxUnsignalisedLoad = 0.9f;

        /// <summary>Signals are only removed if the side roads stay well below capacity without them.</summary>
        public const float RemoveBelowLoad = 0.5f;

        /// <summary>Side road delay in seconds per vehicle under priority rules (HCM two-way stop control delay).</summary>
        public static float PriorityDelay(float major, float minor, float period = 0.25f)
        {
            float capacity = Math.Max(1f, DelayModel.GapCapacity(major, CriticalGap, FollowUp));
            float x = minor / capacity;
            float service = 3600f / capacity;
            float over = x - 1f;
            return service + 900f * period * (over + (float)Math.Sqrt(over * over + service * x / (450f * period))) + 5f;
        }

        /// <param name="signalled">Whether the junction has signals now.</param>
        /// <param name="major">Main road vehicles per hour at the peak, both directions.</param>
        /// <param name="minor">Busiest side road approach at the peak.</param>
        /// <param name="signalDelay">Mean delay per vehicle of the best signal plan at the peak, in seconds.</param>
        public static SignalAdvice Decide(bool signalled, float major, float minor, float signalDelay)
        {
            float capacity = Math.Max(1f, DelayModel.GapCapacity(major, CriticalGap, FollowUp));
            float load = minor / capacity;
            float priority = PriorityDelay(major, minor);
            // Under priority rules only the side road waits; the main road
            // passes. Weigh the side road delay by its share of the traffic.
            float share = minor / Math.Max(1f, major + minor);
            float priorityAverage = priority * share;
            if (!signalled)
                return load > MaxUnsignalisedLoad || priorityAverage > signalDelay * 1.5f ? SignalAdvice.AddSignals : SignalAdvice.Keep;
            return load < RemoveBelowLoad && priorityAverage < signalDelay * 0.6f ? SignalAdvice.RemoveSignals : SignalAdvice.Keep;
        }
    }
}
