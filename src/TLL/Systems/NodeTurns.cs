using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using TLL.Core.Planning;
using Unity.Entities;

namespace TLL.Systems
{
    /// <summary>One way vehicles can go through a node: from one road into another.</summary>
    internal struct NodeTurn
    {
        public Entity From;
        public Entity To;

        /// <summary>Index of <see cref="From"/> and <see cref="To"/> in the node's list of roads.</summary>
        public int Source;
        public int Target;

        public MovementKind Kind;

        /// <summary>All its lanes are forbidden, by TLL or a road upgrade of the game.</summary>
        public bool Forbidden;
    }

    /// <summary>
    /// The ways vehicles can go through a node, read from its car lanes, with
    /// or without traffic lights. JunctionAnalysis reads the signalled lanes
    /// only, and splits them into movements for the signal plan; this is for
    /// the turn rules, which apply at any junction.
    /// </summary>
    internal static class NodeTurns
    {
        public static List<NodeTurn> Collect(EntityManager em, Entity node, List<Entity> edges)
        {
            var result = new List<NodeTurn>();
            var allowed = new HashSet<(int, int)>();
            if (!em.HasBuffer<SubLane>(node))
                return result;
            DynamicBuffer<SubLane> lanes = em.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity entity = lanes[i].m_SubLane;
                // A master lane stands for its group, which has the real lanes.
                if (!em.HasComponent<CarLane>(entity) || em.HasComponent<MasterLane>(entity) || em.HasComponent<TrackLane>(entity))
                    continue;
                Lane lane = em.GetComponentData<Lane>(entity);
                int source = IndexOf(lane.m_StartNode, edges);
                int target = IndexOf(lane.m_EndNode, edges);
                if (source < 0 || target < 0)
                    continue;
                CarLaneFlags flags = em.GetComponentData<CarLane>(entity).m_Flags;
                bool forbidden = (flags & CarLaneFlags.Forbidden) != 0;
                if (!forbidden)
                    allowed.Add((source, target));
                if (result.Exists(t => t.Source == source && t.Target == target))
                    continue;
                result.Add(new NodeTurn
                {
                    From = edges[source],
                    To = edges[target],
                    Source = source,
                    Target = target,
                    Kind = JunctionAnalysis.KindOf(flags),
                });
            }
            for (int i = 0; i < result.Count; i++)
            {
                NodeTurn turn = result[i];
                turn.Forbidden = !allowed.Contains((turn.Source, turn.Target));
                result[i] = turn;
            }
            result.Sort((a, b) => a.Source != b.Source ? a.Source.CompareTo(b.Source) : a.Target.CompareTo(b.Target));
            return result;
        }

        private static int IndexOf(PathNode pathNode, List<Entity> edges)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                if (pathNode.OwnerEquals(new PathNode(edges[i], 0)))
                    return i;
            }
            return -1;
        }
    }
}
