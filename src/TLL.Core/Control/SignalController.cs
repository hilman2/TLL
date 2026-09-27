namespace TLL.Core.Control
{
    public enum ControlMode : byte
    {
        /// <summary>Fixed greens on a fixed cycle.</summary>
        FixedTime,

        /// <summary>Greens between minimum and maximum, ended when demand runs out. Phases without demand are skipped.</summary>
        Actuated,

        /// <summary>Like actuated, but the next phase is the one with the highest pressure, not the next in order.</summary>
        Adaptive,

        /// <summary>Fixed cycle and offset shared with neighbours; non-coordinated phases may end early.</summary>
        Coordinated,

        /// <summary>Signals off: main road has priority, side roads yield.</summary>
        Flashing,
    }

    public enum Stage : byte
    {
        Green,

        /// <summary>Movements losing green see yellow.</summary>
        Yellow,

        /// <summary>Everyone losing green has red, nobody new has green yet.</summary>
        AllRed,

        /// <summary>Movements about to get green may start to roll.</summary>
        Prepare,

        Flashing,
    }

    public struct ControllerConfig
    {
        public ControlMode Mode;

        /// <summary>Steps of the transition stages. Zero skips a stage.</summary>
        public byte Yellow;

        public byte AllRed;

        public byte Prepare;

        /// <summary>Cycle start relative to the global step count, for the fixed-time and coordinated modes.</summary>
        public int Offset;

        /// <summary>
        /// Adaptive mode: a waiting phase takes over from a phase that still
        /// has demand only if its pressure is this many times higher.
        /// </summary>
        public float SwitchRatio;

        /// <summary>Adaptive mode: after this many steps of waiting a phase is served regardless of pressure.</summary>
        public ushort MaxWait;

        /// <summary>
        /// Pedestrians cross in the scramble phase instead of alongside the
        /// vehicles: set while turning vehicles keep being held up by people
        /// on the crosswalks. Without a scramble phase it has no effect.
        /// </summary>
        public bool DivertPedestrians;

        public static ControllerConfig Default(ControlMode mode)
        {
            return new ControllerConfig
            {
                Mode = mode,
                Yellow = (byte)SimTime.ToSteps(3f),
                AllRed = (byte)SimTime.ToSteps(1f),
                Prepare = 1,
                SwitchRatio = 1.5f,
                MaxWait = (ushort)SimTime.ToSteps(120f),
            };
        }

        public int Intergreen => Yellow + AllRed + Prepare;
    }

    /// <summary>State of one junction's controller. Plain data, saved with the junction.</summary>
    public struct ControllerState
    {
        public Stage Stage;

        /// <summary>Phase that has green, or had it last during a transition.</summary>
        public byte Phase;

        /// <summary>Phase that gets green at the end of the transition.</summary>
        public byte Next;

        /// <summary>Steps spent in the current stage.</summary>
        public ushort StageSteps;

        /// <summary>Fixed-time and coordinated: steps of green left before the phase is forced off.</summary>
        public ushort GreenLeft;

        /// <summary>Set while an emergency vehicle holds the current phase.</summary>
        public bool Preempting;

        /// <summary>Set after the first step, so a fresh controller can pick its start phase.</summary>
        public bool Started;

        /// <summary>
        /// The crosswalks of the green phase show walk. Only with a
        /// pedestrian call, except in fixed-time mode, where pedestrians
        /// walk in every cycle.
        /// </summary>
        public bool Walk;

        /// <summary>Value of StageSteps when the walk began; a call during the green starts it late.</summary>
        public ushort WalkSince;
    }

    /// <summary>
    /// Advances a junction's controller by one step.
    ///
    /// The timed modes derive everything from the global step count and the
    /// junction's offset, not from how long the controller has been running.
    /// A controller that starts late, loses steps, or was just switched from
    /// another mode therefore falls into its schedule within one cycle, and
    /// junctions with the same cycle keep their offsets to each other without
    /// any exchange between them.
    /// </summary>
    public static class SignalController
    {
        /// <returns>True if the signals shown change with this step.</returns>
        public static bool Step<TPhases>(ref ControllerState s, in ControllerConfig config, ref TPhases phases, long globalStep)
            where TPhases : struct, IPhaseAccess
        {
            // Diverting pedestrians needs a phase to divert them into; below,
            // the flag means both.
            ControllerConfig c = config;
            c.DivertPedestrians = config.DivertPedestrians && HasScramble(ref phases);
            return StepWith(ref s, in c, ref phases, globalStep);
        }

        private static bool HasScramble<TPhases>(ref TPhases phases)
            where TPhases : struct, IPhaseAccess
        {
            for (int i = 0; i < phases.Count; i++)
            {
                if (phases[i].HasFlag(PhaseFlags.Scramble))
                    return true;
            }
            return false;
        }

        private static bool StepWith<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases, long globalStep)
            where TPhases : struct, IPhaseAccess
        {
            int count = phases.Count;
            if (count == 0)
                return false;

            Stage stageBefore = s.Stage;
            byte phaseBefore = s.Phase;
            byte nextBefore = s.Next;

            if (s.Phase >= count)
                s.Phase = 0;
            if (s.Next >= count)
                s.Next = 0;

            UpdateWaiting(ref s, in c, ref phases);

            if (c.Mode == ControlMode.Flashing)
            {
                s.Stage = Stage.Flashing;
                s.StageSteps = 0;
                s.Started = true;
                return stageBefore != Stage.Flashing;
            }
            if (s.Stage == Stage.Flashing)
            {
                // Leaving flashing operation: everybody stops before the first green.
                s.Next = (byte)FirstPhase(in c, ref phases, globalStep);
                s.Stage = Stage.AllRed;
                s.StageSteps = 0;
                s.Started = true;
                return true;
            }
            if (!s.Started)
            {
                s.Started = true;
                s.Phase = (byte)FirstPhase(in c, ref phases, globalStep);
                EnterGreen(ref s, in c, ref phases, s.Phase, globalStep);
                return true;
            }

            s.StageSteps++;
            switch (s.Stage)
            {
                case Stage.Green:
                    StepGreen(ref s, in c, ref phases, globalStep);
                    break;
                case Stage.Yellow:
                    if (s.StageSteps >= c.Yellow)
                        AfterYellow(ref s, in c, ref phases, globalStep);
                    break;
                case Stage.AllRed:
                    if (s.StageSteps >= c.AllRed)
                        AfterAllRed(ref s, in c, ref phases, globalStep);
                    break;
                case Stage.Prepare:
                    if (s.StageSteps >= c.Prepare)
                        EnterGreen(ref s, in c, ref phases, s.Next, globalStep);
                    break;
            }

            return s.Stage != stageBefore || s.Phase != phaseBefore || s.Next != nextBefore;
        }

        private static void UpdateWaiting<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases)
            where TPhases : struct, IPhaseAccess
        {
            for (int i = 0; i < phases.Count; i++)
            {
                ref PhaseData p = ref phases[i];
                bool served = s.Stage == Stage.Green && s.Phase == i;
                if (served || !Requested(in c, ref p))
                    p.WaitSteps = 0;
                else if (p.WaitSteps < ushort.MaxValue)
                    p.WaitSteps++;
            }
        }

        private static void StepGreen<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases, long globalStep)
            where TPhases : struct, IPhaseAccess
        {
            ref PhaseData current = ref phases[s.Phase];
            current.Stats.GreenSteps++;
            if (current.Busy)
                current.Stats.BusySteps++;
            if (s.GreenLeft > 0)
                s.GreenLeft--;

            LateWalk(ref s, in c, ref phases);
            bool pastMin = s.StageSteps >= current.MinGreen && (!s.Walk || s.StageSteps - s.WalkSince >= WalkGreenOf(ref current));

            if (s.Preempting)
            {
                if (current.Preempt || !pastMin)
                    return;
                s.Preempting = false;
            }

            int preempt = PreemptingPhase(ref phases, s.Phase);
            if (preempt >= 0 && pastMin)
            {
                BeginTransition(ref s, in c, ref phases, preempt, globalStep);
                return;
            }

            int next = -1;
            switch (c.Mode)
            {
                case ControlMode.FixedTime:
                case ControlMode.Coordinated:
                    next = TimedDecision(ref s, in c, ref phases, globalStep, pastMin);
                    break;
                case ControlMode.Actuated:
                    next = ActuatedDecision(ref s, in c, ref phases, pastMin);
                    break;
                case ControlMode.Adaptive:
                    next = AdaptiveDecision(ref s, in c, ref phases, pastMin);
                    break;
            }
            if (next >= 0 && next != s.Phase)
                BeginTransition(ref s, in c, ref phases, next, globalStep);
        }

        /// <summary>Fixed-time and coordinated: returns the phase to switch to, or -1 to stay.</summary>
        private static int TimedDecision<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases, long globalStep, bool pastMin)
            where TPhases : struct, IPhaseAccess
        {
            ref PhaseData current = ref phases[s.Phase];
            bool forcedOff = s.GreenLeft == 0 && pastMin;
            bool gapOut = c.Mode == ControlMode.Coordinated
                && !current.HasFlag(PhaseFlags.Coordinated)
                && pastMin
                && current.Demand <= 0f;

            if (!forcedOff && !gapOut)
                return -1;
            if (gapOut && !forcedOff)
                current.Stats.GapOuts++;

            // Choose among the phases whose green is still ahead, starting the
            // search where the transition would end.
            int next = NextTimedPhase(in c, ref phases, globalStep + c.Intergreen, s.Phase);
            return next == s.Phase ? -1 : next;
        }

        private static int ActuatedDecision<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases, bool pastMin)
            where TPhases : struct, IPhaseAccess
        {
            ref PhaseData current = ref phases[s.Phase];
            if (!pastMin)
                return -1;
            bool maxedOut = s.StageSteps >= current.MaxGreen;
            bool gappedOut = current.Demand <= 0f;
            if (!maxedOut && !gappedOut)
                return -1;

            int count = phases.Count;
            for (int k = 1; k < count; k++)
            {
                int candidate = (s.Phase + k) % count;
                if (Requested(in c, ref phases[candidate]))
                {
                    CountEnd(ref current, maxedOut, gappedOut);
                    return candidate;
                }
            }
            // Nobody else wants green: rest in the current phase.
            return -1;
        }

        private static int AdaptiveDecision<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases, bool pastMin)
            where TPhases : struct, IPhaseAccess
        {
            ref PhaseData current = ref phases[s.Phase];
            if (!pastMin)
                return -1;

            int best = -1;
            float bestScore = float.MinValue;
            bool bestStarved = false;
            for (int i = 0; i < phases.Count; i++)
            {
                if (i == s.Phase)
                    continue;
                ref PhaseData p = ref phases[i];
                if (!Requested(in c, ref p))
                    continue;
                bool starved = p.WaitSteps >= c.MaxWait;
                // A starved phase beats any unstarved one; among equals the
                // pressure decides, and among starved ones the longest wait.
                float score = starved ? p.WaitSteps : p.Pressure;
                if ((starved && !bestStarved) || (starved == bestStarved && score > bestScore))
                {
                    best = i;
                    bestScore = score;
                    bestStarved = starved;
                }
            }
            if (best < 0)
                return -1;

            bool maxedOut = s.StageSteps >= current.MaxGreen;
            bool gappedOut = current.Demand <= 0f;
            bool outweighed = !bestStarved && phases[best].Pressure > current.Pressure * c.SwitchRatio && phases[best].Pressure > current.Pressure + 1f;
            if (!(maxedOut || gappedOut || bestStarved || outweighed))
                return -1;
            if (!maxedOut && !bestStarved && HoldForPlatoon(ref current, ref phases[best]))
                return -1;
            CountEnd(ref current, maxedOut, gappedOut);
            return best;
        }

        /// <summary>
        /// Whether anyone asks for the phase: vehicles, or a pedestrian at
        /// the push button. A call asks for the green but does not keep it
        /// going; that is what the walk time does once the walk has begun.
        /// </summary>
        private static bool Requested(in ControllerConfig c, ref PhaseData phase)
        {
            return phase.Demand > 0f || CallCounts(in c, ref phase);
        }

        /// <summary>
        /// Whether the phase's pedestrian call is answered in this phase: in
        /// the scramble phase only while pedestrians are diverted there, in
        /// the others only while they are not.
        /// </summary>
        private static bool CallCounts(in ControllerConfig c, ref PhaseData phase)
        {
            return phase.PedestrianCall && phase.HasFlag(PhaseFlags.Scramble) == c.DivertPedestrians;
        }

        private static bool OthersRequested<TPhases>(in ControllerConfig c, ref TPhases phases, int current)
            where TPhases : struct, IPhaseAccess
        {
            for (int i = 0; i < phases.Count; i++)
            {
                if (i != current && Requested(in c, ref phases[i]))
                    return true;
            }
            return false;
        }

        private static int WalkGreenOf(ref PhaseData phase)
        {
            return phase.WalkGreen > phase.MinGreen ? phase.WalkGreen : phase.MinGreen;
        }

        /// <summary>
        /// A pedestrian call during a green that started without one: the
        /// walk starts now if the green can still last the walk time, within
        /// its maximum (or, in the timed modes, before its force-off), or if
        /// the green rests because nobody else asks. Otherwise the pedestrian
        /// waits for the next green of the phase.
        /// </summary>
        private static void LateWalk<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases)
            where TPhases : struct, IPhaseAccess
        {
            ref PhaseData current = ref phases[s.Phase];
            if (s.Walk || !CallCounts(in c, ref current) || !current.HasFlag(PhaseFlags.Pedestrian))
                return;
            int walk = WalkGreenOf(ref current);
            bool timed = c.Mode == ControlMode.FixedTime || c.Mode == ControlMode.Coordinated;
            bool room = timed ? s.GreenLeft >= walk : s.StageSteps + walk <= current.MaxGreen || !OthersRequested(in c, ref phases, s.Phase);
            if (!room)
                return;
            s.Walk = true;
            s.WalkSince = s.StageSteps;
        }

        /// <summary>Fewest vehicles on their way that are worth holding a green for.</summary>
        public const float PlatoonSize = 2f;

        /// <summary>
        /// Adaptive mode: keep the green for a group of vehicles about to
        /// arrive, when they outnumber the ones the switch would serve.
        /// Switching now would stop the group for a whole cycle while the
        /// others gain only the few seconds the group takes to pass. Maximum
        /// green and the maximum wait still end the hold.
        /// </summary>
        private static bool HoldForPlatoon(ref PhaseData current, ref PhaseData contender)
        {
            return current.Approaching >= PlatoonSize && current.Approaching > contender.Demand;
        }

        private static void CountEnd(ref PhaseData phase, bool maxedOut, bool gappedOut)
        {
            if (gappedOut)
                phase.Stats.GapOuts++;
            else if (maxedOut)
                phase.Stats.MaxOuts++;
        }

        private static int PreemptingPhase<TPhases>(ref TPhases phases, int current)
            where TPhases : struct, IPhaseAccess
        {
            if (phases[current].Preempt)
                return -1;
            for (int i = 0; i < phases.Count; i++)
            {
                if (i != current && phases[i].Preempt)
                    return i;
            }
            return -1;
        }

        private static void BeginTransition<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases, int next, long globalStep)
            where TPhases : struct, IPhaseAccess
        {
            s.Next = (byte)next;
            s.StageSteps = 0;
            if (c.Yellow > 0)
                s.Stage = Stage.Yellow;
            else if (c.AllRed > 0)
                s.Stage = Stage.AllRed;
            else if (c.Prepare > 0)
                s.Stage = Stage.Prepare;
            else
                EnterGreen(ref s, in c, ref phases, next, globalStep);
        }

        private static void AfterYellow<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases, long globalStep)
            where TPhases : struct, IPhaseAccess
        {
            s.StageSteps = 0;
            if (c.AllRed > 0)
                s.Stage = Stage.AllRed;
            else
                AfterAllRed(ref s, in c, ref phases, globalStep);
        }

        private static void AfterAllRed<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases, long globalStep)
            where TPhases : struct, IPhaseAccess
        {
            s.StageSteps = 0;
            if (c.Prepare > 0)
                s.Stage = Stage.Prepare;
            else
                EnterGreen(ref s, in c, ref phases, s.Next, globalStep);
        }

        private static void EnterGreen<TPhases>(ref ControllerState s, in ControllerConfig c, ref TPhases phases, int phase, long globalStep)
            where TPhases : struct, IPhaseAccess
        {
            s.Stage = Stage.Green;
            s.Phase = (byte)phase;
            s.Next = (byte)phase;
            s.StageSteps = 0;
            ref PhaseData p = ref phases[phase];
            p.Stats.Greens++;
            p.WaitSteps = 0;
            s.Preempting = p.Preempt;
            // Push button: pedestrians walk only when someone asked, except
            // in fixed-time mode, which serves them in every cycle.
            s.Walk = p.HasFlag(PhaseFlags.Pedestrian) && (c.Mode == ControlMode.FixedTime || CallCounts(in c, ref p));
            s.WalkSince = 0;
            s.GreenLeft = 0;
            if (c.Mode == ControlMode.FixedTime || c.Mode == ControlMode.Coordinated)
            {
                int cycle = CycleOf(in c, ref phases);
                int t = SimTime.Mod(globalStep - c.Offset, cycle);
                s.GreenLeft = (ushort)SimTime.Mod(EndOf(in c, ref phases, phase) - t, cycle);
            }
        }

        /// <summary>Phase to start with: the scheduled one for timed modes, else the first with demand.</summary>
        private static int FirstPhase<TPhases>(in ControllerConfig c, ref TPhases phases, long globalStep)
            where TPhases : struct, IPhaseAccess
        {
            if (c.Mode == ControlMode.FixedTime || c.Mode == ControlMode.Coordinated)
                return NextTimedPhase(in c, ref phases, globalStep, -1);
            for (int i = 0; i < phases.Count; i++)
            {
                if (Requested(in c, ref phases[i]))
                    return i;
            }
            return 0;
        }

        /// <summary>
        /// The phase to give green at <paramref name="atStep"/> in the timed
        /// modes: the first phase in schedule order, counted from the next
        /// green to end, that still has room for its minimum green before its
        /// force-off. In coordinated mode, phases without demand are skipped,
        /// but the coordinated phase never is.
        /// </summary>
        private static int NextTimedPhase<TPhases>(in ControllerConfig c, ref TPhases phases, long atStep, int current)
            where TPhases : struct, IPhaseAccess
        {
            int cycle = CycleOf(in c, ref phases);
            int count = phases.Count;
            int t = SimTime.Mod(atStep - c.Offset, cycle);

            int first = 0;
            int firstDistance = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int d = SimTime.Mod(EndOf(in c, ref phases, i) - t, cycle);
                if (d < firstDistance)
                {
                    firstDistance = d;
                    first = i;
                }
            }

            for (int k = 0; k < count; k++)
            {
                int i = (first + k) % count;
                if (i == current)
                    continue;
                ref PhaseData p = ref phases[i];
                bool coordinated = p.HasFlag(PhaseFlags.Coordinated);
                if (c.Mode == ControlMode.Coordinated && !coordinated && !Requested(in c, ref p))
                    continue;
                int room = SimTime.Mod(EndOf(in c, ref phases, i) - t, cycle);
                if (room >= p.MinGreen || coordinated)
                    return i;
            }
            return current >= 0 ? current : first;
        }

        // Green windows of the timed modes within one cycle: phase i's green
        // starts after the greens and intergreens of all phases before it, and
        // the cycle is the sum of all greens and intergreens. The offset in
        // the config says at which global step the cycle starts.

        public static int CycleOf<TPhases>(in ControllerConfig c, ref TPhases phases)
            where TPhases : struct, IPhaseAccess
        {
            int cycle = 0;
            for (int i = 0; i < phases.Count; i++)
                cycle += phases[i].Green + c.Intergreen;
            return cycle < 1 ? 1 : cycle;
        }

        public static int StartOf<TPhases>(in ControllerConfig c, ref TPhases phases, int phase)
            where TPhases : struct, IPhaseAccess
        {
            int start = 0;
            for (int i = 0; i < phase; i++)
                start += phases[i].Green + c.Intergreen;
            return start;
        }

        /// <summary>Cycle position at which the phase's green is forced off.</summary>
        public static int EndOf<TPhases>(in ControllerConfig c, ref TPhases phases, int phase)
            where TPhases : struct, IPhaseAccess
        {
            return StartOf(in c, ref phases, phase) + phases[phase].Green;
        }
    }
}
