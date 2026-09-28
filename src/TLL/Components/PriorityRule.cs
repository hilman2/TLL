using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TLL.Components
{
    /// <summary>The sign vehicles see on one approach of a junction without traffic lights.</summary>
    public enum PrioritySign : byte
    {
        /// <summary>
        /// The game's own rule: the side road gives way to a road of a higher
        /// class, and between roads of one class the one from the right goes
        /// first.
        /// </summary>
        Game = 0,

        /// <summary>Priority road: its vehicles go before those of approaches without it.</summary>
        Priority = 1,

        Yield = 2,

        /// <summary>Vehicles stop at the line, then give way.</summary>
        Stop = 3,
    }

    [System.Flags]
    public enum PriorityRuleFlags : byte
    {
        None = 0,

        /// <summary>The player set the sign; the autopilot leaves the junction's signs alone.</summary>
        Player = 1,
    }

    /// <summary>
    /// The sign on one approach of a junction, on the node entity. Like
    /// <see cref="TurnRule"/>, applied to the junction's lanes every time the
    /// game builds them (LaneRuleSystem). Has no effect while the junction
    /// has traffic lights.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct PriorityRule : IBufferElementData, ISerializable
    {
        /// <summary>Layout of the saved data. Raise it when fields are added and read old layouts in Deserialize.</summary>
        private const byte kVersion = 1;

        /// <summary>The road the approach comes in on.</summary>
        public Entity Edge;
        public PrioritySign Sign;
        public PriorityRuleFlags Flags;

        public bool ByPlayer => (Flags & PriorityRuleFlags.Player) != 0;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(Edge);
            writer.Write((byte)Sign);
            writer.Write((byte)Flags);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out Edge);
            reader.Read(out byte sign);
            reader.Read(out byte flags);
            Sign = (PrioritySign)sign;
            Flags = (PriorityRuleFlags)flags;
        }
    }
}
