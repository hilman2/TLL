using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using TLL.Components;
using TLL.Core;
using TLL.Core.Control;
using TLL.Core.Planning;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Stage = TLL.Core.Control.Stage;

namespace TLL.Systems
{
    /// <summary>
    /// Runs the controllers of all managed junctions, once per controller
    /// step (every 16 simulation frames).
    ///
    /// Each step measures demand, advances the controller, and translates its
    /// stage into the game's own signal states, so vehicles, pedestrians and
    /// signal poles behave exactly as at a vanilla junction.
    /// </summary>
    public partial class SignalControlSystem : TllSystemBase
    {
        /// <summary>
        /// Reach of the approach zone before the stop line, in metres, as a
        /// long-range radar at a real junction. It follows the road across
        /// plain nodes (see <see cref="DetectorLane"/>) but never beyond the
        /// previous junction, so what it sees is what a detector at the start
        /// of the link would count.
        /// </summary>
        internal const float kDetectionRange = 250f;

        /// <summary>
        /// Passage time in seconds: a vehicle this close to the line keeps
        /// the green, as the gap setting of an actuated controller.
        /// </summary>
        private const float kPassageTime = 3f;

        /// <summary>Planning horizon in seconds: arrivals within it count towards pressure.</summary>
        private const float kHorizon = 15f;

        /// <summary>
        /// Arrivals within this many seconds may hold a green in the adaptive
        /// mode (see PhaseData.Approaching): about what one more phase change
        /// would cost them in yellow and all-red.
        /// </summary>
        private const float kHoldHorizon = 8f;

        /// <summary>Road length a queued vehicle takes up, including the gap to the next one.</summary>
        private const float kVehicleSpacing = 7f;

        /// <summary>Exit lane fill above which traffic sent there would only block the junction.</summary>
        private const float kBlockedOccupancy = 0.85f;

        /// <summary>The game's vehicles ask for green with priority 100, emergency vehicles with 108.</summary>
        private const int kEmergencyPriority = 108;

        /// <summary>
        /// Keep clear: an exit whose last vehicle stands within this distance
        /// of the lane start has no room for one more car.
        /// </summary>
        private const float kKeepClearGap = kVehicleSpacing * 1.2f;

        /// <summary>A held lane is released once its exit has this much room, so it does not flicker.</summary>
        private const float kKeepClearRelease = kVehicleSpacing * 2.5f;

        /// <summary>Below this speed in m/s a vehicle counts as standing.</summary>
        private const float kStandingSpeed = 1.5f;

        /// <summary>Turn on red: a vehicle this close to the end of its lane is at the stop line.</summary>
        private const float kStopLineReach = 12f;

        /// <summary>Turn on red: below this speed in m/s the vehicle has come to a stop.</summary>
        private const float kStoppedSpeed = 0.5f;

        private SimulationSystem m_Simulation;
        private CityConfigurationSystem m_CityConfiguration;
        private EntityQuery m_Query;

        public bool Available { get; private set; }

        /// <summary>Name of another mod that drives traffic lights and made TLL stand back, or null.</summary>
        public string Conflict { get; private set; }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (!Available || !mode.IsGame())
                return;
            try
            {
                // Checked when a city loads, when every mod has registered
                // its systems. A mod that replaced the game's query after TLL
                // counts as a conflict even if it is not known by name.
                string conflict = ModConflicts.Find(World);
                if (conflict == null && !VanillaBypass.IsIntact(World))
                    conflict = "?";
                if (conflict == null)
                    return;
                StandBack(conflict);
            }
            catch (System.Exception e)
            {
                Mod.Log.Error(e, "Checking for other traffic light mods failed.");
            }
        }

        /// <summary>
        /// Leaves every junction to the game and the other mod for the rest
        /// of the session. Managed junctions keep their TLL data in the save,
        /// so they come back once the other mod is removed; until then their
        /// signal groups are rebuilt by whoever controls them now.
        /// </summary>
        private void StandBack(string conflict)
        {
            Conflict = conflict;
            Mod.Log.Warn($"{conflict} drives traffic lights as well. TLL stays out of the way while it is loaded.");
            VanillaBypass.Undo(World);
            Available = false;
            Enabled = false;
            EntityQuery managed = GetEntityQuery(ComponentType.ReadOnly<ManagedJunction>());
            EntityManager.AddComponent<RebuildRequest>(managed);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return SimTime.FramesPerStep;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_CityConfiguration = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ManagedJunction>(),
                    ComponentType.ReadWrite<JunctionRuntime>(),
                    ComponentType.ReadWrite<TrafficLights>(),
                    ComponentType.ReadWrite<JunctionPhase>(),
                    ComponentType.ReadWrite<JunctionLane>(),
                    ComponentType.ReadOnly<JunctionMovement>(),
                    ComponentType.ReadWrite<MovementCounter>(),
                    ComponentType.ReadOnly<MovementStatistics>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<JunctionDirty>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Destroyed>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            RequireForUpdate(m_Query);

            Available = VanillaBypass.Apply(World);
            Enabled = Available;
        }

        protected override void OnSafeUpdate()
        {
            var job = new ControlJob
            {
                EntityType = GetEntityTypeHandle(),
                JunctionType = GetComponentTypeHandle<ManagedJunction>(true),
                RuntimeType = GetComponentTypeHandle<JunctionRuntime>(false),
                LightsType = GetComponentTypeHandle<TrafficLights>(false),
                PhaseType = GetBufferTypeHandle<JunctionPhase>(false),
                LaneType = GetBufferTypeHandle<JunctionLane>(false),
                MovementType = GetBufferTypeHandle<JunctionMovement>(true),
                CounterType = GetBufferTypeHandle<MovementCounter>(false),
                StatisticsType = GetBufferTypeHandle<MovementStatistics>(true),
                DetectorType = GetBufferTypeHandle<DetectorLane>(true),
                SubObjectType = GetBufferTypeHandle<Game.Objects.SubObject>(true),
                LaneSignals = GetComponentLookup<LaneSignal>(false),
                Poles = GetComponentLookup<Game.Objects.TrafficLight>(false),
                LaneObjects = GetBufferLookup<LaneObject>(true),
                Curves = GetComponentLookup<Curve>(true),
                Movings = GetComponentLookup<Game.Objects.Moving>(true),
                GlobalStep = SimTime.StepOfFrame(m_Simulation.frameIndex),
                LeftHandTraffic = m_CityConfiguration.leftHandTraffic,
                TurnOnRed = Mod.Settings != null && Mod.Settings.TurnOnRed,
                KeepClear = Mod.Settings == null || Mod.Settings.KeepClear,
            };
            Dependency = job.ScheduleParallel(m_Query, Dependency);
        }

        [BurstCompile]
        private struct ControlJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle EntityType;
            [ReadOnly] public ComponentTypeHandle<ManagedJunction> JunctionType;
            public ComponentTypeHandle<JunctionRuntime> RuntimeType;
            public ComponentTypeHandle<TrafficLights> LightsType;
            public BufferTypeHandle<JunctionPhase> PhaseType;
            public BufferTypeHandle<JunctionLane> LaneType;
            [ReadOnly] public BufferTypeHandle<JunctionMovement> MovementType;
            public BufferTypeHandle<MovementCounter> CounterType;
            [ReadOnly] public BufferTypeHandle<MovementStatistics> StatisticsType;
            [ReadOnly] public BufferTypeHandle<DetectorLane> DetectorType;
            [ReadOnly] public BufferTypeHandle<Game.Objects.SubObject> SubObjectType;

            // Lanes and poles belong to exactly one junction, so parallel
            // chunks never write the same entity.
            [NativeDisableParallelForRestriction] public ComponentLookup<LaneSignal> LaneSignals;
            [NativeDisableParallelForRestriction] public ComponentLookup<Game.Objects.TrafficLight> Poles;
            [ReadOnly] public BufferLookup<LaneObject> LaneObjects;
            [ReadOnly] public ComponentLookup<Curve> Curves;
            [ReadOnly] public ComponentLookup<Game.Objects.Moving> Movings;

            public long GlobalStep;
            public bool LeftHandTraffic;
            public bool TurnOnRed;
            public bool KeepClear;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<ManagedJunction> junctions = chunk.GetNativeArray(ref JunctionType);
                NativeArray<JunctionRuntime> runtimes = chunk.GetNativeArray(ref RuntimeType);
                NativeArray<TrafficLights> lights = chunk.GetNativeArray(ref LightsType);
                BufferAccessor<JunctionPhase> phaseBuffers = chunk.GetBufferAccessor(ref PhaseType);
                BufferAccessor<JunctionLane> laneBuffers = chunk.GetBufferAccessor(ref LaneType);
                BufferAccessor<JunctionMovement> movementBuffers = chunk.GetBufferAccessor(ref MovementType);
                BufferAccessor<MovementCounter> counterBuffers = chunk.GetBufferAccessor(ref CounterType);
                BufferAccessor<MovementStatistics> statisticsBuffers = chunk.GetBufferAccessor(ref StatisticsType);
                bool hasDetectors = chunk.Has(ref DetectorType);
                BufferAccessor<DetectorLane> detectorBuffers = hasDetectors ? chunk.GetBufferAccessor(ref DetectorType) : default;
                bool hasPoles = chunk.Has(ref SubObjectType);
                BufferAccessor<Game.Objects.SubObject> subObjects = hasPoles ? chunk.GetBufferAccessor(ref SubObjectType) : default;

                for (int i = 0; i < chunk.Count; i++)
                {
                    ManagedJunction junction = junctions[i];
                    JunctionRuntime runtime = runtimes[i];
                    TrafficLights light = lights[i];
                    DynamicBuffer<JunctionPhase> phases = phaseBuffers[i];
                    DynamicBuffer<JunctionLane> lanes = laneBuffers[i];
                    DynamicBuffer<JunctionMovement> movements = movementBuffers[i];
                    if (phases.Length == 0 || movements.Length == 0 || movements.Length > 64)
                        continue;
                    DynamicBuffer<MovementCounter> counters = counterBuffers[i];
                    DynamicBuffer<MovementStatistics> statistics = statisticsBuffers[i];
                    if (counters.Length != movements.Length || statistics.Length != movements.Length)
                        continue;

                    DynamicBuffer<DetectorLane> detectors = hasDetectors ? detectorBuffers[i] : default;
                    Sense(phases, lanes, detectors, hasDetectors, counters, statistics, movements.Length);

                    ControllerConfig config = junction.ToConfig();
                    var access = new PhaseBufferAccess(phases);
                    SignalController.Step(ref runtime.State, in config, ref access, GlobalStep);

                    Show(runtime.State, phases, lanes, movements, ref light);
                    if (hasPoles)
                        ShowOnPoles(subObjects[i], light, runtime.State.Stage == Stage.Flashing);

                    runtimes[i] = runtime;
                    lights[i] = light;
                }
            }

            /// <summary>
            /// Fills the sensor readings of every phase and the measurement
            /// counters of every movement, the way detectors at a real
            /// junction would see the traffic:
            ///
            /// - Entry: a vehicle is counted once when it enters a junction
            ///   lane, which also tells its direction.
            /// - Approach zone, up to kDetectionRange before the stop line on
            ///   the approach road only (radar): standing vehicles are the
            ///   queue, moving ones are arrivals with a time to the line.
            ///   Nothing beyond the upstream junction, and no routes.
            ///
            /// An approach lane serving several movements is split among them
            /// by their measured shares.
            /// </summary>
            private unsafe void Sense(DynamicBuffer<JunctionPhase> phases, DynamicBuffer<JunctionLane> lanes,
                DynamicBuffer<DetectorLane> detectors, bool hasDetectors,
                DynamicBuffer<MovementCounter> counters, DynamicBuffer<MovementStatistics> statistics, int movementCount)
            {
                float* waiting = stackalloc float[movementCount];
                float* soon = stackalloc float[movementCount];
                float* near = stackalloc float[movementCount];
                float* arriving = stackalloc float[movementCount];
                bool* blocked = stackalloc bool[movementCount];
                bool* busy = stackalloc bool[movementCount];
                bool* preempt = stackalloc bool[movementCount];
                bool* call = stackalloc bool[movementCount];
                for (int m = 0; m < movementCount; m++)
                {
                    waiting[m] = 0f;
                    soon[m] = 0f;
                    near[m] = 0f;
                    arriving[m] = 0f;
                    blocked[m] = false;
                    busy[m] = false;
                    preempt[m] = false;
                    call[m] = false;
                }

                for (int l = 0; l < lanes.Length; l++)
                {
                    JunctionLane lane = lanes[l];
                    int m = lane.Movement;
                    if (m >= movementCount || !LaneSignals.HasComponent(lane.Lane))
                        continue;

                    // Requests the game's road users made since the last step.
                    // Reading them uses them up, as the game's own system does.
                    // A request means someone is at the line. On a crosswalk
                    // it is the push button: pedestrians ask when their next
                    // step is the crosswalk, and wait there while it is red.
                    LaneSignal signal = LaneSignals[lane.Lane];
                    if (signal.m_Priority > 0)
                    {
                        if ((lane.Flags & JunctionLaneFlags.Pedestrian) != 0)
                            call[m] = true;
                        else
                            soon[m] += 1f;
                    }
                    if (signal.m_Priority >= kEmergencyPriority)
                        preempt[m] = true;
                    signal.m_Petitioner = Entity.Null;
                    signal.m_Priority = signal.m_Default;
                    LaneSignals[lane.Lane] = signal;

                    if (LaneObjects.TryGetBuffer(lane.Lane, out DynamicBuffer<LaneObject> inside) && inside.Length > 0)
                    {
                        busy[m] = true;
                        ref MovementCounter counter = ref counters.ElementAt(m);
                        if ((lane.Flags & JunctionLaneFlags.Pedestrian) != 0)
                        {
                            counter.PedestrianSteps += inside.Length;
                        }
                        else
                        {
                            Entity entrant = Rearmost(inside, out float position);
                            if (entrant != lane.LastEntrant && position < 0.5f)
                            {
                                counter.Vehicles++;
                                lane.LastEntrant = entrant;
                                lanes[l] = lane;
                            }
                        }
                    }

                    if (lane.Exit != Entity.Null && Occupancy(lane.Exit) > kBlockedOccupancy)
                        blocked[m] = true;
                }

                // Approach zones, once per approach lane, shared out among
                // the movements it feeds.
                for (int l = 0; l < lanes.Length; l++)
                {
                    Entity approach = lanes[l].Approach;
                    if (approach == Entity.Null || SeenBefore(lanes, l, approach))
                        continue;
                    float q = 0f, s = 0f, n = 0f, a = 0f;
                    Detect(approach, 0f, ref q, ref s, ref n, ref a);
                    if (hasDetectors)
                    {
                        for (int d = 0; d < detectors.Length; d++)
                        {
                            if (detectors[d].Approach == approach)
                                Detect(detectors[d].Lane, detectors[d].Offset, ref q, ref s, ref n, ref a);
                        }
                    }
                    if (q == 0f && s == 0f && a == 0f)
                        continue;
                    float total = 0f;
                    int sharing = 0;
                    for (int k = l; k < lanes.Length; k++)
                    {
                        if (lanes[k].Approach != approach || lanes[k].Movement >= movementCount)
                            continue;
                        total += statistics[lanes[k].Movement].Recent;
                        sharing++;
                    }
                    for (int k = l; k < lanes.Length; k++)
                    {
                        int m = lanes[k].Movement;
                        if (lanes[k].Approach != approach || m >= movementCount)
                            continue;
                        float share = total > 0f ? statistics[m].Recent / total : 1f / sharing;
                        waiting[m] += q * share;
                        soon[m] += s * share;
                        near[m] += n * share;
                        arriving[m] += a * share;
                    }
                }

                for (int m = 0; m < movementCount; m++)
                    counters.ElementAt(m).QueueSteps += waiting[m];

                for (int p = 0; p < phases.Length; p++)
                {
                    ref JunctionPhase phase = ref phases.ElementAt(p);
                    float demand = 0f;
                    float pressure = 0f;
                    float approaching = 0f;
                    bool phaseCall = false;
                    bool phaseBusy = false;
                    bool phasePreempt = false;
                    for (int m = 0; m < movementCount; m++)
                    {
                        if ((phase.Movements & (1UL << m)) == 0)
                            continue;
                        // Demand keeps a green going and asks for one: someone
                        // waits, or arrives within the passage time, as the
                        // gap setting of a real actuated controller.
                        demand += waiting[m] + soon[m];
                        // Max-pressure: the queue plus part of what is on its
                        // way. Green for a movement whose exit is full moves
                        // nobody, so it hardly counts.
                        float own = waiting[m] + soon[m] + 0.5f * arriving[m];
                        pressure += blocked[m] ? own * 0.1f : own;
                        // A platoon held for is only worth it if it can leave.
                        if (!blocked[m])
                            approaching += near[m];
                        // A waiting crosswalk weighs like one vehicle in the
                        // choice of the next phase; the maximum wait makes
                        // sure it is served even against heavy traffic.
                        if (call[m])
                        {
                            phaseCall = true;
                            pressure += 1f;
                        }
                        phaseBusy |= busy[m];
                        phasePreempt |= preempt[m];
                    }
                    phase.Data.Demand = demand;
                    phase.Data.Pressure = pressure;
                    phase.Data.Approaching = approaching;
                    phase.Data.PedestrianCall = phaseCall;
                    phase.Data.Busy = phaseBusy;
                    phase.Data.Preempt = phasePreempt;
                }
            }

            /// <summary>
            /// Adds what one lane of the approach zone sees: vehicles standing
            /// (<paramref name="waiting"/>), moving ones that reach the line
            /// within the passage time (<paramref name="soon"/>), within the
            /// hold horizon (<paramref name="near"/>) and within the planning
            /// horizon (<paramref name="arriving"/>, which includes near).
            /// <paramref name="offset"/> is the distance from the end of the
            /// lane to the stop line.
            /// </summary>
            private void Detect(Entity lane, float offset, ref float waiting, ref float soon, ref float near, ref float arriving)
            {
                if (!LaneObjects.TryGetBuffer(lane, out DynamicBuffer<LaneObject> objects) || objects.Length == 0 || !Curves.HasComponent(lane))
                    return;
                float length = Curves[lane].m_Length;
                for (int i = 0; i < objects.Length; i++)
                {
                    float distance = offset + (1f - math.cmax(objects[i].m_CurvePosition)) * length;
                    if (distance > kDetectionRange)
                        continue;
                    float speed = Movings.TryGetComponent(objects[i].m_LaneObject, out Game.Objects.Moving moving) ? math.length(moving.m_Velocity) : 0f;
                    if (speed < kStandingSpeed)
                    {
                        waiting += 1f;
                        continue;
                    }
                    float eta = distance / speed;
                    if (eta <= kPassageTime || distance <= kStopLineReach)
                    {
                        soon += 1f;
                        continue;
                    }
                    if (eta <= kHoldHorizon)
                        near += 1f;
                    if (eta <= kHorizon)
                        arriving += 1f;
                }
            }

            /// <summary>The vehicle nearest the start of a lane, and its position along it.</summary>
            private static Entity Rearmost(DynamicBuffer<LaneObject> objects, out float position)
            {
                Entity rear = Entity.Null;
                position = float.MaxValue;
                for (int i = 0; i < objects.Length; i++)
                {
                    float p = math.cmin(objects[i].m_CurvePosition);
                    if (p < position)
                    {
                        position = p;
                        rear = objects[i].m_LaneObject;
                    }
                }
                return rear;
            }

            private static bool SeenBefore(DynamicBuffer<JunctionLane> lanes, int index, Entity approach)
            {
                for (int k = 0; k < index; k++)
                {
                    if (lanes[k].Approach == approach)
                        return true;
                }
                return false;
            }

            /// <summary>How full a lane is, as a share of the vehicles it can hold.</summary>
            private float Occupancy(Entity lane)
            {
                if (!LaneObjects.TryGetBuffer(lane, out DynamicBuffer<LaneObject> objects) || !Curves.HasComponent(lane))
                    return 0f;
                float capacity = math.max(1f, Curves[lane].m_Length / kVehicleSpacing);
                return objects.Length / capacity;
            }

            /// <summary>Translates the controller stage into the game's signal states on every junction lane.</summary>
            private void Show(ControllerState state, DynamicBuffer<JunctionPhase> phases, DynamicBuffer<JunctionLane> lanes,
                DynamicBuffer<JunctionMovement> movements, ref TrafficLights light)
            {
                if (state.Stage == Stage.Flashing)
                {
                    light.m_State = TrafficLightState.None;
                    light.m_CurrentSignalGroup = 0;
                    light.m_NextSignalGroup = 0;
                    for (int l = 0; l < lanes.Length; l++)
                    {
                        JunctionLane lane = lanes[l];
                        if (!LaneSignals.HasComponent(lane.Lane))
                            continue;
                        LaneSignal signal = LaneSignals[lane.Lane];
                        MovementKind kind = lane.Movement < movements.Length ? movements[lane.Movement].Kind : MovementKind.Straight;
                        bool keepsGoing = (lane.Flags & JunctionLaneFlags.Major) != 0 && !IsLongTurn(kind);
                        signal.m_Signal = keepsGoing ? LaneSignalType.Go : LaneSignalType.Yield;
                        LaneSignals[lane.Lane] = signal;
                    }
                    return;
                }

                light.m_CurrentSignalGroup = (byte)(state.Phase + 1);
                light.m_NextSignalGroup = (byte)(state.Next + 1);
                switch (state.Stage)
                {
                    case Stage.Green:
                        light.m_State = TrafficLightState.Ongoing;
                        light.m_NextSignalGroup = 0;
                        break;
                    case Stage.Yellow:
                        light.m_State = TrafficLightState.Ending;
                        break;
                    case Stage.AllRed:
                        light.m_State = TrafficLightState.Changing;
                        break;
                    case Stage.Prepare:
                        light.m_State = TrafficLightState.Beginning;
                        break;
                }

                // The phase whose rules apply to a lane that has green: the
                // current one, or during a transition the next one if the lane
                // keeps its green into it.
                ulong permittedNow = phases[state.Phase].Permitted;
                ulong permittedNext = phases[state.Next].Permitted;
                ulong movementsNext = phases[state.Next].Movements;
                ulong turnOnRed = TurnOnRed && state.Stage == Stage.Green ? phases[state.Phase].TurnOnRed : 0UL;
                for (int l = 0; l < lanes.Length; l++)
                {
                    JunctionLane lane = lanes[l];
                    if (!LaneSignals.HasComponent(lane.Lane))
                        continue;
                    LaneSignal signal = LaneSignals[lane.Lane];
                    TrafficLightSystem.UpdateLaneSignal(light, ref signal);
                    ulong bit = 1UL << lane.Movement;
                    bool vehicle = (lane.Flags & (JunctionLaneFlags.Pedestrian | JunctionLaneFlags.Track)) == 0;
                    bool crosswalk = (lane.Flags & JunctionLaneFlags.Pedestrian) != 0;
                    if (crosswalk && signal.m_Signal != LaneSignalType.Stop)
                    {
                        // Push button: a crosswalk shows walk only while the
                        // controller serves pedestrians, and through a
                        // transition only if the walk goes on into the next
                        // phase. Otherwise it stays red with the cars on green.
                        bool continuing = state.Stage != Stage.Green && (movementsNext & bit) != 0;
                        if (!state.Walk || (state.Stage != Stage.Green && !continuing))
                            signal.m_Signal = LaneSignalType.Stop;
                    }
                    if (signal.m_Signal == LaneSignalType.Go)
                    {
                        bool continuing = state.Stage != Stage.Green && (movementsNext & bit) != 0;
                        ulong permitted = continuing ? permittedNext : permittedNow;
                        if ((permitted & bit) != 0)
                            signal.m_Signal = LaneSignalType.Yield;
                    }
                    else if (vehicle && signal.m_Signal == LaneSignalType.Stop && (turnOnRed & bit) != 0 && StoppedAtLine(lane.Approach))
                    {
                        // Turn on red as the rules have it: stop first, then
                        // go if the way is clear. The lane stays red until the
                        // front vehicle has come to a halt at the line.
                        signal.m_Signal = LaneSignalType.Yield;
                    }

                    if (vehicle && lane.Exit != Entity.Null)
                    {
                        bool held = KeepClear && Hold(ref lane);
                        if (!KeepClear)
                            lane.Flags &= ~JunctionLaneFlags.KeepClear;
                        lanes[l] = lane;
                        // SafeStop stops only vehicles that can still brake in
                        // time; one already at the line goes on.
                        if (held && (signal.m_Signal == LaneSignalType.Go || signal.m_Signal == LaneSignalType.Yield))
                            signal.m_Signal = LaneSignalType.SafeStop;
                    }
                    signal.m_Blocker = Entity.Null;
                    LaneSignals[lane.Lane] = signal;
                }
            }

            /// <summary>Whether the front vehicle on an approach lane stands at its end, i.e. at the stop line.</summary>
            private bool StoppedAtLine(Entity approach)
            {
                if (approach == Entity.Null || !LaneObjects.TryGetBuffer(approach, out DynamicBuffer<LaneObject> objects)
                    || objects.Length == 0 || !Curves.HasComponent(approach))
                    return false;
                float length = Curves[approach].m_Length;
                float front = -1f;
                Entity first = Entity.Null;
                for (int i = 0; i < objects.Length; i++)
                {
                    float position = math.cmax(objects[i].m_CurvePosition);
                    if (position > front)
                    {
                        front = position;
                        first = objects[i].m_LaneObject;
                    }
                }
                if ((1f - front) * length > kStopLineReach)
                    return false;
                return !Movings.TryGetComponent(first, out Game.Objects.Moving moving) || math.length(moving.m_Velocity) < kStoppedSpeed;
            }

            /// <summary>
            /// Keep clear: whether traffic entering this lane would get stuck
            /// in the junction because its exit is backed up to the start.
            /// Holding and releasing use different gaps, so a lane on the edge
            /// does not switch every step.
            /// </summary>
            private bool Hold(ref JunctionLane lane)
            {
                float free = FreeSpaceAtStart(lane.Exit);
                bool held = (lane.Flags & JunctionLaneFlags.KeepClear) != 0;
                held = held ? free < kKeepClearRelease : free < kKeepClearGap;
                if (held)
                    lane.Flags |= JunctionLaneFlags.KeepClear;
                else
                    lane.Flags &= ~JunctionLaneFlags.KeepClear;
                return held;
            }

            /// <summary>
            /// Distance from the start of a lane to its rearmost vehicle, if
            /// that vehicle is standing. A moving vehicle makes room, so the
            /// lane then counts as free.
            /// </summary>
            private float FreeSpaceAtStart(Entity lane)
            {
                if (!LaneObjects.TryGetBuffer(lane, out DynamicBuffer<LaneObject> objects) || objects.Length == 0 || !Curves.HasComponent(lane))
                    return float.MaxValue;
                float length = Curves[lane].m_Length;
                float nearest = float.MaxValue;
                Entity rear = Entity.Null;
                for (int i = 0; i < objects.Length; i++)
                {
                    float distance = math.cmin(objects[i].m_CurvePosition) * length;
                    if (distance < nearest)
                    {
                        nearest = distance;
                        rear = objects[i].m_LaneObject;
                    }
                }
                if (Movings.TryGetComponent(rear, out Game.Objects.Moving moving) && math.length(moving.m_Velocity) > kStandingSpeed)
                    return float.MaxValue;
                return nearest;
            }

            private bool IsLongTurn(MovementKind kind)
            {
                return kind == MovementKind.UTurn || kind == (LeftHandTraffic ? MovementKind.Right : MovementKind.Left);
            }

            private void ShowOnPoles(DynamicBuffer<Game.Objects.SubObject> subObjects, TrafficLights light, bool flashing)
            {
                for (int i = 0; i < subObjects.Length; i++)
                {
                    Entity pole = subObjects[i].m_SubObject;
                    if (!Poles.HasComponent(pole))
                        continue;
                    Game.Objects.TrafficLight head = Poles[pole];
                    if (flashing)
                    {
                        // Vehicle heads in the low four bits, pedestrian heads
                        // in the next four, as the game encodes them.
                        head.m_State = Game.Objects.TrafficLightState.Yellow | Game.Objects.TrafficLightState.Flashing;
                    }
                    else
                    {
                        TrafficLightSystem.UpdateTrafficLightState(light, ref head);
                    }
                    Poles[pole] = head;
                }
            }
        }

        /// <summary><see cref="IPhaseAccess"/> over a junction's phase buffer, usable in Burst jobs.</summary>
        private struct PhaseBufferAccess : IPhaseAccess
        {
            private DynamicBuffer<JunctionPhase> m_Phases;

            public PhaseBufferAccess(DynamicBuffer<JunctionPhase> phases)
            {
                m_Phases = phases;
            }

            public int Count => m_Phases.Length;

            public ref PhaseData this[int index] => ref m_Phases.ElementAt(index).Data;
        }
    }
}
