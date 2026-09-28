using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TLL.Components
{
    /// <summary>
    /// A road of a managed junction as it was when the junction's plan was
    /// last built, with its direction. When a road is rebuilt, its entity is
    /// gone with it; the direction is what lets the player's plan find the
    /// road again (PlanTransfer.MatchArms). Written by every set-up.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct JunctionArm : IBufferElementData, ISerializable
    {
        /// <summary>Layout of the saved data. Raise it when fields are added and read old layouts in Deserialize.</summary>
        private const byte kVersion = 1;

        public Entity Edge;

        /// <summary>Direction from the junction out along the road, in degrees counter-clockwise (NetGeometry.ApproachAngles).</summary>
        public float Angle;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(Edge);
            writer.Write(Angle);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out Edge);
            reader.Read(out Angle);
        }
    }
}
