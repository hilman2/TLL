using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TLL.Components
{
    /// <summary>
    /// Solid lines on one approach of a junction: on the last
    /// <see cref="Pieces"/> road pieces before it, drivers heading for the
    /// junction do not change lanes if they can help it. On the node entity,
    /// set by the player in the planner. The lanes themselves carry it as
    /// <see cref="LaneChangeBan"/> on their roads (SolidLines).
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SolidLineRule : IBufferElementData, ISerializable
    {
        /// <summary>Layout of the saved data. Raise it when fields are added and read old layouts in Deserialize.</summary>
        private const byte kVersion = 1;

        /// <summary>The road the approach comes in on, at the junction.</summary>
        public Entity Edge;

        /// <summary>Road pieces back from the stop line, following the road across plain nodes.</summary>
        public byte Pieces;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(Edge);
            writer.Write(Pieces);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out Edge);
            reader.Read(out Pieces);
        }
    }

    /// <summary>
    /// On a road piece inside solid lines: its lanes heading for
    /// <see cref="Toward"/> lose the game's permission to change lanes
    /// (SlaveLaneFlags.AllowChange) every time the game builds them. A road
    /// piece between two junctions can carry one for each direction.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct LaneChangeBan : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        /// <summary>The node the banned lanes lead to: the junction, or a plain node on the way to it.</summary>
        public Entity Toward;

        /// <summary>The junction whose rule this comes from.</summary>
        public Entity Junction;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(Toward);
            writer.Write(Junction);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out Toward);
            reader.Read(out Junction);
        }
    }
}
