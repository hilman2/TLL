using Colossal.Serialization.Entities;
using Unity.Entities;

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
        private const byte kVersion = 1;

        /// <summary>Vehicles (or pedestrians for a crosswalk) per hour, averaged over the last hour or two of game time.</summary>
        public float Recent;

        /// <summary>Same, averaged over about a game day.</summary>
        public float Daily;

        /// <summary>Highest recent rate, slowly decaying: the peak the junction has to be planned for.</summary>
        public float Peak;

        /// <summary>Mean number of vehicles waiting, over about a game day.</summary>
        public float Queue;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(Recent);
            writer.Write(Daily);
            writer.Write(Peak);
            writer.Write(Queue);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out Recent);
            reader.Read(out Daily);
            reader.Read(out Peak);
            reader.Read(out Queue);
        }
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

        /// <summary>Crosswalks: length in metres, for turning occupancy into people.</summary>
        public float Length;
    }
}
