using Colossal.Serialization.Entities;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Components
{
    /// <summary>
    /// Traffic measured on one movement, one element per movement in the
    /// order of <see cref="JunctionMovement"/>. Rates are per hour of
    /// simulated time, the time base of the vehicles, in which a lane
    /// discharges about 1800 vehicles per hour. Saved, so decisions based
    /// on days of traffic survive a reload.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MovementStatistics : IBufferElementData, ISerializable
    {
        private const byte kVersion = 4;

        /// <summary>Vehicles (or pedestrians for a crosswalk) per hour, averaged over the last hour or two of game time.</summary>
        public float Recent;

        /// <summary>Same, averaged over about a game day.</summary>
        public float Daily;

        /// <summary>Highest recent rate, slowly decaying: the peak the junction has to be planned for.</summary>
        public float Peak;

        /// <summary>Mean number of vehicles waiting, over about a game day.</summary>
        public float Queue;

        /// <summary>Mean number of vehicles waiting over the last rounds, smoothed like <see cref="Recent"/>.</summary>
        public float RecentQueue;

        /// <summary>
        /// Highest <see cref="RecentQueue"/>, slowly decaying like
        /// <see cref="Peak"/>: the queue of the rush hour, which the daily
        /// mean dilutes with the quiet night.
        /// </summary>
        public float PeakQueue;

        /// <summary>
        /// Mean number of vehicles waiting in the last round alone, unsmoothed,
        /// for decisions that cannot wait for the average, counted only
        /// while the movement's exit was free: a queue behind a backed-up
        /// exit is not the signal's doing, and neither flashing nor another
        /// layout would clear it. Not saved.
        /// </summary>
        public float LastQueue;

        /// <summary>
        /// Highest <see cref="Recent"/> per time-of-day window
        /// (AutopilotSystem.TimeWindow), windows 0 to 3 and 4 to 7, fading
        /// over the days like <see cref="Peak"/> over the hours: what this
        /// time of day brings, for the layout that runs then. 0 for a window
        /// not yet seen.
        /// </summary>
        public float4 WindowsEarly;
        public float4 WindowsLate;

        public float Window(int window)
        {
            return window < 4 ? WindowsEarly[window] : WindowsLate[window - 4];
        }

        public void SetWindow(int window, float value)
        {
            if (window < 4)
                WindowsEarly[window] = value;
            else
                WindowsLate[window - 4] = value;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(Recent);
            writer.Write(Daily);
            writer.Write(Peak);
            writer.Write(Queue);
            writer.Write(PeakQueue);
            writer.Write(RecentQueue);
            writer.Write(WindowsEarly);
            writer.Write(WindowsLate);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out Recent);
            reader.Read(out Daily);
            reader.Read(out Peak);
            reader.Read(out Queue);
            if (version >= 2)
                reader.Read(out PeakQueue);
            if (version >= 3)
                reader.Read(out RecentQueue);
            if (version >= 4)
            {
                reader.Read(out WindowsEarly);
                reader.Read(out WindowsLate);
            }
        }
    }

    /// <summary>
    /// How badly a managed junction copes, from its long-term measurement:
    /// the rush-hour queue of its worst movement. Written by the autopilot
    /// once per round for the problem list and the map; not saved, the next
    /// round after loading refills it from the saved statistics.
    /// </summary>
    public struct JunctionHealth : IComponentData
    {
        /// <summary>Largest <see cref="MovementStatistics.PeakQueue"/> of the junction's vehicle movements.</summary>
        public float WorstQueue;
    }

    /// <summary>
    /// Counts of the running measurement round, one element per movement.
    /// The control job adds to them, the autopilot turns them into
    /// <see cref="MovementStatistics"/> and clears them. Not saved.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct MovementCounter : IBufferElementData
    {
        /// <summary>Vehicles that entered the junction on this movement.</summary>
        public uint Vehicles;

        /// <summary>Crosswalks: sum over steps of the people on it.</summary>
        public float PedestrianSteps;

        /// <summary>Sum over steps of the vehicles waiting for this movement.</summary>
        public float QueueSteps;

        /// <summary>
        /// The part of <see cref="QueueSteps"/> while the movement's exit was
        /// free: vehicles waiting for the signal, not for room behind the
        /// junction.
        /// </summary>
        public float FreeQueueSteps;

        /// <summary>Vehicles among <see cref="Vehicles"/> that entered at speed, without stopping at the line.</summary>
        public uint Flowing;

        /// <summary>Steps during which the movement's exit was backed up to the junction.</summary>
        public uint BlockedSteps;

        /// <summary>Crosswalks: length in metres, for turning occupancy into people.</summary>
        public float Length;
    }
}
