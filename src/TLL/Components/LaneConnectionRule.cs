using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TLL.Components
{
    public enum LaneConnectionChange : byte
    {
        /// <summary>The game connects the two lanes; TLL takes the connection away.</summary>
        Removed = 1,

        /// <summary>The game does not connect the two lanes; TLL adds a connection.</summary>
        Added = 2,
    }

    /// <summary>
    /// A change the player made to which lane leads into which at a junction,
    /// on the node entity. A lane is named by its road and its index on that
    /// road, the index the game gives it in the road's lane layout, so the
    /// rule outlives the rebuilds of the junction. LaneRuleSystem applies the
    /// rules every time the game builds the junction's lanes.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct LaneConnectionRule : IBufferElementData, ISerializable
    {
        /// <summary>Layout of the saved data. Raise it when fields are added and read old layouts in Deserialize.</summary>
        private const byte kVersion = 1;

        public Entity FromEdge;
        public Entity ToEdge;

        /// <summary>The lanes' indices on their roads (PathNode.GetLaneIndex, low byte).</summary>
        public byte FromLane;
        public byte ToLane;

        public LaneConnectionChange Change;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(FromEdge);
            writer.Write(ToEdge);
            writer.Write(FromLane);
            writer.Write(ToLane);
            writer.Write((byte)Change);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out FromEdge);
            reader.Read(out ToEdge);
            reader.Read(out FromLane);
            reader.Read(out ToLane);
            reader.Read(out byte change);
            Change = (LaneConnectionChange)change;
        }
    }
}
