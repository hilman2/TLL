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
    public class SignalControlSystem : TllSystemBase
    {
        /// <summary>Distance before the stop line in which waiting and approaching vehicles count as demand.</summary>
        private const float kDetectionDistance = 60f;

        /// <summary>Road length a queued vehicle takes up, including the gap to the next one.</summary>
        private const float kVehicleSpacing = 7f;

        /// <summary>Exit lane fill above which traffic sent there would only block the junction.</summary>
        private const float kBlockedOccupancy = 0.85f;

        /// <summary>The game's vehicles ask for green with priority 100, emergency vehicles with 108.</summary>
        private const int kEmergencyPriority = 108;

        private SimulationSystem m_Simulation;
        private CityConfigurationSystem m_CityConfiguration;
        private EntityQuery m_Query;

        public bool Available { get; private set; }

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
                    ComponentType.ReadOnly<JunctionLane>(),
                    ComponentType.ReadOnly<JunctionMovement>(),
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
                LaneType = GetBufferTypeHandle<JunctionLane>(true),
                MovementType = GetBufferTypeHandle<JunctionMovement>(true),
                SubObjectType = GetBufferTypeHandle<Game.Objects.SubObject>(true),
                LaneSignals = GetComponentLookup<LaneSignal>(false),
                Poles = GetComponentLookup<Game.Objects.TrafficLight>(false),
                LaneObjects = GetBufferLookup<LaneObject>(true),
                Curves = GetComponentLookup<Curve>(true),
                GlobalStep = SimTime.StepOfFrame(m_Simulation.frameIndex),
                LeftHandTraffic = m_CityConfiguration.leftHandTraffic,
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
            [ReadOnly] public BufferTypeHandle<JunctionLane> LaneType;
            [ReadOnly] public BufferTypeHandle<JunctionMovement> MovementType;
            [ReadOnly] public BufferTypeHandle<Game.Objects.SubObject> SubObjectType;

            // Lanes and poles belong to exactly one junction, so parallel
            // chunks never write the same entity.
            [NativeDisableParallelForRestriction] public ComponentLookup<LaneSignal> LaneSignals;
            [NativeDisableParallelForRestriction] public ComponentLookup<Game.Objects.TrafficLight> Poles;
            [ReadOnly] public BufferLookup<LaneObject> LaneObjects;
            [ReadOnly] public ComponentLookup<Curve> Curves;

            public long GlobalStep;
            public bool LeftHandTraffic;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<ManagedJunction> junctions = chunk.GetNativeArray(ref JunctionType);
                NativeArray<JunctionRuntime> runtimes = chunk.GetNativeArray(ref RuntimeType);
                NativeArray<TrafficLights> lights = chunk.GetNativeArray(ref LightsType);
                BufferAccessor<JunctionPhase> phaseBuffers = chunk.GetBufferAccessor(ref PhaseType);
                BufferAccessor<JunctionLane> laneBuffers = chunk.GetBufferAccessor(ref LaneType);
                BufferAccessor<JunctionMovement> movementBuffers = chunk.GetBufferAccessor(ref MovementType);
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

                    Sense(phases, lanes, movements.Length);

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

            /// <summary>Fills the sensor readings of every phase from what is on the lanes now.</summary>
            private unsafe void Sense(DynamicBuffer<JunctionPhase> phases, DynamicBuffer<JunctionLane> lanes, int movementCount)
            {
                float* demand = stackalloc float[movementCount];
                bool* blocked = stackalloc bool[movementCount];
                bool* busy = stackalloc bool[movementCount];
                bool* preempt = stackalloc bool[movementCount];
                for (int m = 0; m < movementCount; m++)
                {
                    demand[m] = 0f;
                    blocked[m] = false;
                    busy[m] = false;
                    preempt[m] = false;
                }

                for (int l = 0; l < lanes.Length; l++)
                {
                    JunctionLane lane = lanes[l];
                    int m = lane.Movement;
                    if (m >= movementCount || !LaneSignals.HasComponent(lane.Lane))
                        continue;

                    // Requests the game's road users made since the last step.
                    // Reading them uses them up, as the game's own system does.
                    LaneSignal signal = LaneSignals[lane.Lane];
                    if (signal.m_Priority > 0)
                        demand[m] += 1f;
                    if (signal.m_Priority >= kEmergencyPriority)
                        preempt[m] = true;
                    signal.m_Petitioner = Entity.Null;
                    signal.m_Priority = signal.m_Default;
                    LaneSignals[lane.Lane] = signal;

                    if (LaneObjects.TryGetBuffer(lane.Lane, out DynamicBuffer<LaneObject> inside) && inside.Length > 0)
                        busy[m] = true;

                    if (lane.Approach != Entity.Null)
                        demand[m] += QueueOn(lane.Approach) / SharedApproachCount(lanes, lane.Approach);

                    if (lane.Exit != Entity.Null && Occupancy(lane.Exit) > kBlockedOccupancy)
                        blocked[m] = true;
                }

                for (int p = 0; p < phases.Length; p++)
                {
                    ref JunctionPhase phase = ref phases.ElementAt(p);
                    float phaseDemand = 0f;
                    float pressure = 0f;
                    bool phaseBusy = false;
                    bool phasePreempt = false;
                    for (int m = 0; m < movementCount; m++)
                    {
                        if ((phase.Movements & (1UL << m)) == 0)
                            continue;
                        phaseDemand += demand[m];
                        // Max-pressure weighting: green for a movement whose
                        // exit is full moves nobody, so it hardly counts.
                        pressure += blocked[m] ? demand[m] * 0.1f : demand[m];
                        phaseBusy |= busy[m];
                        phasePreempt |= preempt[m];
                    }
                    phase.Data.Demand = phaseDemand;
                    phase.Data.Pressure = pressure;
                    phase.Data.Busy = phaseBusy;
                    phase.Data.Preempt = phasePreempt;
                }
            }

            /// <summary>Vehicles within the detection distance before the end of an approach lane.</summary>
            private float QueueOn(Entity approach)
            {
                if (!LaneObjects.TryGetBuffer(approach, out DynamicBuffer<LaneObject> objects) || objects.Length == 0)
                    return 0f;
                float length = Curves.HasComponent(approach) ? Curves[approach].m_Length : kDetectionDistance;
                float from = length > kDetectionDistance ? 1f - kDetectionDistance / length : 0f;
                int count = 0;
                for (int i = 0; i < objects.Length; i++)
                {
                    if (objects[i].m_CurvePosition.x >= from)
                        count++;
                }
                return count;
            }

            /// <summary>How full a lane is, as a share of the vehicles it can hold.</summary>
            private float Occupancy(Entity lane)
            {
                if (!LaneObjects.TryGetBuffer(lane, out DynamicBuffer<LaneObject> objects) || !Curves.HasComponent(lane))
                    return 0f;
                float capacity = math.max(1f, Curves[lane].m_Length / kVehicleSpacing);
                return objects.Length / capacity;
            }

            /// <summary>
            /// Number of junction lanes fed by one approach lane. Its queue is
            /// split among them, since a vehicle takes only one.
            /// </summary>
            private static float SharedApproachCount(DynamicBuffer<JunctionLane> lanes, Entity approach)
            {
                int count = 0;
                for (int i = 0; i < lanes.Length; i++)
                    count += lanes[i].Approach == approach ? 1 : 0;
                return math.max(1, count);
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
                for (int l = 0; l < lanes.Length; l++)
                {
                    JunctionLane lane = lanes[l];
                    if (!LaneSignals.HasComponent(lane.Lane))
                        continue;
                    LaneSignal signal = LaneSignals[lane.Lane];
                    TrafficLightSystem.UpdateLaneSignal(light, ref signal);
                    if (signal.m_Signal == LaneSignalType.Go)
                    {
                        ulong bit = 1UL << lane.Movement;
                        bool continuing = state.Stage != Stage.Green && (movementsNext & bit) != 0;
                        ulong permitted = continuing ? permittedNext : permittedNow;
                        if ((permitted & bit) != 0)
                            signal.m_Signal = LaneSignalType.Yield;
                    }
                    signal.m_Blocker = Entity.Null;
                    LaneSignals[lane.Lane] = signal;
                }
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
