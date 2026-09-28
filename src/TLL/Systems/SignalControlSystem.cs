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
        /// Most vehicles read per approach lane with its detector lanes; more
        /// than 250 m of queue at the vehicle spacing, over two lanes.
        /// </summary>
        private const int kMaxSamples = 96;

        /// <summary>Road length a queued vehicle takes up, including the gap to the next one.</summary>
        private const float kVehicleSpacing = 7f;

        /// <summary>Exit lane fill above which traffic sent there would only block the junction.</summary>
        private const float kBlockedOccupancy = 0.85f;

        /// <summary>
        /// Weight of a tram against a car in the adaptive mode's pressure,
        /// roughly their passengers at typical loads. The maximum wait keeps
        /// the cars from being starved by a busy tram line.
        /// </summary>
        private const float kTramWeight = 10f;

        /// <summary>The game's vehicles ask for green with priority 100, emergency vehicles with 108.</summary>
        private const int kEmergencyPriority = 108;

        /// <summary>
        /// Keep clear: an exit whose last vehicle stands within this distance
        /// of the lane start has no room for one more car.
        /// </summary>
        private const float kKeepClearGap = kVehicleSpacing * 1.2f;

        /// <summary>A held lane is released once its exit has this much room, so it does not flicker.</summary>
        private const float kKeepClearRelease = kVehicleSpacing * 2.5f;

        private const float kStandingSpeed = ApproachSensor.StandingSpeed;

        /// <summary>Turn on red: a vehicle this close to the end of its lane is at the stop line.</summary>
        private const float kStopLineReach = ApproachSensor.StopLineReach;

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
            if (!mode.IsGame())
                return;
            if (Conflict != null)
            {
                // Standing back already: a city loaded later has TLL's signal
                // groups in its save too, which the game must rebuild.
                EntityManager.AddComponent<RebuildRequest>(GetEntityQuery(ComponentType.ReadOnly<ManagedJunction>()));
                return;
            }
            if (!Available)
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
        /// Without this system nobody switches the managed junctions' signals
        /// any more, and the game's own system skips them: they would freeze.
        /// They go back to the game instead.
        /// </summary>
        protected override void OnSwitchedOff()
        {
            StandBack(ErrorConflict);
        }

        /// <summary><see cref="Conflict"/> when TLL stood back after an error of its own, not for another mod.</summary>
        public const string ErrorConflict = "!";

        /// <summary>
        /// Leaves every junction to the game and the other mod for the rest
        /// of the session. Managed junctions keep their TLL data in the save,
        /// so they come back once the other mod is removed; until then their
        /// signal groups are rebuilt by whoever controls them now.
        /// </summary>
        internal void StandBack(string conflict)
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
                Blockers = GetComponentLookup<Game.Vehicles.Blocker>(true),
                Creatures = GetComponentLookup<Game.Creatures.Creature>(true),
                GlobalStep = SimTime.StepOfFrame(m_Simulation.frameIndex),
                LeftHandTraffic = m_CityConfiguration.leftHandTraffic,
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
            [ReadOnly] public ComponentLookup<Game.Vehicles.Blocker> Blockers;
            [ReadOnly] public ComponentLookup<Game.Creatures.Creature> Creatures;

            public long GlobalStep;
            public bool LeftHandTraffic;
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
                    bool scramble = (junction.Options & JunctionOptions.ScrambleOnDemand) != 0;
                    bool divert = scramble && runtime.Conflicts.Divert;
                    Sense(phases, lanes, detectors, hasDetectors, counters, statistics, movements, runtime.State, divert, out bool conflict);

                    ControllerConfig config = junction.ToConfig();
                    config.DivertPedestrians = divert;
                    var access = new PhaseBufferAccess(phases);
                    Stage stageBefore = runtime.State.Stage;
                    byte phaseBefore = runtime.State.Phase;
                    SignalController.Step(ref runtime.State, in config, ref access, GlobalStep);
                    if (scramble)
                        CountConflicts(ref runtime, phases, movements, stageBefore, phaseBefore, conflict);

                    Show(runtime.State, phases, lanes, movements, (junction.Options & JunctionOptions.TurnOnRed) != 0, ref light);
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
                DynamicBuffer<MovementCounter> counters, DynamicBuffer<MovementStatistics> statistics,
                DynamicBuffer<JunctionMovement> movements, ControllerState state, bool divert, out bool conflict)
            {
                int movementCount = movements.Length;
                float* waiting = stackalloc float[movementCount];
                float* soon = stackalloc float[movementCount];
                float* near = stackalloc float[movementCount];
                float* arriving = stackalloc float[movementCount];
                bool* blocked = stackalloc bool[movementCount];
                bool* busy = stackalloc bool[movementCount];
                bool* preempt = stackalloc bool[movementCount];
                bool* call = stackalloc bool[movementCount];
                bool* track = stackalloc bool[movementCount];
                bool* moving = stackalloc bool[movementCount];
                for (int m = 0; m < movementCount; m++)
                {
                    moving[m] = false;
                    waiting[m] = 0f;
                    soon[m] = 0f;
                    near[m] = 0f;
                    arriving[m] = 0f;
                    blocked[m] = false;
                    busy[m] = false;
                    preempt[m] = false;
                    call[m] = false;
                    track[m] = false;
                }

                for (int l = 0; l < lanes.Length; l++)
                {
                    JunctionLane lane = lanes[l];
                    int m = lane.Movement;
                    if (m >= movementCount || !LaneSignals.HasComponent(lane.Lane))
                        continue;
                    track[m] |= (lane.Flags & JunctionLaneFlags.Track) != 0;

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
                            moving[m] |= AnyMoving(inside);
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
                // the movements it feeds. The sample memory is taken once:
                // stack memory taken inside the loop would only be given back
                // when the method returns.
                float* distances = stackalloc float[kMaxSamples];
                float* speeds = stackalloc float[kMaxSamples];
                for (int l = 0; l < lanes.Length; l++)
                {
                    Entity approach = lanes[l].Approach;
                    if (approach == Entity.Null || SeenBefore(lanes, l, approach))
                        continue;
                    var samples = new Samples { Distances = distances, Speeds = speeds };
                    Gather(approach, 0f, ref samples);
                    if (hasDetectors)
                    {
                        for (int d = 0; d < detectors.Length; d++)
                        {
                            if (detectors[d].Approach == approach)
                                Gather(detectors[d].Lane, detectors[d].Offset, ref samples);
                        }
                    }
                    ApproachReading reading = ApproachSensor.Read(ref samples);
                    float q = reading.Waiting, s = reading.Soon, n = reading.Near, a = reading.Arriving;
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

                conflict = state.Stage == Stage.Green && state.Phase < phases.Length
                    && !phases[state.Phase].Data.HasFlag(PhaseFlags.Scramble)
                    && TurnsMeetPedestrians(phases[state.Phase].Movements, lanes, movements, busy, call, divert);

                for (int p = 0; p < phases.Length; p++)
                {
                    ref JunctionPhase phase = ref phases.ElementAt(p);
                    float demand = 0f;
                    float queue = 0f;
                    float pressure = 0f;
                    bool queued = false;
                    bool allQueuedBlocked = true;
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
                        queue += waiting[m];
                        if (waiting[m] >= 0.5f)
                        {
                            queued = true;
                            allQueuedBlocked &= blocked[m];
                        }
                        // Max-pressure: the queue plus part of what is on its
                        // way. Green for a movement whose exit is full moves
                        // nobody, so it hardly counts.
                        float own = waiting[m] + soon[m] + 0.5f * arriving[m];
                        // A tram carries the passengers of many cars, and on
                        // its own track it is not held up by the cars' jam,
                        // which only lowers the cars' own share.
                        if (track[m])
                            own *= kTramWeight;
                        pressure += blocked[m] ? own * 0.1f : own;
                        // A platoon held for is only worth it if it can leave.
                        if (!blocked[m])
                            approaching += near[m];
                        phaseCall |= call[m];
                        // The optimiser reads Busy as green that moved
                        // traffic: vehicles standing in the junction, or
                        // people on a crosswalk, do not count.
                        phaseBusy |= moving[m];
                        phasePreempt |= preempt[m];
                    }
                    // A pedestrian call asks for the phase but adds no
                    // pressure: it decides whether the phase comes, not
                    // ahead of which queue. Pedestrians then get their walk
                    // and no more, and the maximum wait makes sure they are
                    // served against steady traffic.
                    phase.Data.Demand = demand;
                    phase.Data.Queue = queue;
                    phase.Data.Blocked = queued && allQueuedBlocked;
                    phase.Data.Pressure = pressure;
                    phase.Data.Approaching = approaching;
                    phase.Data.PedestrianCall = phaseCall;
                    phase.Data.Busy = phaseBusy;
                    phase.Data.Preempt = phasePreempt;
                }
            }

            /// <summary>The vehicles of one approach zone, kept sorted by distance to the stop line.</summary>
            private unsafe struct Samples : IVehicleSamples
            {
                public float* Distances;
                public float* Speeds;
                public int Length;

                public int Count => Length;

                public float Distance(int index) => Distances[index];

                public float Speed(int index) => Speeds[index];

                /// <summary>Inserts in order; beyond the capacity the farthest vehicles are dropped.</summary>
                public void Add(float distance, float speed)
                {
                    int i = Length < kMaxSamples ? Length : kMaxSamples - 1;
                    if (Length == kMaxSamples && distance >= Distances[i])
                        return;
                    while (i > 0 && Distances[i - 1] > distance)
                    {
                        Distances[i] = Distances[i - 1];
                        Speeds[i] = Speeds[i - 1];
                        i--;
                    }
                    Distances[i] = distance;
                    Speeds[i] = speed;
                    if (Length < kMaxSamples)
                        Length++;
                }
            }

            /// <summary>
            /// Adds the vehicles on one lane of the approach zone within reach.
            /// <paramref name="offset"/> is the distance from the end of the
            /// lane to the stop line.
            /// </summary>
            private void Gather(Entity lane, float offset, ref Samples samples)
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
                    samples.Add(distance, speed);
                }
            }

            /// <summary>
            /// Whether turning vehicles of the green phase and pedestrians want
            /// the same crosswalk right now: the one across the road the turn
            /// leads into. While pedestrians cross with the vehicles, a turning
            /// vehicle the game holds for a pedestrian (its Blocker is one).
            /// While they are diverted, someone waiting at that crosswalk while
            /// turning vehicles use it. See PedestrianConflicts.
            /// </summary>
            private unsafe bool TurnsMeetPedestrians(ulong green, DynamicBuffer<JunctionLane> lanes, DynamicBuffer<JunctionMovement> movements,
                bool* busy, bool* call, bool divert)
            {
                for (int l = 0; l < lanes.Length; l++)
                {
                    JunctionLane lane = lanes[l];
                    int m = lane.Movement;
                    if (m >= movements.Length || (green & (1UL << m)) == 0)
                        continue;
                    MovementKind kind = movements[m].Kind;
                    if (kind != MovementKind.Left && kind != MovementKind.Right)
                        continue;
                    bool turning = divert ? busy[m] : HeldByPedestrian(lane.Lane);
                    if (!turning)
                        continue;
                    for (int c = 0; c < movements.Length; c++)
                    {
                        if (movements[c].Kind != MovementKind.Pedestrian || movements[c].Source != movements[m].Target)
                            continue;
                        if (divert ? call[c] : busy[c])
                            return true;
                    }
                }
                return false;
            }

            private bool AnyMoving(DynamicBuffer<LaneObject> objects)
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    if (Movings.TryGetComponent(objects[i].m_LaneObject, out Game.Objects.Moving moving) && math.length(moving.m_Velocity) > kStandingSpeed)
                        return true;
                }
                return false;
            }

            /// <summary>A vehicle on the lane is held by the game for a pedestrian.</summary>
            private bool HeldByPedestrian(Entity lane)
            {
                if (!LaneObjects.TryGetBuffer(lane, out DynamicBuffer<LaneObject> objects))
                    return false;
                for (int i = 0; i < objects.Length; i++)
                {
                    if (Blockers.TryGetComponent(objects[i].m_LaneObject, out Game.Vehicles.Blocker blocker)
                        && blocker.m_Blocker != Entity.Null && Creatures.HasComponent(blocker.m_Blocker))
                        return true;
                }
                return false;
            }

            /// <summary>
            /// Records the vehicle green that just ended in the junction's
            /// conflict history. Only greens in which turning vehicles and a
            /// crosswalk across their exit run together count: the others
            /// cannot tell anything about pedestrians.
            /// </summary>
            private static void CountConflicts(ref JunctionRuntime runtime, DynamicBuffer<JunctionPhase> phases, DynamicBuffer<JunctionMovement> movements,
                Stage stageBefore, byte phaseBefore, bool conflict)
            {
                if (stageBefore != Stage.Green || phaseBefore >= phases.Length || phases[phaseBefore].Data.HasFlag(PhaseFlags.Scramble))
                    return;
                runtime.ConflictThisGreen |= conflict;
                bool ended = runtime.State.Stage != Stage.Green || runtime.State.Phase != phaseBefore;
                if (!ended)
                    return;
                if (TurnsCrossCrosswalk(phases[phaseBefore].Movements, movements))
                    runtime.Conflicts.Record(runtime.ConflictThisGreen);
                runtime.ConflictThisGreen = false;
            }

            private static bool TurnsCrossCrosswalk(ulong green, DynamicBuffer<JunctionMovement> movements)
            {
                for (int m = 0; m < movements.Length; m++)
                {
                    if ((green & (1UL << m)) == 0 || (movements[m].Kind != MovementKind.Left && movements[m].Kind != MovementKind.Right))
                        continue;
                    for (int c = 0; c < movements.Length; c++)
                    {
                        if ((green & (1UL << c)) != 0 && movements[c].Kind == MovementKind.Pedestrian && movements[c].Source == movements[m].Target)
                            return true;
                    }
                }
                return false;
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
                DynamicBuffer<JunctionMovement> movements, bool turnOnRedAllowed, ref TrafficLights light)
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
                ulong turnOnRed = turnOnRedAllowed && state.Stage == Stage.Green ? phases[state.Phase].TurnOnRed : 0UL;
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
