using System;
using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using TLL.Components;
using TLL.Core.Planning;
using Unity.Entities;

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
            List<LaneInfo> lanes = CollectLanes(em, node, edges);
            if (lanes.Count == 0)
                return null;

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
                LeftHandTraffic = leftHandTraffic,
            };
            foreach (MovementKey key in keys)
            {
                int laneCount = 0;
                foreach (LaneInfo lane in lanes)
                    laneCount += lane.Key.Equals(key) ? 1 : 0;
                model.Movements.Add(new Movement(key.Source, key.Target, key.Kind, laneCount));
            }
            model.Conflicts = Conflicts(em, lanes, keys, model);
            // Safety net: the circle model catches crossing paths whose lanes
            // the game did not record as overlapping. Merges come from the
            // lanes alone (see ChordModel.Classify). The model needs distinct
            // approach directions to place its points.
            if (ChordModel.SmallestGap(angles) >= 1f)
                model.Conflicts.Tighten(ChordModel.Classify(model.Movements, angles, model.OppositeOf, model.LeftHandTraffic, includeMerges: false));

            return new JunctionLayout { Edges = edges, Angles = angles, Lanes = lanes, Keys = keys, Model = model };
        }

        private static List<LaneInfo> CollectLanes(EntityManager em, Entity node, List<Entity> edges)
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
                var info = new LaneInfo { Lane = laneEntity };

                if (em.HasComponent<PedestrianLane>(laneEntity))
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
            var matrix = new ConflictMatrix(keys.Count);
            var movementOfLane = new Dictionary<Entity, int>();
            foreach (LaneInfo lane in lanes)
                movementOfLane[lane.Lane] = keys.IndexOf(lane.Key);

            foreach (LaneInfo lane in lanes)
            {
                if (!em.HasBuffer<LaneOverlap>(lane.Lane))
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
