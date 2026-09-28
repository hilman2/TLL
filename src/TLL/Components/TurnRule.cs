using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TLL.Components
{
    [System.Flags]
    public enum TurnRuleFlags : byte
    {
        None = 0,

        /// <summary>Vehicles may not take this turn; the route search avoids it unless there is no other way.</summary>
        Forbidden = 1,

        /// <summary>
        /// The player set the rule, forbidden or allowed. The autopilot
        /// leaves such a turn alone.
        /// </summary>
        Player = 2,
    }

    /// <summary>
    /// A rule for one way through a junction: from one road into another,
    /// the same road for a U-turn. On the node entity. The game builds the
    /// junction's lanes anew whenever something about it changes, and
    /// LaneRuleSystem applies the rules to them every time.
    /// </summary>
    /// <remarks>
    /// A forbidden turn keeps its lanes, flagged the way the game flags the
    /// turns its own road upgrades forbid: the route search gives them a
    /// high cost, and a vehicle with no other way still gets through.
    /// </remarks>
    [InternalBufferCapacity(0)]
    public struct TurnRule : IBufferElementData, ISerializable
    {
        /// <summary>Layout of the saved data. Raise it when fields are added and read old layouts in Deserialize.</summary>
        private const byte kVersion = 1;

        public Entity From;
        public Entity To;
        public TurnRuleFlags Flags;

        /// <summary>
        /// Vehicles per hour the turn carried when it was forbidden: those
        /// that would come back if it were allowed again, and whose detour
        /// the autopilot weighs (TurnAdvisor).
        /// </summary>
        public float Volume;

        public bool Forbidden => (Flags & TurnRuleFlags.Forbidden) != 0;

        public bool ByPlayer => (Flags & TurnRuleFlags.Player) != 0;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(From);
            writer.Write(To);
            writer.Write((byte)Flags);
            writer.Write(Volume);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out From);
            reader.Read(out To);
            reader.Read(out byte flags);
            reader.Read(out Volume);
            Flags = (TurnRuleFlags)flags;
        }
    }
}
