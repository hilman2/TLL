using Colossal.Serialization.Entities;
using TLL.Core.Advisor;
using TLL.Core.Planning;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Components
{
    /// <summary>
    /// What the autopilot keeps per automatic junction between its rounds,
    /// and what it found, for the panel. The pending layout change and the
    /// flashing dwell are saved, so reloading does not start them over; the
    /// estimates are made again in the first round after loading.
    /// </summary>
    public struct AutopilotState : IComponentData, ISerializable
    {
        private const byte kVersion = 2;

        /// <summary>A layout recommended but not yet applied, and in how many reviews in a row.</summary>
        public PlanStrategy Pending;
        public byte PendingRounds;

        public FlashSchedule Flash;

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

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write((byte)Pending);
            writer.Write(PendingRounds);
            writer.Write(Flash.RoundsSinceChange);
            writer.Write(Flash.EndedByBacklog);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out byte pending);
            reader.Read(out PendingRounds);
            reader.Read(out Flash.RoundsSinceChange);
            if (version >= 2)
                reader.Read(out Flash.EndedByBacklog);
            Pending = (PlanStrategy)pending;
        }
    }
}
