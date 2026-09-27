using Colossal.Serialization.Entities;
using TLL.Core.Control;
using TLL.Core.Planning;
using Unity.Entities;

namespace TLL.Components
{
    /// <summary>
    /// One movement of a managed junction: all lanes from one approach to
    /// another, or one crosswalk. Phases refer to movements by their index in
    /// this buffer, so the order is part of the saved plan.
    ///
    /// Approaches are stored as the edge entities, which survive a save. If
    /// the road layout changes, the stored movements no longer match the
    /// lanes, and the plan is rebuilt (automatic junctions) or reported
    /// (manual ones).
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct JunctionMovement : IBufferElementData, ISerializable
    {
        public Entity Source;

        /// <summary>Exit edge. Null for a crosswalk.</summary>
        public Entity Target;

        public MovementKind Kind;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Source);
            writer.Write(Target);
            writer.Write((byte)Kind);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Source);
            reader.Read(out Target);
            reader.Read(out byte kind);
            Kind = (MovementKind)kind;
        }
    }

    /// <summary>
    /// One phase of a managed junction. Phase i drives the game's signal
    /// group i + 1. Only the configuration is saved; sensor readings and
    /// statistics start fresh after loading.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct JunctionPhase : IBufferElementData, ISerializable
    {
        public PhaseData Data;

        /// <summary>Bit i: movement i has green in this phase.</summary>
        public ulong Movements;

        /// <summary>Subset of <see cref="Movements"/> that must give way.</summary>
        public ulong Permitted;

        /// <summary>
        /// Movements with red in this phase that may turn on red, when the
        /// option is on. Derived from the geometry whenever the junction is
        /// built, so it is not saved.
        /// </summary>
        public ulong TurnOnRed;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Data.MinGreen);
            writer.Write(Data.MaxGreen);
            writer.Write(Data.Green);
            writer.Write((byte)Data.Flags);
            writer.Write(Movements);
            writer.Write(Permitted);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Data.MinGreen);
            reader.Read(out Data.MaxGreen);
            reader.Read(out Data.Green);
            reader.Read(out byte flags);
            Data.Flags = (PhaseFlags)flags;
            reader.Read(out Movements);
            reader.Read(out Permitted);
        }
    }

    [System.Flags]
    public enum JunctionLaneFlags : byte
    {
        None = 0,
        Pedestrian = 1,
        Track = 2,

        /// <summary>Part of the major road: keeps going while the signals flash.</summary>
        Major = 4,

        /// <summary>
        /// Controller state, not layout: the exit had no room at the last
        /// check, so the lane is held although its phase has green.
        /// </summary>
        KeepClear = 8,
    }

    /// <summary>
    /// A signalled lane inside a managed junction and the lanes it connects.
    /// Rebuilt from the game's lanes whenever the junction changes, and not
    /// saved: after loading, the missing buffer is what triggers the rebuild.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct JunctionLane : IBufferElementData
    {
        /// <summary>The lane inside the junction that carries the signal.</summary>
        public Entity Lane;

        /// <summary>Lane on the incoming road that feeds it, for counting the queue. May be Null.</summary>
        public Entity Approach;

        /// <summary>Lane on the outgoing road it leads into, for detecting spillback. May be Null.</summary>
        public Entity Exit;

        public byte Movement;

        public JunctionLaneFlags Flags;
    }

    /// <summary>Controller state of a managed junction. Not saved; the controller resynchronises after loading.</summary>
    public struct JunctionRuntime : IComponentData
    {
        public ControllerState State;

        /// <summary>Controller step at which the phase statistics were last reset.</summary>
        public long StatsSince;
    }

    /// <summary>Tag: the junction's plan or lanes must be rebuilt before it runs again.</summary>
    public struct JunctionDirty : IComponentData
    {
    }

    /// <summary>
    /// Tag: the game should rebuild this node, including its vanilla signal
    /// groups. <see cref="Systems.RebuildRequestSystem"/> turns it into the
    /// game's Updated tag at the start of the next modification pass.
    /// </summary>
    public struct RebuildRequest : IComponentData
    {
    }
}
