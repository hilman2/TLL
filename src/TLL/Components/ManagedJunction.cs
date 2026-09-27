using Colossal.Serialization.Entities;
using TLL.Core.Control;
using TLL.Core.Planning;
using Unity.Entities;

namespace TLL.Components
{
    public enum JunctionOrigin : byte
    {
        /// <summary>Taken over by the city-wide automation. The optimiser may change anything.</summary>
        Auto,

        /// <summary>
        /// Configured by the player. The autopilot and the green waves leave
        /// it alone; the optimiser still fits its greens to the traffic while
        /// <see cref="JunctionOptions.AutoTiming"/> is set.
        /// </summary>
        Manual,
    }

    [System.Flags]
    public enum JunctionOptions : byte
    {
        None = 0,

        /// <summary>The optimiser adjusts cycle and greens from measured traffic.</summary>
        AutoTiming = 1,

        // Value 2 was a per-junction flashing switch that nothing read; the
        // setting AutoFlash decides. Left free so old saves stay readable.

        /// <summary>
        /// The plan gets a scramble phase, which runs only while turning
        /// vehicles keep being held up by pedestrians (see PedestrianConflicts).
        /// </summary>
        ScrambleOnDemand = 4,
    }

    /// <summary>
    /// Marks a junction whose signals TLL controls, and holds its settings.
    /// Present on the node entity next to the game's <c>TrafficLights</c>.
    /// The game's own traffic light system skips nodes with this component.
    /// </summary>
    public struct ManagedJunction : IComponentData, ISerializable
    {
        /// <summary>Layout of the saved data. Raise it when fields are added and read old layouts in Deserialize.</summary>
        private const byte kVersion = 1;

        public JunctionOrigin Origin;
        public JunctionOptions Options;
        public ControlMode Mode;
        public PlanStrategy Strategy;

        public byte Yellow;
        public byte AllRed;
        public byte Prepare;

        /// <summary>Cycle start in controller steps, for the timed modes.</summary>
        public int Offset;

        public float SwitchRatio;
        public ushort MaxWait;

        /// <summary>Coordination group, 0 for none. Junctions of a group share their cycle.</summary>
        public int Group;

        /// <summary>The approach whose traffic keeps going while the signals flash. Null lets TLL choose.</summary>
        public Entity MajorApproach;

        public static ManagedJunction Create(JunctionOrigin origin, ControlMode mode, PlanStrategy strategy)
        {
            ControllerConfig defaults = ControllerConfig.Default(mode);
            return new ManagedJunction
            {
                Origin = origin,
                Options = origin == JunctionOrigin.Auto ? JunctionOptions.AutoTiming | JunctionOptions.ScrambleOnDemand : JunctionOptions.None,
                Mode = mode,
                Strategy = strategy,
                Yellow = defaults.Yellow,
                AllRed = defaults.AllRed,
                Prepare = defaults.Prepare,
                SwitchRatio = defaults.SwitchRatio,
                MaxWait = defaults.MaxWait,
            };
        }

        public ControllerConfig ToConfig()
        {
            return new ControllerConfig
            {
                Mode = Mode,
                Yellow = Yellow,
                AllRed = AllRed,
                Prepare = Prepare,
                Offset = Offset,
                SwitchRatio = SwitchRatio,
                MaxWait = MaxWait,
            };
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write((byte)Origin);
            writer.Write((byte)Options);
            writer.Write((byte)Mode);
            writer.Write((byte)Strategy);
            writer.Write(Yellow);
            writer.Write(AllRed);
            writer.Write(Prepare);
            writer.Write(Offset);
            writer.Write(SwitchRatio);
            writer.Write(MaxWait);
            writer.Write(Group);
            writer.Write(MajorApproach);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out byte origin);
            reader.Read(out byte options);
            reader.Read(out byte mode);
            reader.Read(out byte strategy);
            reader.Read(out Yellow);
            reader.Read(out AllRed);
            reader.Read(out Prepare);
            reader.Read(out Offset);
            reader.Read(out SwitchRatio);
            reader.Read(out MaxWait);
            reader.Read(out Group);
            reader.Read(out MajorApproach);
            Origin = (JunctionOrigin)origin;
            Options = (JunctionOptions)options;
            Mode = (ControlMode)mode;
            Strategy = (PlanStrategy)strategy;
        }
    }
}
