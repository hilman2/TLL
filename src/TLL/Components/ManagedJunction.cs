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
        // setting AutoFlash decides. Value 4 was "scramble on demand", a
        // scramble phase switched on by a count of turning vehicles held up
        // by people; the scramble is a switch of its own now (Scramble), and
        // loading clears 4. Both are left free so old saves stay readable.

        /// <summary>
        /// Short turns may go on red where they meet only traffic they give
        /// way to (PhasePlanner.TurnOnRed). The autopilot sets it where its
        /// estimate says it saves time, if the setting TurnOnRed allows it.
        /// </summary>
        TurnOnRed = 8,

        /// <summary>
        /// Pedestrians get a phase of their own, in all directions, and walk
        /// in no other; the vehicle phases are planned without crosswalks
        /// (PhasePlanner.Build). At automatic junctions the autopilot sets it
        /// where its estimate and the junction's measurements say it saves
        /// time. The bit meant "pedestrians diverted into the scramble on
        /// demand" before, which is the same.
        /// </summary>
        Scramble = 16,
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

        /// <param name="scramble">Pedestrians get a phase of their own (JunctionOptions.Scramble).</param>
        public static ManagedJunction Create(JunctionOrigin origin, ControlMode mode, PlanStrategy strategy, bool scramble = false)
        {
            ControllerConfig defaults = ControllerConfig.Default(mode);
            return new ManagedJunction
            {
                Origin = origin,
                Options = (origin == JunctionOrigin.Auto ? JunctionOptions.AutoTiming : JunctionOptions.None)
                    | (scramble ? JunctionOptions.Scramble : JunctionOptions.None),
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
            // "Scramble on demand" (4) is gone; see JunctionOptions.
            Options = (JunctionOptions)(options & ~4);
            Mode = (ControlMode)mode;
            Strategy = (PlanStrategy)strategy;
            // The pedestrian scramble layout is the permissive one with a scramble.
            if (Strategy == PlanStrategy.ExclusivePedestrian)
            {
                Strategy = PlanStrategy.Permissive;
                Options |= JunctionOptions.Scramble;
            }
        }
    }
}
