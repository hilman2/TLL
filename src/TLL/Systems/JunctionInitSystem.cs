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
    public class JunctionInitSystem : TllSystemBase
    {
        private EntityQuery m_ChangedQuery;
        private EntityQuery m_UnbuiltQuery;
        private CityConfigurationSystem m_CityConfiguration;
        private SignalControlSystem m_Control;

        /// <summary>Movement key while collecting lanes: source approach, target approach (-1 for a crosswalk), kind.</summary>
        private struct MovementKey : IEquatable<MovementKey>
        {
            public int Source;
            public int Target;
            public MovementKind Kind;

            public bool Equals(MovementKey other) => Source == other.Source && Target == other.Target && Kind == other.Kind;

            public override int GetHashCode() => (Source * 397) ^ (Target * 31) ^ (int)Kind;
        }

        private struct LaneInfo
        {
            public Entity Lane;
            public MovementKey Key;
            public Entity Approach;
            public Entity Exit;
            public JunctionLaneFlags Flags;
        }

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
            List<Entity> edges = ConnectedEdges(node);
            float[] angles = ApproachAngles(node, edges);
            List<LaneInfo> lanes = CollectLanes(node, edges);
            if (lanes.Count == 0)
            {
                Release(node, exclude: true);
                return;
            }

            // Movements in a fixed order, so the indices a saved plan refers to
            // come out the same for the same road layout.
            var keys = new List<MovementKey>();
            foreach (LaneInfo lane in lanes)
            {
                if (!keys.Contains(lane.Key))
                    keys.Add(lane.Key);
            }
            keys.Sort((a, b) => a.Source != b.Source ? a.Source.CompareTo(b.Source)
                : a.Target != b.Target ? a.Target.CompareTo(b.Target)
                : a.Kind.CompareTo(b.Kind));
            if (keys.Count > 64)
                throw new InvalidOperationException($"{keys.Count} movements, TLL handles at most 64.");

            var model = new JunctionModel
            {
                ApproachCount = edges.Count,
                OppositeOf = ChordModel.FindOpposites(angles),
                LeftHandTraffic = m_CityConfiguration.leftHandTraffic,
            };
            foreach (MovementKey key in keys)
            {
                int laneCount = 0;
                foreach (LaneInfo lane in lanes)
                    laneCount += lane.Key.Equals(key) ? 1 : 0;
                model.Movements.Add(new Movement(key.Source, key.Target, key.Kind, laneCount));
            }
            model.Conflicts = Conflicts(lanes, keys, model);
            // Safety net: the circle model catches pairs whose lanes the game
            // did not record as overlapping. It needs distinct approach
            // directions to place its points.
            if (ChordModel.SmallestGap(angles) >= 1f)
                model.Conflicts.Tighten(ChordModel.Classify(model.Movements, angles, model.OppositeOf, model.LeftHandTraffic));

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

            List<JunctionPhase> phases = ExistingPlan(node, storedMovements, model);
            if (phases == null)
            {
                if (junction.Origin == JunctionOrigin.Manual && EntityManager.HasBuffer<JunctionPhase>(node)
                    && EntityManager.GetBuffer<JunctionPhase>(node).Length > 0)
                    Mod.Log.Warn($"Junction {node}: the road layout changed, the manual plan no longer fits and was replaced by a generated one.");
                phases = NewPlan(model, junction.Strategy);
            }

            for (int p = 0; p < phases.Count; p++)
            {
                JunctionPhase phase = phases[p];
                phase.TurnOnRed = PhasePlanner.TurnOnRed(model, phase.Movements);
                phases[p] = phase;
            }

            MarkMajorRoad(lanes, model, edges, node, junction.MajorApproach);
            WriteBuffers(node, storedMovements, phases, lanes, keys);
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
            if (exclude)
                entityManager.AddComponent<JunctionExcluded>(node);
            entityManager.AddComponent<RebuildRequest>(node);
        }

        private List<Entity> ConnectedEdges(Entity node)
        {
            var edges = new List<Entity>();
            DynamicBuffer<ConnectedEdge> connected = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < connected.Length; i++)
                edges.Add(connected[i].m_Edge);
            return edges;
        }

        /// <summary>Direction from the node out along each edge, in degrees counter-clockwise seen from above.</summary>
        private float[] ApproachAngles(Entity node, List<Entity> edges)
        {
            var angles = new float[edges.Count];
            for (int i = 0; i < edges.Count; i++)
            {
                Edge edge = EntityManager.GetComponentData<Edge>(edges[i]);
                Bezier4x3 curve = EntityManager.GetComponentData<Curve>(edges[i]).m_Bezier;
                float3 direction = edge.m_Start == node ? curve.b - curve.a : curve.c - curve.d;
                angles[i] = math.degrees(math.atan2(direction.z, direction.x));
            }
            return angles;
        }

        private List<LaneInfo> CollectLanes(Entity node, List<Entity> edges)
        {
            var result = new List<LaneInfo>();
            DynamicBuffer<SubLane> subLanes = EntityManager.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity laneEntity = subLanes[i].m_SubLane;
                if (!EntityManager.HasComponent<LaneSignal>(laneEntity) || EntityManager.HasComponent<SecondaryLane>(laneEntity))
                    continue;
                Lane lane = EntityManager.GetComponentData<Lane>(laneEntity);
                int source = EdgeIndexOf(lane.m_StartNode, edges);
                int target = EdgeIndexOf(lane.m_EndNode, edges);
                var info = new LaneInfo { Lane = laneEntity };

                if (EntityManager.HasComponent<PedestrianLane>(laneEntity))
                {
                    // A crosswalk joins the two sidewalks of the road it
                    // crosses, so both of its ends belong to that edge.
                    int crossed = source >= 0 ? source : target;
                    if (crossed < 0)
                        continue;
                    info.Key = new MovementKey { Source = crossed, Target = -1, Kind = MovementKind.Pedestrian };
                    info.Flags = JunctionLaneFlags.Pedestrian;
                }
                else
                {
                    if (source < 0 || target < 0)
                        continue;
                    bool track = !EntityManager.HasComponent<CarLane>(laneEntity) && EntityManager.HasComponent<TrackLane>(laneEntity);
                    MovementKind kind = track ? MovementKind.Track : KindOf(EntityManager.GetComponentData<CarLane>(laneEntity).m_Flags);
                    info.Key = new MovementKey { Source = source, Target = target, Kind = kind };
                    info.Flags = track ? JunctionLaneFlags.Track : JunctionLaneFlags.None;
                    info.Approach = ConnectedLane(edges[source], lane.m_StartNode, atEnd: true);
                    info.Exit = ConnectedLane(edges[target], lane.m_EndNode, atEnd: false);
                }
                result.Add(info);
            }
            return result;
        }

        private static MovementKind KindOf(CarLaneFlags flags)
        {
            if ((flags & (CarLaneFlags.UTurnLeft | CarLaneFlags.UTurnRight)) != 0)
                return MovementKind.UTurn;
            if ((flags & (CarLaneFlags.TurnLeft | CarLaneFlags.GentleTurnLeft)) != 0)
                return MovementKind.Left;
            if ((flags & (CarLaneFlags.TurnRight | CarLaneFlags.GentleTurnRight)) != 0)
                return MovementKind.Right;
            return MovementKind.Straight;
        }

        /// <summary>Index of the edge that owns the path node, or -1.</summary>
        private static int EdgeIndexOf(PathNode pathNode, List<Entity> edges)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                if (pathNode.OwnerEquals(new PathNode(edges[i], 0)))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// The lane of <paramref name="edge"/> that ends (or starts) at <paramref name="pathNode"/>:
        /// the road lane feeding a junction lane, or the one it leads into.
        /// </summary>
        private Entity ConnectedLane(Entity edge, PathNode pathNode, bool atEnd)
        {
            if (!EntityManager.HasBuffer<SubLane>(edge))
                return Entity.Null;
            DynamicBuffer<SubLane> subLanes = EntityManager.GetBuffer<SubLane>(edge, true);
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity candidate = subLanes[i].m_SubLane;
                if (!EntityManager.HasComponent<Lane>(candidate))
                    continue;
                Lane lane = EntityManager.GetComponentData<Lane>(candidate);
                if ((atEnd ? lane.m_EndNode : lane.m_StartNode).Equals(pathNode))
                    return candidate;
            }
            return Entity.Null;
        }

        /// <summary>
        /// Relations between movements from the game's lane overlaps. Each
        /// overlap between two lanes of different movements is classified;
        /// a movement pair takes the strictest relation any of its lane pairs
        /// has.
        /// </summary>
        private ConflictMatrix Conflicts(List<LaneInfo> lanes, List<MovementKey> keys, JunctionModel model)
        {
            var matrix = new ConflictMatrix(keys.Count);
            var movementOfLane = new Dictionary<Entity, int>();
            foreach (LaneInfo lane in lanes)
                movementOfLane[lane.Lane] = keys.IndexOf(lane.Key);

            foreach (LaneInfo lane in lanes)
            {
                if (!EntityManager.HasBuffer<LaneOverlap>(lane.Lane))
                    continue;
                int a = movementOfLane[lane.Lane];
                DynamicBuffer<LaneOverlap> overlaps = EntityManager.GetBuffer<LaneOverlap>(lane.Lane, true);
                for (int i = 0; i < overlaps.Length; i++)
                {
                    LaneOverlap overlap = overlaps[i];
                    if (!movementOfLane.TryGetValue(overlap.m_Other, out int b) || a == b)
                        continue;
                    PathContact contact = (overlap.m_Flags & OverlapFlags.MergeEnd) != 0 ? PathContact.Merge
                        : (overlap.m_Flags & OverlapFlags.MergeStart) != 0 ? PathContact.Diverge
                        : PathContact.Cross;
                    Relation relation = ConflictRules.Classify(model.Movements[a], model.Movements[b], contact, model.OppositeOf, model.LeftHandTraffic);
                    matrix.Merge(a, b, relation);
                }
            }
            return matrix;
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
