using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Tools;
using TLL.Components;
using TLL.Core;
using TLL.Core.Advisor;
using TLL.Core.Control;
using TLL.Core.Planning;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Edge = Game.Net.Edge;

namespace TLL.Systems
{
    /// <summary>
    /// Builds the plan of every managed junction that is new, was changed, or
    /// was just loaded, and writes it into the game's signal groups.
    ///
    /// Runs in Modification4B right after the game's own
    /// TrafficLightInitializationSystem, which has just written its vanilla
    /// grouping to the same lanes, and before SecondaryObjectSystem, which
    /// reads the lane groups to decide which signal heads the poles get.
    /// Writing here means the poles match TLL's phases without further work.
    ///
    /// This runs on the main thread. It only touches junctions that changed,
    /// which is rare compared with the per-step control.
    /// </summary>
    public partial class JunctionInitSystem : TllSystemBase
    {
        private EntityQuery m_ChangedQuery;
        private EntityQuery m_UnbuiltQuery;
        private CityConfigurationSystem m_CityConfiguration;
        private SignalControlSystem m_Control;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CityConfiguration = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_Control = World.GetOrCreateSystemManaged<SignalControlSystem>();
            m_ChangedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ManagedJunction>(), ComponentType.ReadWrite<TrafficLights>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<JunctionDirty>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_UnbuiltQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ManagedJunction>(), ComponentType.ReadWrite<TrafficLights>() },
                None = new[] { ComponentType.ReadOnly<JunctionLane>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        /// <summary>Junctions whose plan can no longer be built go back to the game rather than keep a stale one.</summary>
        protected override void OnSwitchedOff()
        {
            m_Control.StandBack(SignalControlSystem.ErrorConflict);
        }

        protected override void OnSafeUpdate()
        {
            // Without the bypass the game would drive these junctions too, with
            // TLL's groups; better to leave the game's grouping untouched.
            if (!m_Control.Available)
                return;
            if (m_ChangedQuery.IsEmptyIgnoreFilter && m_UnbuiltQuery.IsEmptyIgnoreFilter)
                return;

            var nodes = new HashSet<Entity>();
            using (NativeArray<Entity> changed = m_ChangedQuery.ToEntityArray(Allocator.Temp))
                foreach (Entity e in changed)
                    nodes.Add(e);
            using (NativeArray<Entity> unbuilt = m_UnbuiltQuery.ToEntityArray(Allocator.Temp))
                foreach (Entity e in unbuilt)
                    nodes.Add(e);

            foreach (Entity node in nodes)
            {
                try
                {
                    Build(node);
                }
                catch (Exception e)
                {
                    // One junction the planner cannot handle must not stop the
                    // others, and nothing may escape into the game's update
                    // loop, which treats that as a critical error every frame.
                    Mod.Log.Error(e, $"Junction {node}: building the signal plan failed, returning it to the game's control.");
                    try
                    {
                        Release(node, exclude: true);
                    }
                    catch (Exception releaseError)
                    {
                        Mod.Log.Error(releaseError, $"Junction {node}: returning it to the game failed as well.");
                    }
                }
            }
        }

        private void Build(Entity node)
        {
            TrafficLights lights = EntityManager.GetComponentData<TrafficLights>(node);
            const TrafficLightFlags unsupported = TrafficLightFlags.LevelCrossing | TrafficLightFlags.MoveableBridge | TrafficLightFlags.IsSubNode;
            if ((lights.m_Flags & unsupported) != 0)
            {
                Release(node, exclude: true);
                return;
            }

            ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
            JunctionLayout layout = JunctionAnalysis.Analyse(EntityManager, node, m_CityConfiguration.leftHandTraffic);
            if (layout == null)
            {
                Release(node, exclude: true);
                return;
            }
            List<Entity> edges = layout.Edges;
            float[] angles = layout.Angles;
            List<LaneInfo> lanes = layout.Lanes;
            List<MovementKey> keys = layout.Keys;
            JunctionModel model = layout.Model;

            var storedMovements = new List<JunctionMovement>();
            foreach (MovementKey key in keys)
            {
                storedMovements.Add(new JunctionMovement
                {
                    Source = edges[key.Source],
                    Target = key.Target >= 0 ? edges[key.Target] : Entity.Null,
                    Kind = key.Kind,
                });
            }

            // A manual plan is the player's and stays as long as it fits. An
            // automatic plan is always generated afresh, so automatic junctions
            // follow the current planner; the timing learnt so far is kept
            // where a phase stays the same.
            int[] savedToCurrent = SavedToCurrent(node, storedMovements, out string changed);
            List<JunctionPhase> existing = ExistingPlan(node, savedToCurrent, changed, model, out string misfit);
            List<JunctionPhase> phases = junction.Origin == JunctionOrigin.Manual ? existing : null;
            if (phases == null)
            {
                if (junction.Origin == JunctionOrigin.Manual && misfit != null)
                    Mod.Log.Warn($"Junction {node}: the saved plan was replaced by a generated one, because {misfit}.");
                phases = NewPlan(model, junction.Strategy, (junction.Options & JunctionOptions.ScrambleOnDemand) != 0);
                KeepTiming(existing, phases);
            }

            for (int p = 0; p < phases.Count; p++)
            {
                JunctionPhase phase = phases[p];
                phase.TurnOnRed = PhasePlanner.TurnOnRed(model, phase.Movements);
                phase.Data.WalkGreen = WalkGreen(phase.Movements, lanes, keys, junction);
                phases[p] = phase;
            }

            MarkMajorRoad(lanes, model, edges, node, junction.MajorApproach);
            WriteBuffers(node, storedMovements, phases, lanes, keys);
            WriteDetectors(node, JunctionAnalysis.DetectorChain(EntityManager, node, layout, SignalControlSystem.kDetectionRange));
            WriteMeasurement(node, lanes, keys, savedToCurrent);
            WriteSignalGroups(node, phases, lanes, keys, layout.Unassigned, ref lights);

            if (EntityManager.HasComponent<JunctionDirty>(node))
                EntityManager.RemoveComponent<JunctionDirty>(node);
            if (Mod.Settings != null && Mod.Settings.VerboseLogging)
                Mod.Log.Info($"Junction {node}: {edges.Count} approaches, {keys.Count} movements, {lanes.Count} lanes, {phases.Count} phases, mode {junction.Mode}.\n{Describe(model, angles, phases)}");
        }

        /// <summary>
        /// Approaches, movements and phases in one block for the log, so a
        /// plan can be checked against the junction without the game.
        /// </summary>
        private static string Describe(JunctionModel model, float[] angles, List<JunctionPhase> phases)
        {
            var text = new System.Text.StringBuilder();
            text.Append("  approaches:");
            for (int a = 0; a < angles.Length; a++)
                text.Append($" {a}@{angles[a]:0}deg(opposite {model.OppositeOf[a]})");
            for (int p = 0; p < phases.Count; p++)
            {
                text.Append($"\n  phase {p + 1}:");
                for (int m = 0; m < model.Movements.Count; m++)
                {
                    if ((phases[p].Movements & (1UL << m)) == 0)
                        continue;
                    text.Append(' ').Append(model.Movements[m]);
                    if ((phases[p].Permitted & (1UL << m)) != 0)
                        text.Append("(yield)");
                    text.Append(',');
                }
            }
            text.Append("\n  hard conflicts:");
            for (int a = 0; a < model.Movements.Count; a++)
            {
                for (int b = a + 1; b < model.Movements.Count; b++)
                {
                    if (model.Conflicts.Get(a, b) == Relation.Hard)
                        text.Append($" [{model.Movements[a]} x {model.Movements[b]}]");
                }
            }
            return text.ToString();
        }

        private void Release(Entity node, bool exclude)
        {
            Release(EntityManager, node, exclude);
        }

        /// <summary>
        /// Hands a junction back to the game's own traffic light control and
        /// asks the game to rebuild its vanilla signal groups on the next
        /// frame (see <see cref="RebuildRequestSystem"/>). With
        /// <paramref name="exclude"/> the automation will not take it over again.
        /// </summary>
        public static void Release(EntityManager entityManager, Entity node, bool exclude)
        {
            entityManager.RemoveComponent<ManagedJunction>(node);
            entityManager.RemoveComponent<JunctionMovement>(node);
            entityManager.RemoveComponent<JunctionPhase>(node);
            entityManager.RemoveComponent<JunctionLane>(node);
            entityManager.RemoveComponent<JunctionRuntime>(node);
            entityManager.RemoveComponent<JunctionDirty>(node);
            entityManager.RemoveComponent<MovementCounter>(node);
            entityManager.RemoveComponent<MovementStatistics>(node);
            entityManager.RemoveComponent<AutopilotState>(node);
            entityManager.RemoveComponent<DetectorLane>(node);
            entityManager.RemoveComponent<JunctionHealth>(node);
            if (exclude)
                entityManager.AddComponent<JunctionExcluded>(node);
            entityManager.AddComponent<RebuildRequest>(node);
        }

        /// <summary>
        /// Where each saved movement is in the list just found: element i is
        /// the new index of saved movement i. Null if the junction does not
        /// have the same movements any more, or had none saved.
        /// </summary>
        /// <remarks>
        /// Movements are matched by their roads and kind, not by position.
        /// Their order follows the node's list of connected roads, and the
        /// game may hand that list out in another order after loading.
        /// </remarks>
        private int[] SavedToCurrent(Entity node, List<JunctionMovement> movements, out string reason)
        {
            reason = null;
            if (!EntityManager.HasBuffer<JunctionMovement>(node))
                return null;
            DynamicBuffer<JunctionMovement> stored = EntityManager.GetBuffer<JunctionMovement>(node, true);
            if (stored.Length == 0)
                return null;
            if (stored.Length != movements.Count)
            {
                reason = $"the junction now has {movements.Count} movements instead of {stored.Length}";
                return null;
            }
            var map = new int[stored.Length];
            for (int i = 0; i < stored.Length; i++)
            {
                map[i] = -1;
                for (int j = 0; j < movements.Count; j++)
                {
                    if (stored[i].Source == movements[j].Source && stored[i].Target == movements[j].Target && stored[i].Kind == movements[j].Kind)
                    {
                        map[i] = j;
                        break;
                    }
                }
                if (map[i] < 0)
                {
                    reason = $"its {stored[i].Kind} movement {i} no longer exists";
                    return null;
                }
            }
            return map;
        }

        /// <summary>Moves the bits of a movement mask from saved to current movement indices.</summary>
        private static ulong Remap(ulong mask, int[] map)
        {
            ulong result = 0;
            for (int i = 0; i < map.Length; i++)
            {
                if ((mask & (1UL << i)) != 0)
                    result |= 1UL << map[i];
            }
            return result;
        }

        /// <summary>
        /// Sets up the per-movement counters and statistics. Statistics from
        /// earlier days stay as long as the movements are the same ones
        /// (<paramref name="savedToCurrent"/> not null); after a change to the
        /// road layout they start over.
        /// </summary>
        private void WriteMeasurement(Entity node, List<LaneInfo> lanes, List<MovementKey> keys, int[] savedToCurrent)
        {
            DynamicBuffer<MovementCounter> counters = EntityManager.HasBuffer<MovementCounter>(node)
                ? EntityManager.GetBuffer<MovementCounter>(node)
                : EntityManager.AddBuffer<MovementCounter>(node);
            counters.Clear();
            for (int i = 0; i < keys.Count; i++)
                counters.Add(new MovementCounter());
            foreach (LaneInfo lane in lanes)
            {
                if ((lane.Flags & JunctionLaneFlags.Pedestrian) == 0 || !EntityManager.HasComponent<Curve>(lane.Lane))
                    continue;
                ref MovementCounter counter = ref counters.ElementAt(keys.IndexOf(lane.Key));
                counter.Length = math.max(counter.Length, EntityManager.GetComponentData<Curve>(lane.Lane).m_Length);
            }

            bool has = EntityManager.HasBuffer<MovementStatistics>(node);
            DynamicBuffer<MovementStatistics> statistics = has
                ? EntityManager.GetBuffer<MovementStatistics>(node)
                : EntityManager.AddBuffer<MovementStatistics>(node);
            var saved = new MovementStatistics[statistics.Length];
            for (int i = 0; i < saved.Length; i++)
                saved[i] = statistics[i];
            statistics.Clear();
            for (int i = 0; i < keys.Count; i++)
                statistics.Add(new MovementStatistics());
            if (savedToCurrent == null || saved.Length != savedToCurrent.Length)
                return;
            for (int i = 0; i < saved.Length; i++)
                statistics[savedToCurrent[i]] = saved[i];
        }

        /// <summary>
        /// The saved plan, moved to the current movement order, if it still
        /// fits the junction; else null with the reason in
        /// <paramref name="reason"/> (null when there was no plan).
        /// </summary>
        private List<JunctionPhase> ExistingPlan(Entity node, int[] savedToCurrent, string misfit, JunctionModel model, out string reason)
        {
            reason = null;
            if (!EntityManager.HasBuffer<JunctionPhase>(node))
                return null;
            DynamicBuffer<JunctionPhase> storedPhases = EntityManager.GetBuffer<JunctionPhase>(node, true);
            if (storedPhases.Length == 0)
                return null;
            if (savedToCurrent == null)
            {
                reason = misfit ?? "its movements were not saved";
                return null;
            }
            int movementCount = savedToCurrent.Length;
            if (storedPhases.Length > PhasePlanner.MaxPhases)
            {
                reason = $"it has {storedPhases.Length} phases, at most {PhasePlanner.MaxPhases} are allowed";
                return null;
            }

            var plan = new PhasePlan();
            var result = new List<JunctionPhase>();
            for (int i = 0; i < storedPhases.Length; i++)
            {
                JunctionPhase phase = storedPhases[i];
                phase.Movements = Remap(phase.Movements, savedToCurrent);
                // Who gives way follows from the lanes as they are now: a
                // change of lane layout on the same roads can add a conflict
                // the saved plan does not know.
                phase.Permitted = PhasePlanner.PermittedIn(model, phase.Movements);
                plan.Phases.Add(new Phase { Green = phase.Movements, Permitted = phase.Permitted });
                result.Add(phase);
            }
            if (plan.Uncovered(movementCount) != 0)
            {
                reason = "some movements never get green in it";
                return null;
            }
            // A plan that would give green to two movements the geometry says
            // must never run together is not kept, whoever made it.
            foreach (Phase phase in plan.Phases)
            {
                for (int a = 0; a < movementCount; a++)
                {
                    for (int b = a + 1; b < movementCount; b++)
                    {
                        if (phase.Has(a) && phase.Has(b) && !model.Conflicts.CanShare(a, b))
                        {
                            reason = $"it gives green together to {model.Movements[a]} and {model.Movements[b]}, whose paths cross";
                            return null;
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>Carries timing over to new phases that give green to exactly the same movements as an old one.</summary>
        private static void KeepTiming(List<JunctionPhase> existing, List<JunctionPhase> phases)
        {
            if (existing == null)
                return;
            for (int p = 0; p < phases.Count; p++)
            {
                foreach (JunctionPhase old in existing)
                {
                    if (old.Movements != phases[p].Movements)
                        continue;
                    JunctionPhase phase = phases[p];
                    phase.Data.Green = old.Data.Green;
                    phase.Data.MaxGreen = old.Data.MaxGreen;
                    phase.Data.Flags = old.Data.Flags;
                    phases[p] = phase;
                    break;
                }
            }
        }

        /// <summary>Walk signal shown before the flashing don't-walk, as in most signal timing guides.</summary>
        private const float kWalkInterval = 7f;

        /// <summary>
        /// Green a phase needs once pedestrians walk in it: the walk interval,
        /// then time for someone who stepped off at its end to cross the
        /// longest of its crosswalks. The yellow and all-red that follow count
        /// towards that clearance. Zero for a phase without a crosswalk.
        /// </summary>
        private ushort WalkGreen(ulong movements, List<LaneInfo> lanes, List<MovementKey> keys, ManagedJunction junction)
        {
            float longest = -1f;
            foreach (LaneInfo lane in lanes)
            {
                if ((lane.Flags & JunctionLaneFlags.Pedestrian) == 0 || (movements & (1UL << keys.IndexOf(lane.Key))) == 0)
                    continue;
                float length = EntityManager.HasComponent<Curve>(lane.Lane) ? EntityManager.GetComponentData<Curve>(lane.Lane).m_Length : 0f;
                longest = Math.Max(longest, length);
            }
            if (longest < 0f)
                return 0;
            int walk = SimTime.ToSteps(kWalkInterval);
            int clearance = SimTime.ToSteps(longest / DelayModel.WalkingSpeed) - junction.Yellow - junction.AllRed;
            return (ushort)(walk + Math.Max(0, clearance));
        }

        /// <param name="scrambleOnDemand">
        /// Adds a phase of all crosswalks at the end, run only while
        /// pedestrians are diverted into it. The pedestrian scramble layout has
        /// such a phase already, as its only pedestrian phase.
        /// </param>
        private static List<JunctionPhase> NewPlan(JunctionModel model, PlanStrategy strategy, bool scrambleOnDemand)
        {
            List<JunctionPhase> result = NewPlan(model, strategy);
            if (!scrambleOnDemand || strategy == PlanStrategy.ExclusivePedestrian || result.Count >= PhasePlanner.MaxPhases)
                return result;
            ulong crosswalks = 0;
            bool turns = false;
            for (int i = 0; i < model.Movements.Count; i++)
            {
                MovementKind kind = model.Movements[i].Kind;
                if (kind == MovementKind.Pedestrian)
                    crosswalks |= 1UL << i;
                turns |= kind == MovementKind.Left || kind == MovementKind.Right;
            }
            // Without turning traffic nobody has to wait for pedestrians. And
            // where the plan already has a phase of crosswalks only, because
            // they meet some vehicle in every other phase, pedestrians have
            // their own phase anyway.
            if (crosswalks == 0 || !turns)
                return result;
            foreach (JunctionPhase phase in result)
            {
                if ((phase.Movements & ~crosswalks) == 0)
                    return result;
            }
            result.Add(new JunctionPhase
            {
                Movements = crosswalks,
                Data = new PhaseData
                {
                    MinGreen = (ushort)SimTime.ToSteps(5f),
                    MaxGreen = (ushort)SimTime.ToSteps(30f),
                    Green = (ushort)SimTime.ToSteps(10f),
                    Flags = PhaseFlags.Pedestrian | PhaseFlags.Scramble,
                },
            });
            return result;
        }

        private static List<JunctionPhase> NewPlan(JunctionModel model, PlanStrategy strategy)
        {
            PhasePlan plan = PhasePlanner.Build(model, strategy);
            var result = new List<JunctionPhase>();
            foreach (Phase phase in plan.Phases)
            {
                bool pedestrian = false;
                bool straight = false;
                for (int i = 0; i < model.Movements.Count; i++)
                {
                    if (!phase.Has(i))
                        continue;
                    MovementKind kind = model.Movements[i].Kind;
                    pedestrian |= kind == MovementKind.Pedestrian;
                    straight |= kind == MovementKind.Straight || kind == MovementKind.Track;
                }
                result.Add(new JunctionPhase
                {
                    Movements = phase.Green,
                    Permitted = phase.Permitted,
                    Data = new PhaseData
                    {
                        // The walk time comes on top only when someone calls
                        // (see WalkGreen); a phase of crosswalks alone has no
                        // other reason to run.
                        MinGreen = (ushort)SimTime.ToSteps(5f),
                        MaxGreen = (ushort)SimTime.ToSteps(straight ? 45f : 25f),
                        Green = (ushort)SimTime.ToSteps(straight ? 20f : 10f),
                        Flags = pedestrian ? PhaseFlags.Pedestrian : PhaseFlags.None,
                    },
                });
            }
            return result;
        }

        /// <summary>
        /// Flags the lanes of the major road, which keeps going while the
        /// signals flash. Without a choice by the player, the major road is
        /// the pair of opposite approaches with the most lanes.
        /// </summary>
        private void MarkMajorRoad(List<LaneInfo> lanes, JunctionModel model, List<Entity> edges, Entity node, Entity chosen)
        {
            int major = chosen != Entity.Null ? edges.IndexOf(chosen) : -1;
            if (major < 0)
            {
                int bestLanes = -1;
                for (int a = 0; a < edges.Count; a++)
                {
                    int count = 0;
                    foreach (LaneInfo lane in lanes)
                    {
                        if ((lane.Flags & JunctionLaneFlags.Pedestrian) == 0
                            && (lane.Key.Source == a || lane.Key.Source == model.OppositeOf[a]))
                            count++;
                    }
                    if (count > bestLanes)
                    {
                        bestLanes = count;
                        major = a;
                    }
                }
            }
            int opposite = major >= 0 ? model.OppositeOf[major] : -1;
            for (int i = 0; i < lanes.Count; i++)
            {
                LaneInfo lane = lanes[i];
                bool onMajor = (lane.Flags & JunctionLaneFlags.Pedestrian) == 0
                    && (lane.Key.Source == major || (opposite >= 0 && lane.Key.Source == opposite));
                if (onMajor)
                {
                    lane.Flags |= JunctionLaneFlags.Major;
                    lanes[i] = lane;
                }
            }
        }

        private void WriteBuffers(Entity node, List<JunctionMovement> movements, List<JunctionPhase> phases, List<LaneInfo> lanes, List<MovementKey> keys)
        {
            DynamicBuffer<JunctionMovement> movementBuffer = EntityManager.HasBuffer<JunctionMovement>(node)
                ? EntityManager.GetBuffer<JunctionMovement>(node)
                : EntityManager.AddBuffer<JunctionMovement>(node);
            movementBuffer.Clear();
            foreach (JunctionMovement m in movements)
                movementBuffer.Add(m);

            DynamicBuffer<JunctionPhase> phaseBuffer = EntityManager.HasBuffer<JunctionPhase>(node)
                ? EntityManager.GetBuffer<JunctionPhase>(node)
                : EntityManager.AddBuffer<JunctionPhase>(node);
            phaseBuffer.Clear();
            foreach (JunctionPhase p in phases)
                phaseBuffer.Add(p);

            DynamicBuffer<JunctionLane> laneBuffer = EntityManager.HasBuffer<JunctionLane>(node)
                ? EntityManager.GetBuffer<JunctionLane>(node)
                : EntityManager.AddBuffer<JunctionLane>(node);
            laneBuffer.Clear();
            foreach (LaneInfo lane in lanes)
            {
                laneBuffer.Add(new JunctionLane
                {
                    Lane = lane.Lane,
                    Approach = lane.Approach,
                    Exit = lane.Exit,
                    Movement = (byte)keys.IndexOf(lane.Key),
                    Flags = lane.Flags,
                });
            }

            // A rebuilt plan starts its controller from scratch.
            var runtime = new JunctionRuntime();
            if (EntityManager.HasComponent<JunctionRuntime>(node))
                EntityManager.SetComponentData(node, runtime);
            else
                EntityManager.AddComponentData(node, runtime);
        }

        private void WriteDetectors(Entity node, List<DetectorLane> detectors)
        {
            DynamicBuffer<DetectorLane> buffer = EntityManager.HasBuffer<DetectorLane>(node)
                ? EntityManager.GetBuffer<DetectorLane>(node)
                : EntityManager.AddBuffer<DetectorLane>(node);
            buffer.Clear();
            foreach (DetectorLane d in detectors)
                buffer.Add(d);
        }

        /// <summary>
        /// Puts every junction lane into the game's signal groups of the
        /// phases that give its movement green. Group i + 1 is phase i.
        /// </summary>
        private void WriteSignalGroups(Entity node, List<JunctionPhase> phases, List<LaneInfo> lanes, List<MovementKey> keys, List<Entity> unassigned, ref TrafficLights lights)
        {
            // Lanes TLL cannot put into a movement keep whatever the game set
            // last, and nobody updates them while TLL runs the node; giving
            // way is the one signal that neither blocks them nor lets them
            // run into others.
            foreach (Entity lane in unassigned)
            {
                LaneSignal signal = EntityManager.GetComponentData<LaneSignal>(lane);
                signal.m_GroupMask = 0;
                signal.m_Signal = LaneSignalType.Yield;
                signal.m_Flags &= ~LaneSignalFlags.CanExtend;
                EntityManager.SetComponentData(lane, signal);
            }
            foreach (LaneInfo lane in lanes)
            {
                int movement = keys.IndexOf(lane.Key);
                ushort mask = 0;
                for (int p = 0; p < phases.Count; p++)
                {
                    if ((phases[p].Movements & (1UL << movement)) != 0)
                        mask |= (ushort)(1 << p);
                }
                LaneSignal signal = EntityManager.GetComponentData<LaneSignal>(lane.Lane);
                signal.m_GroupMask = mask;
                // Extension is the vanilla controller's mechanism; TLL decides
                // green lengths itself.
                signal.m_Flags &= ~LaneSignalFlags.CanExtend;
                EntityManager.SetComponentData(lane.Lane, signal);
            }
            lights.m_SignalGroupCount = (byte)phases.Count;
            lights.m_CurrentSignalGroup = 0;
            lights.m_NextSignalGroup = 0;
            lights.m_State = TrafficLightState.None;
            lights.m_Timer = 0;
            EntityManager.SetComponentData(node, lights);
        }
    }
}
