namespace TLL.Core.Control
{
    public enum PhaseFlags : byte
    {
        None = 0,

        /// <summary>
        /// The phase that carries a green wave. In coordinated mode it is never
        /// skipped and holds its window; it ends early only when nobody is on
        /// it or coming. Time a skipped or short side phase leaves goes to the
        /// phase after it in the schedule.
        /// </summary>
        Coordinated = 1,

        /// <summary>The phase contains a crosswalk. Its minimum green covers the walk.</summary>
        Pedestrian = 2,

        /// <summary>
        /// A phase of crosswalks only, run on demand: it is asked for only
        /// while the controller diverts pedestrians into it
        /// (<see cref="ControllerConfig.DivertPedestrians"/>).
        /// </summary>
        Scramble = 4,
    }

    /// <summary>
    /// Configuration, sensor readings and running counters of one phase.
    /// Durations are in controller steps (see <see cref="SimTime"/>).
    /// </summary>
    public struct PhaseData
    {
        // Configuration.

        public ushort MinGreen;

        /// <summary>Upper bound for the actuated and adaptive modes.</summary>
        public ushort MaxGreen;

        /// <summary>Planned green in the fixed-time and coordinated modes.</summary>
        public ushort Green;

        /// <summary>
        /// Shortest green once pedestrians walk in this phase, so they can
        /// cross. Without a pedestrian call the phase only needs
        /// <see cref="MinGreen"/>. Zero means the same as MinGreen.
        /// </summary>
        public ushort WalkGreen;

        public PhaseFlags Flags;

        // Sensor readings, written by the caller before every step.

        /// <summary>Weighted number of road users waiting for or approaching this phase. 0 means nobody.</summary>
        public float Demand;

        /// <summary>Vehicles standing in the queue of this phase's lanes, unweighted; the part of <see cref="Demand"/> already stopped.</summary>
        public float Queue;

        /// <summary>
        /// Demand minus the congestion downstream. A phase whose exits are
        /// jammed has low pressure even with a long queue, because green would
        /// move nobody. Used by the adaptive mode.
        /// </summary>
        public float Pressure;

        /// <summary>
        /// Vehicles still on their way that reach the stop line within a few
        /// seconds, beyond those already counted in <see cref="Demand"/>. The
        /// adaptive mode may hold a green for them (see SignalController).
        /// </summary>
        public float Approaching;

        /// <summary>Traffic is moving through the junction on this phase's lanes right now.</summary>
        public bool Busy;

        /// <summary>
        /// Every movement of the phase with a queue leads into an exit backed
        /// up to the junction: green would move nobody. Ends a green in the
        /// drain mode.
        /// </summary>
        public bool Blocked;

        /// <summary>An emergency vehicle is asking for this phase.</summary>
        public bool Preempt;

        /// <summary>Someone waits to cross on one of this phase's crosswalks: the push button.</summary>
        public bool PedestrianCall;

        // Kept by the controller.

        /// <summary>Steps this phase has had demand without green. Reset when it gets green.</summary>
        public ushort WaitSteps;

        /// <summary>Statistics for the optimiser. The optimiser resets them after reading.</summary>
        public PhaseStatistics Stats;

        public bool HasFlag(PhaseFlags flag)
        {
            return (Flags & flag) != 0;
        }

        /// <summary>
        /// Takes over what the junction has learnt for the same phase in its
        /// previous plan, when a new plan gives green to exactly the same
        /// movements.
        /// </summary>
        public void KeepFrom(in PhaseData old)
        {
            Green = old.Green;
            MaxGreen = old.MaxGreen;
            // Whether the phase carries a green wave is learnt; what it is
            // comes from the new plan. The scramble on demand of one layout
            // and the pedestrian phase of the scramble layout have the same
            // crosswalks, and a copied Scramble flag made the latter wait for
            // a diversion that never comes there: pedestrians never crossed.
            Flags = (Flags & ~PhaseFlags.Coordinated) | (old.Flags & PhaseFlags.Coordinated);
        }

        /// <summary>
        /// Least green a timed plan gives the phase: long enough for the walk
        /// if it has a crosswalk, since the fixed schedule cannot stretch a
        /// green when a pedestrian calls.
        /// </summary>
        public int PlannedMinimum => HasFlag(PhaseFlags.Pedestrian) && WalkGreen > MinGreen ? WalkGreen : MinGreen;
    }

    public struct PhaseStatistics
    {
        /// <summary>Steps of green.</summary>
        public uint GreenSteps;

        /// <summary>Steps of green during which traffic actually moved.</summary>
        public uint BusySteps;

        /// <summary>Greens that ended at the maximum while demand remained.</summary>
        public uint MaxOuts;

        /// <summary>Greens that ended because demand ran out.</summary>
        public uint GapOuts;

        /// <summary>Number of greens.</summary>
        public uint Greens;

        public void Clear()
        {
            this = default;
        }
    }

    /// <summary>
    /// Gives the controller access to the phases of one junction.
    /// The mod implements it over an ECS buffer, tests over an array. Being
    /// a struct constraint keeps it usable from Burst-compiled jobs.
    /// </summary>
    public interface IPhaseAccess
    {
        int Count { get; }

        ref PhaseData this[int index] { get; }
    }

    /// <summary><see cref="IPhaseAccess"/> over a plain array.</summary>
    public struct PhaseArray : IPhaseAccess
    {
        private readonly PhaseData[] m_Phases;

        public PhaseArray(PhaseData[] phases)
        {
            m_Phases = phases;
        }

        public int Count => m_Phases.Length;

        public ref PhaseData this[int index] => ref m_Phases[index];
    }
}
