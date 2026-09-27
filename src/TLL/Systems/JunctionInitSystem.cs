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
            List<JunctionPhase> existing = ExistingPlan(node, storedMovements, model);
            List<JunctionPhase> phases = junction.Origin == JunctionOrigin.Manual ? existing : null;
            if (phases == null)
            {
                if (junction.Origin == JunctionOrigin.Manual && EntityManager.HasBuffer<JunctionPhase>(node)
                    && EntityManager.GetBuffer<JunctionPhase>(node).Length > 0)
                    Mod.Log.Warn($"Junction {node}: the road layout changed, the manual plan no longer fits and was replaced by a generated one.");
                phases = NewPlan(model, junction.Strategy);
                KeepTiming(existing, phases);
            }

            for (int p = 0; p < phases.Count; p++)
            {
                JunctionPhase phase = phases[p];
                phase.TurnOnRed = PhasePlanner.TurnOnRed(model, phase.Movements);
                phases[p] = phase;
            }

            MarkMajorRoad(lanes, model, edges, node, junction.MajorApproach);
            bool sameMovements = SameMovements(node, storedMovements);
            WriteBuffers(node, storedMovements, phases, lanes, keys);
            WriteDetectors(node, JunctionAnalysis.DetectorChain(EntityManager, node, layout, SignalControlSystem.kDetectionRange));
            WriteMeasurement(node, lanes, keys, keepStatistics: sameMovements);
            WriteSignalGroups(node, phases, lanes, keys, ref lights);

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

        /// <summary>Whether the junction's saved movements are the ones just found, in the same order.</summary>
        private bool SameMovements(Entity node, List<JunctionMovement> movements)
        {
            if (!EntityManager.HasBuffer<JunctionMovement>(node))
                return false;
            DynamicBuffer<JunctionMovement> stored = EntityManager.GetBuffer<JunctionMovement>(node, true);
            if (stored.Length != movements.Count)
                return false;
            for (int i = 0; i < stored.Length; i++)
            {
                if (stored[i].Source != movements[i].Source || stored[i].Target != movements[i].Target || stored[i].Kind != movements[i].Kind)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Sets up the per-movement counters and statistics. Statistics from
        /// earlier days stay as long as the movements are the same ones;
        /// after a change to the road layout they start over.
        /// </summary>
        private void WriteMeasurement(Entity node, List<LaneInfo> lanes, List<MovementKey> keys, bool keepStatistics)
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
            if (!keepStatistics || statistics.Length != keys.Count)
            {
                statistics.Clear();
                for (int i = 0; i < keys.Count; i++)
                    statistics.Add(new MovementStatistics());
            }
        }

        /// <summary>The saved plan, if it still matches the junction's movements and gives every movement green.</summary>
        private List<JunctionPhase> ExistingPlan(Entity node, List<JunctionMovement> movements, JunctionModel model)
        {
            if (!EntityManager.HasBuffer<JunctionMovement>(node) || !EntityManager.HasBuffer<JunctionPhase>(node))
                return null;
            DynamicBuffer<JunctionMovement> stored = EntityManager.GetBuffer<JunctionMovement>(node, true);
            if (stored.Length != movements.Count)
                return null;
            for (int i = 0; i < stored.Length; i++)
            {
                if (stored[i].Source != movements[i].Source || stored[i].Target != movements[i].Target || stored[i].Kind != movements[i].Kind)
                    return null;
            }
            DynamicBuffer<JunctionPhase> storedPhases = EntityManager.GetBuffer<JunctionPhase>(node, true);
            if (storedPhases.Length == 0 || storedPhases.Length > PhasePlanner.MaxPhases)
                return null;

            var plan = new PhasePlan();
            var result = new List<JunctionPhase>();
            for (int i = 0; i < storedPhases.Length; i++)
            {
                plan.Phases.Add(new Phase { Green = storedPhases[i].Movements, Permitted = storedPhases[i].Permitted });
                result.Add(storedPhases[i]);
            }
            if (plan.Uncovered(movements.Count) != 0)
                return null;
            // A plan that would give green to two movements the geometry says
            // must never run together is not kept, whoever made it.
            foreach (Phase phase in plan.Phases)
            {
                for (int a = 0; a < movements.Count; a++)
                {
                    for (int b = a + 1; b < movements.Count; b++)
                    {
                        if (phase.Has(a) && phase.Has(b) && !model.Conflicts.CanShare(a, b))
                            return null;
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
                        MinGreen = (ushort)SimTime.ToSteps(pedestrian ? 7f : 5f),
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
        private void WriteSignalGroups(Entity node, List<JunctionPhase> phases, List<LaneInfo> lanes, List<MovementKey> keys, ref TrafficLights lights)
        {
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
