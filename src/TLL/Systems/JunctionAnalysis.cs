using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using TLL.Components;
using TLL.Core.Planning;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Systems
{
    /// <summary>Movement key while collecting lanes: source approach, target approach (-1 for a crosswalk), kind.</summary>
    internal struct MovementKey : IEquatable<MovementKey>
    {
        public int Source;
        public int Target;
        public MovementKind Kind;

        public bool Equals(MovementKey other) => Source == other.Source && Target == other.Target && Kind == other.Kind;

        public override int GetHashCode() => (Source * 397) ^ (Target * 31) ^ (int)Kind;
    }

    internal struct LaneInfo
    {
        public Entity Lane;
        public MovementKey Key;
        public Entity Approach;
        public Entity Exit;
        public JunctionLaneFlags Flags;

        /// <summary>
        /// The game's master lane of a group of parallel lanes: it carries a
        /// signal but no vehicles, and one exists for every pair of lane
        /// groups, so it joins movements that share no real lane.
        /// </summary>
        public bool Master;
    }

    /// <summary>What <see cref="JunctionAnalysis.Analyse"/> finds at a junction.</summary>
    internal sealed class JunctionLayout
    {
        /// <summary>The approaches, in the order of the node's ConnectedEdge buffer.</summary>
        public List<Entity> Edges;
        public float[] Angles;
        public List<LaneInfo> Lanes;

        /// <summary>The movements in their fixed order; index i is movement i everywhere.</summary>
        public List<MovementKey> Keys;
        public JunctionModel Model;

        /// <summary>
        /// Signalled vehicle lanes that belong to no movement, such as side
        /// connections with one end in the node. They are set to give way
        /// once; nobody else updates their signal while TLL runs the node.
        /// </summary>
        public List<Entity> Unassigned;
    }

    /// <summary>
    /// Reads a junction's lanes into movements and their conflicts. The
    /// junction set-up and the autopilot both use it, so both number the
    /// movements the same way.
    /// </summary>
    internal static class JunctionAnalysis
    {
        /// <returns>Null if the node has no signalled lanes.</returns>
        public static JunctionLayout Analyse(EntityManager em, Entity node, bool leftHandTraffic)
        {
            List<Entity> edges = NetGeometry.ConnectedEdges(em, node);
            float[] angles = NetGeometry.ApproachAngles(em, node, edges);
            var unassigned = new List<Entity>();
            List<LaneInfo> lanes = CollectLanes(em, node, edges, unassigned);
            if (lanes.Count == 0)
                return null;

            // Movements in a fixed order of their approaches. The approaches
            // are numbered as the node lists its roads, which the game may
            // change after loading, so saved plans are matched to movements
            // by their roads, not by index (see JunctionInitSystem).
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
                LeftHandTraffic = leftHandTraffic,
            };
            foreach (MovementKey key in keys)
            {
                int laneCount = 0;
                foreach (LaneInfo lane in lanes)
                    laneCount += lane.Key.Equals(key) && !lane.Master ? 1 : 0;
                model.Movements.Add(new Movement(key.Source, key.Target, key.Kind, Math.Max(1, laneCount)));
            }
            // An approach lane feeding junction lanes of several movements is
            // a shared lane (e.g. straight and left): its movements can only
            // move together. Master lanes connect every pair of lane groups
            // and would make every approach one shared lane.
            var byApproachLane = new Dictionary<Entity, ulong>();
            foreach (LaneInfo lane in lanes)
            {
                if (lane.Approach == Entity.Null || lane.Master || (lane.Flags & JunctionLaneFlags.Pedestrian) != 0)
                    continue;
                byApproachLane.TryGetValue(lane.Approach, out ulong movements);
                byApproachLane[lane.Approach] = movements | 1UL << keys.IndexOf(lane.Key);
            }
            foreach (ulong movements in byApproachLane.Values)
            {
                for (int a = 0; a < keys.Count; a++)
                {
                    for (int b = a + 1; b < keys.Count; b++)
                    {
                        if ((movements & (1UL << a)) != 0 && (movements & (1UL << b)) != 0)
                            model.ShareLane(a, b);
                    }
                }
            }
            model.Conflicts = Conflicts(em, lanes, keys, model);
            // Safety net: the circle model catches crossing paths whose lanes
            // the game did not record as overlapping. Merges come from the
            // lanes alone (see ChordModel.Classify). The model needs distinct
            // approach directions to place its points.
            if (ChordModel.SmallestGap(angles) >= 1f)
                model.Conflicts.Tighten(ChordModel.Classify(model.Movements, angles, model.OppositeOf, model.LeftHandTraffic, includeMerges: false));

            return new JunctionLayout { Edges = edges, Angles = angles, Lanes = lanes, Keys = keys, Model = model, Unassigned = unassigned };
        }

        /// <summary>
        /// The road lanes further up each approach lane of the layout, out to
        /// <paramref name="range"/> metres from the stop line. The chain
        /// follows the road across plain nodes, where one road continues into
        /// the next piece of the same road, and stops at any node with signals
        /// or more than two roads: what lies beyond belongs to the previous
        /// junction.
        /// </summary>
        public static List<DetectorLane> DetectorChain(EntityManager em, Entity node, JunctionLayout layout, float range)
        {
            var result = new List<DetectorLane>();
            var done = new HashSet<Entity>();
            foreach (LaneInfo info in layout.Lanes)
            {
                // A master lane carries no vehicles to detect.
                if (info.Master || info.Approach == Entity.Null || !done.Add(info.Approach))
                    continue;
                int edge = info.Key.Source;
                if (edge < 0 || edge >= layout.Edges.Count)
                    continue;
                Follow(em, info.Approach, info.Approach, 0f, layout.Edges[edge], node, range, result, new HashSet<Entity>());
            }
            return result;
        }

        /// <summary>Most lanes watched per approach lane; a limit against unusual road layouts, not a design value.</summary>
        private const int kMaxChain = 16;

        private static void Follow(EntityManager em, Entity approach, Entity lane, float offset, Entity edge, Entity downstreamNode,
            float range, List<DetectorLane> result, HashSet<Entity> visited)
        {
            if (!em.HasComponent<Curve>(lane) || !em.HasComponent<Lane>(lane))
                return;
            float start = offset + em.GetComponentData<Curve>(lane).m_Length;
            if (start >= range || visited.Count >= kMaxChain)
                return;
            Entity upstreamNode = NetGeometry.OtherEnd(em, edge, downstreamNode);
            if (!IsPlainNode(em, upstreamNode))
                return;
            Entity upstreamEdge = Entity.Null;
            foreach (Entity e in NetGeometry.ConnectedEdges(em, upstreamNode))
            {
                if (e != edge)
                    upstreamEdge = e;
            }
            if (upstreamEdge == Entity.Null)
                return;

            PathNode entry = em.GetComponentData<Lane>(lane).m_StartNode;
            // Usually a short connecting lane of the node joins the two road
            // pieces; the road lane before it is found from its start.
            DynamicBuffer<SubLane> nodeLanes = em.GetBuffer<SubLane>(upstreamNode, true);
            bool found = false;
            for (int i = 0; i < nodeLanes.Length; i++)
            {
                Entity feeder = nodeLanes[i].m_SubLane;
                if (!IsVehicleLane(em, feeder) || visited.Contains(feeder))
                    continue;
                Lane feederLane = em.GetComponentData<Lane>(feeder);
                if (!feederLane.m_EndNode.Equals(entry))
                    continue;
                found = true;
                visited.Add(feeder);
                result.Add(new DetectorLane { Approach = approach, Lane = feeder, Offset = start });
                float beyond = start + (em.HasComponent<Curve>(feeder) ? em.GetComponentData<Curve>(feeder).m_Length : 0f);
                Entity before = ConnectedLane(em, upstreamEdge, feederLane.m_StartNode, atEnd: true);
                if (before != Entity.Null && beyond < range && visited.Add(before))
                {
                    result.Add(new DetectorLane { Approach = approach, Lane = before, Offset = beyond });
                    Follow(em, approach, before, beyond, upstreamEdge, upstreamNode, range, result, visited);
                }
            }
            if (found)
                return;
            // Without a connecting lane the road lanes meet directly.
            Entity direct = ConnectedLane(em, upstreamEdge, entry, atEnd: true);
            if (direct != Entity.Null && visited.Add(direct))
            {
                result.Add(new DetectorLane { Approach = approach, Lane = direct, Offset = start });
                Follow(em, approach, direct, start, upstreamEdge, upstreamNode, range, result, visited);
            }
        }

        /// <summary>A node where one road simply continues: exactly two roads and no signals.</summary>
        private static bool IsPlainNode(EntityManager em, Entity node)
        {
            if (node == Entity.Null || em.HasComponent<TrafficLights>(node) || !em.HasBuffer<ConnectedEdge>(node))
                return false;
            return em.GetBuffer<ConnectedEdge>(node, true).Length == 2 && em.HasBuffer<SubLane>(node);
        }

        private static bool IsVehicleLane(EntityManager em, Entity lane)
        {
            return em.HasComponent<Lane>(lane) && !em.HasComponent<PedestrianLane>(lane)
                && (em.HasComponent<CarLane>(lane) || em.HasComponent<TrackLane>(lane));
        }

        private static List<LaneInfo> CollectLanes(EntityManager em, Entity node, List<Entity> edges, List<Entity> unassigned)
        {
            var result = new List<LaneInfo>();
            DynamicBuffer<SubLane> subLanes = em.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity laneEntity = subLanes[i].m_SubLane;
                if (!em.HasComponent<LaneSignal>(laneEntity) || em.HasComponent<SecondaryLane>(laneEntity))
                    continue;
                Lane lane = em.GetComponentData<Lane>(laneEntity);
                int source = EdgeIndexOf(lane.m_StartNode, edges);
                int target = EdgeIndexOf(lane.m_EndNode, edges);
                var info = new LaneInfo { Lane = laneEntity, Master = em.HasComponent<MasterLane>(laneEntity) };

                if (em.HasComponent<PedestrianLane>(laneEntity))
                {
                    // A crosswalk joins the two sidewalks of the road it
                    // crosses, so its ends usually belong to that edge. At a
                    // pedestrian light without a junction, and for the middle
                    // piece of a crosswalk split by medians, both ends belong
                    // to the node; there the road is found by where the
                    // crosswalk lies.
                    int crossed = source >= 0 ? source : target >= 0 ? target : CrossedApproach(em, node, edges, laneEntity);
                    if (crossed < 0)
                        continue;
                    info.Key = new MovementKey { Source = crossed, Target = -1, Kind = MovementKind.Pedestrian };
                    info.Flags = JunctionLaneFlags.Pedestrian;
                }
                else
                {
                    if (source < 0 || target < 0)
                    {
                        unassigned.Add(laneEntity);
                        continue;
                    }
                    bool track = !em.HasComponent<CarLane>(laneEntity) && em.HasComponent<TrackLane>(laneEntity);
                    MovementKind kind = track ? MovementKind.Track : KindOf(em.GetComponentData<CarLane>(laneEntity).m_Flags);
                    info.Key = new MovementKey { Source = source, Target = target, Kind = kind };
                    info.Flags = track ? JunctionLaneFlags.Track : JunctionLaneFlags.None;
                    info.Approach = ConnectedLane(em, edges[source], lane.m_StartNode, atEnd: true);
                    info.Exit = ConnectedLane(em, edges[target], lane.m_EndNode, atEnd: false);
                }
                result.Add(info);
            }
            return result;
        }

        /// <summary>
        /// The approach a crosswalk with both ends in the node lies across:
        /// the one reaching out furthest towards the crosswalk's middle. At a
        /// pedestrian light without a junction the middle is at the node and
        /// either road serves; the crosswalk crosses both.
        /// </summary>
        private static int CrossedApproach(EntityManager em, Entity node, List<Entity> edges, Entity lane)
        {
            if (edges.Count == 0 || !em.HasComponent<Curve>(lane) || !em.HasComponent<Node>(node))
                return -1;
            float3 middle = MathUtils.Position(em.GetComponentData<Curve>(lane).m_Bezier, 0.5f);
            float2 fromNode = (middle - em.GetComponentData<Node>(node).m_Position).xz;
            int best = 0;
            float bestReach = float.MinValue;
            for (int i = 0; i < edges.Count; i++)
            {
                float reach = math.dot(NetGeometry.Outward(em, node, edges[i]), fromNode);
                if (reach > bestReach)
                {
                    bestReach = reach;
                    best = i;
                }
            }
            return best;
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
        private static Entity ConnectedLane(EntityManager em, Entity edge, PathNode pathNode, bool atEnd)
        {
            if (!em.HasBuffer<SubLane>(edge))
                return Entity.Null;
            DynamicBuffer<SubLane> subLanes = em.GetBuffer<SubLane>(edge, true);
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity candidate = subLanes[i].m_SubLane;
                if (!em.HasComponent<Lane>(candidate))
                    continue;
                Lane lane = em.GetComponentData<Lane>(candidate);
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
        private static ConflictMatrix Conflicts(EntityManager em, List<LaneInfo> lanes, List<MovementKey> keys, JunctionModel model)
        {
            // Master lanes are left out: they carry no vehicles, and their
            // overlaps stand for their whole lane group at once.
            var matrix = new ConflictMatrix(keys.Count);
            var movementOfLane = new Dictionary<Entity, int>();
            foreach (LaneInfo lane in lanes)
            {
                if (!lane.Master)
                    movementOfLane[lane.Lane] = keys.IndexOf(lane.Key);
            }

            foreach (LaneInfo lane in lanes)
            {
                if (lane.Master || !em.HasBuffer<LaneOverlap>(lane.Lane))
                    continue;
                int a = movementOfLane[lane.Lane];
                DynamicBuffer<LaneOverlap> overlaps = em.GetBuffer<LaneOverlap>(lane.Lane, true);
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
    }
}
