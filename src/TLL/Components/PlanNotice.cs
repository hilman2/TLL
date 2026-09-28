using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TLL.Components
{
    public enum PlanNoticeKind : byte
    {
        /// <summary>The player's plan was carried over to the changed roads (PlanTransfer), with changes.</summary>
        Adapted = 1,

        /// <summary>The player's plan could not be carried over and was replaced by a generated one.</summary>
        Replaced = 2,
    }

    /// <summary>
    /// Something TLL did to a plan of the player's on its own, after the
    /// junction's roads changed. The panel shows it until the player has
    /// looked; saved, so it survives until then.
    /// </summary>
    public struct PlanNotice : IComponentData, ISerializable
    {
        /// <summary>Layout of the saved data. Raise it when fields are added and read old layouts in Deserialize.</summary>
        private const byte kVersion = 1;

        public PlanNoticeKind Kind;

        /// <summary>Movements new to the plan, movements that changed phase, and movements gone with a road.</summary>
        public byte Added;
        public byte Moved;
        public byte Dropped;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write((byte)Kind);
            writer.Write(Added);
            writer.Write(Moved);
            writer.Write(Dropped);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out byte kind);
            Kind = (PlanNoticeKind)kind;
            reader.Read(out Added);
            reader.Read(out Moved);
            reader.Read(out Dropped);
        }
    }
}
