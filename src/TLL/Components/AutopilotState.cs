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
        /// <summary>
        /// Layout of the saved data. 6 added the scramble: the pending
        /// change's, and the measurements of the layouts run with one.
        /// </summary>
        private const byte kVersion = 6;

        /// <summary>A layout recommended but not yet applied, and how long the running one has run.</summary>
        public LayoutSchedule Layout;

        public FlashSchedule Flash;

        /// <summary>What the junction measured of its layouts; the autopilot corrects its estimates with it.</summary>
        public LayoutMemory Memory;

        /// <summary>
        /// Rounds the junction stays out of green waves, after one it was in
        /// made its junctions wait longer than running alone.
        /// </summary>
        public ushort WaveBan;

        /// <summary>
        /// Rounds the autopilot keeps the layout, after the green waves chose
        /// it so that a wave can run (CoordinationSystem). While the junction
        /// runs in a wave, the autopilot keeps its layout anyway.
        /// </summary>
        public ushort LayoutHold;

        /// <summary>
        /// The layout was chosen for a wave that has not started yet. If the
        /// corridor still has no band at the next round, the trial failed.
        /// </summary>
        public bool WaveTrial;

        /// <summary>From the last measurement period: the vehicles' mean wait measured, and what the model expected, in seconds. Not saved.</summary>
        public float MeasuredWait;
        public float ModelledWait;

        /// <summary>From the last layout round, per JunctionAdvisor.Strategies: mean delay in seconds and worst saturation.</summary>
        public float4 LayoutDelay;
        public float4 LayoutSaturation;

        /// <summary>The same for each layout with a scramble; -1 where the junction has no crosswalks.</summary>
        public float4 ScrambleDelay;
        public float4 ScrambleSaturation;
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
            writer.Write((byte)Layout.Pending);
            writer.Write(Layout.PendingReviews);
            writer.Write(Flash.RoundsSinceChange);
            writer.Write(Flash.EndedByBacklog);
            for (int i = 0; i < LayoutMemory.Layouts * 2; i++)
            {
                Calibration c = Memory.Get(i % LayoutMemory.Layouts, i >= LayoutMemory.Layouts);
                writer.Write(c.Factor);
                writer.Write(c.Samples);
                writer.Write(c.Backlog);
            }
            writer.Write(WaveBan);
            writer.Write(Layout.Age);
            writer.Write(LayoutHold);
            writer.Write(WaveTrial);
            writer.Write(Layout.PendingScramble);
            for (int i = 0; i < LayoutMemory.Layouts; i++)
            {
                Calibration c = Memory.Get(i, false, true);
                writer.Write(c.Factor);
                writer.Write(c.Samples);
                writer.Write(c.Backlog);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out byte pending);
            reader.Read(out Layout.PendingReviews);
            // Saves from before the dwell count: the running layout is old.
            Layout.Age = byte.MaxValue;
            reader.Read(out Flash.RoundsSinceChange);
            if (version >= 2)
                reader.Read(out Flash.EndedByBacklog);
            if (version >= 3)
            {
                for (int i = 0; i < LayoutMemory.Layouts * 2; i++)
                {
                    var c = new Calibration();
                    reader.Read(out c.Factor);
                    reader.Read(out c.Samples);
                    reader.Read(out c.Backlog);
                    Memory.Set(i % LayoutMemory.Layouts, i >= LayoutMemory.Layouts, c);
                }
                reader.Read(out WaveBan);
            }
            if (version >= 4)
                reader.Read(out Layout.Age);
            if (version >= 5)
            {
                reader.Read(out LayoutHold);
                reader.Read(out WaveTrial);
            }
            Layout.Pending = (PlanStrategy)pending;
            if (version >= 6)
            {
                reader.Read(out Layout.PendingScramble);
                for (int i = 0; i < LayoutMemory.Layouts; i++)
                {
                    var c = new Calibration();
                    reader.Read(out c.Factor);
                    reader.Read(out c.Samples);
                    reader.Read(out c.Backlog);
                    Memory.Set(i, false, true, c);
                }
            }
            else
            {
                // The pedestrian scramble layout became the permissive one
                // with a scramble; so does what the junction measured of it.
                const int scrambleLayout = 3;
                Memory.Set(0, false, true, Memory.Get(scrambleLayout, false));
                Memory.Set(scrambleLayout, false, default(Calibration));
                Memory.Set(scrambleLayout, true, default(Calibration));
                if (Layout.Pending == PlanStrategy.ExclusivePedestrian)
                {
                    Layout.Pending = PlanStrategy.Permissive;
                    Layout.PendingScramble = true;
                }
            }
        }
    }
}
