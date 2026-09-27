using TLL.Core.Advisor;
using TLL.Core.Planning;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Components
{
    /// <summary>
    /// What the autopilot keeps per automatic junction between its rounds,
    /// and what it found, for the panel. Not saved: the decisions themselves
    /// live in <see cref="ManagedJunction"/>, and the next rounds refill this.
    /// </summary>
    public struct AutopilotState : IComponentData
    {
        /// <summary>A layout recommended but not yet applied, and in how many rounds in a row.</summary>
        public PlanStrategy Pending;
        public byte PendingRounds;

        /// <summary>Rounds since the junction last started or stopped flashing.</summary>
        public ushort RoundsSinceFlashChange;

        /// <summary>From the last layout round, per JunctionAdvisor.Strategies: mean delay in seconds and worst saturation.</summary>
        public float4 LayoutDelay;
        public float4 LayoutSaturation;
        public bool HasEstimate;

        /// <summary>The last review found too little traffic at the peak to compare layouts.</summary>
        public bool TooQuiet;

        public SignalAdvice SignalAdvice;

        /// <summary>Recent vehicles per hour on the main road and on the busiest side road approach.</summary>
        public float MajorVolume;
        public float MinorVolume;
    }
}
