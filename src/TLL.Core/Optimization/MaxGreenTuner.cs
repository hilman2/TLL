using System;
using TLL.Core.Control;

namespace TLL.Core.Optimization
{
    /// <summary>
    /// The maximum green of the demand-driven modes, adjusted from how the
    /// phase's greens ended since the last adjustment: longer while they end
    /// at the maximum with demand left, slowly shorter while they all run
    /// empty, within <see cref="Floor"/> and <see cref="Cap"/>.
    /// </summary>
    /// <remarks>
    /// The maximum used to follow the phase's share of the traffic it moved.
    /// A phase with a short maximum moves little, so its share, and with it
    /// its maximum, stayed short: phases ended at 7 s with their queues
    /// still standing, round after round. How often a green is cut off at
    /// its maximum measures the demand the green did not meet.
    /// </remarks>
    public static class MaxGreenTuner
    {
        /// <summary>Shortest maximum green, in steps: 20 s.</summary>
        public static readonly int Floor = SimTime.ToSteps(20f);

        /// <summary>Longest maximum green, in steps: 90 s.</summary>
        public static readonly int Cap = SimTime.ToSteps(90f);

        /// <summary>Share of greens ending at the maximum from which it grows, and from which it grows fast.</summary>
        public const float GrowAt = 0.2f;
        public const float GrowFastAt = 0.5f;

        /// <summary>The maximum green, in steps, for the phase's next greens.</summary>
        public static int Next(in PhaseData phase)
        {
            PhaseStatistics s = phase.Stats;
            int max = phase.MaxGreen;
            if (s.Greens > 0)
            {
                float cut = s.MaxOuts / (float)s.Greens;
                if (cut >= GrowFastAt)
                    max = Math.Max(max + SimTime.ToSteps(4f), (int)(max * 1.5f));
                else if (cut >= GrowAt)
                    max = Math.Max(max + SimTime.ToSteps(2f), (int)(max * 1.2f));
                else if (s.MaxOuts == 0 && s.GapOuts >= s.Greens)
                    max = (int)(max * 0.9f);
            }
            // A late pedestrian call must still fit its walk.
            int floor = Math.Max(Floor, phase.PlannedMinimum + 1);
            return Math.Min(Cap, Math.Max(floor, max));
        }
    }
}
