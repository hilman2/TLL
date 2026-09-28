using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using TLL.Components;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Systems
{
    /// <summary>
    /// Solid lines before a junction (<see cref="SolidLineRule"/>): on the
    /// last road pieces of an approach, the lanes heading for the junction
    /// lose the game's permission to change lanes.
    /// </summary>
    /// <remarks>
    /// The game has no hard lane-change ban. A vehicle chooses its lane by
    /// cost (CarLaneSelectIterator), and a change from a lane without
    /// SlaveLaneFlags.AllowChange costs five times as much, 35 times with a
    /// turn ahead; the game builds its lanes inside junctions that way.
    /// Vehicles plan their lane some lanes ahead, so they change before the
    /// solid lines instead, and one that must still change can: nobody gets
    /// stuck in a lane that does not lead where it has to go.
    /// </remarks>
    internal static class SolidLines
    {
        /// <summary>Most road pieces solid lines reach back.</summary>
        public const int MaxPieces = 8;

        /// <summary>
        /// The road pieces of an approach from the junction back, each with
        /// the node its traffic heads for: the approach at the junction,
        /// then across plain nodes, where the road simply continues, to at
        /// most <paramref name="pieces"/> pieces. Stops at the previous
        /// junction.
        /// </summary>
        public static List<(Entity Edge, Entity Toward)> Chain(EntityManager em, Entity node, Entity edge, int pieces)
        {
            var chain = new List<(Entity Edge, Entity Toward)>();
            Entity toward = node;
            while (chain.Count < pieces && edge != Entity.Null && em.Exists(edge) && em.HasComponent<Edge>(edge))
            {
                chain.Add((edge, toward));
                Entity upstream = NetGeometry.OtherEnd(em, edge, toward);
                if (!JunctionAnalysis.IsPlainNode(em, upstream))
                    break;
                Entity next = Entity.Null;
                foreach (Entity e in NetGeometry.ConnectedEdges(em, upstream))
                {
                    if (e != edge)
                        next = e;
                }
                if (next == Entity.Null || chain.Exists(c => c.Edge == next))
                    break;
                toward = upstream;
                edge = next;
            }
            return chain;
        }

        /// <summary>The length of each of the first pieces of a chain added up, in metres: element k is the length of k + 1 pieces.</summary>
        public static float[] Lengths(EntityManager em, List<(Entity Edge, Entity Toward)> chain)
        {
            var result = new float[chain.Count];
            float sum = 0f;
            for (int k = 0; k < chain.Count; k++)
            {
                if (em.HasComponent<Curve>(chain[k].Edge))
                    sum += em.GetComponentData<Curve>(chain[k].Edge).m_Length;
                result[k] = sum;
            }
            return result;
        }

        /// <summary>
        /// Brings the bans on the roads before a junction in line with its
        /// rules: the pieces a rule covers get a ban toward the junction, the
        /// ones beyond lose it. Roads whose bans changed are rebuilt, which
        /// applies them to their lanes (<see cref="ApplyToLanes"/>).
        /// </summary>
        public static void Sync(EntityManager em, Entity node)
        {
            if (!em.Exists(node) || !em.HasComponent<Node>(node))
                return;
            var wanted = new Dictionary<Entity, int>();
            if (em.HasBuffer<SolidLineRule>(node))
            {
                DynamicBuffer<SolidLineRule> rules = em.GetBuffer<SolidLineRule>(node, true);
                for (int i = 0; i < rules.Length; i++)
                    wanted[rules[i].Edge] = rules[i].Pieces;
            }
            foreach (Entity approach in NetGeometry.ConnectedEdges(em, node))
            {
                wanted.TryGetValue(approach, out int pieces);
                List<(Entity Edge, Entity Toward)> chain = Chain(em, node, approach, MaxPieces);
                for (int k = 0; k < chain.Count; k++)
                {
                    if (SetBan(em, chain[k].Edge, chain[k].Toward, node, k < pieces))
                        em.AddComponent<RebuildRequest>(chain[k].Edge);
                }
            }
        }

        /// <summary>Adds or removes one ban on a road piece. Returns whether anything changed.</summary>
        private static bool SetBan(EntityManager em, Entity edge, Entity toward, Entity junction, bool on)
        {
            bool has = em.HasBuffer<LaneChangeBan>(edge);
            if (!has && !on)
                return false;
            DynamicBuffer<LaneChangeBan> bans = has ? em.GetBuffer<LaneChangeBan>(edge) : em.AddBuffer<LaneChangeBan>(edge);
            for (int i = 0; i < bans.Length; i++)
            {
                if (bans[i].Toward != toward || bans[i].Junction != junction)
                    continue;
                if (on)
                    return false;
                bans.RemoveAt(i);
                return true;
            }
            if (!on)
                return false;
            bans.Add(new LaneChangeBan { Toward = toward, Junction = junction });
            return true;
        }

        /// <summary>
        /// Takes the permission to change lanes from the lanes the game has
        /// just built on a road piece with bans, where they head for a
        /// banned node. Lanes of a group change among themselves (SlaveLane);
        /// a road with one lane each way has none to take.
        /// </summary>
        public static void ApplyToLanes(EntityManager em, Entity edge, List<Entity> lanes)
        {
            DynamicBuffer<LaneChangeBan> bans = em.GetBuffer<LaneChangeBan>(edge, true);
            if (bans.Length == 0)
                return;
            foreach (Entity lane in lanes)
            {
                if (!em.HasComponent<SlaveLane>(lane) || !em.HasComponent<Curve>(lane))
                    continue;
                Bezier4x3 curve = em.GetComponentData<Curve>(lane).m_Bezier;
                bool banned = false;
                for (int i = 0; i < bans.Length && !banned; i++)
                    banned = HeadsFor(em, curve, bans[i].Toward);
                if (!banned)
                    continue;
                SlaveLane slave = em.GetComponentData<SlaveLane>(lane);
                if ((slave.m_Flags & SlaveLaneFlags.AllowChange) == 0)
                    continue;
                slave.m_Flags &= ~SlaveLaneFlags.AllowChange;
                em.SetComponentData(lane, slave);
            }
        }

        /// <summary>Whether a lane of a road piece runs toward <paramref name="node"/>: its end is nearer to it than its start.</summary>
        public static bool HeadsFor(EntityManager em, Bezier4x3 curve, Entity node)
        {
            if (node == Entity.Null || !em.Exists(node) || !em.HasComponent<Node>(node))
                return false;
            float3 at = em.GetComponentData<Node>(node).m_Position;
            return math.distancesq(curve.d, at) < math.distancesq(curve.a, at);
        }

        /// <summary>The car lanes of a chain that head for its junction, for drawing them.</summary>
        public static List<Entity> Lanes(EntityManager em, List<(Entity Edge, Entity Toward)> chain)
        {
            var result = new List<Entity>();
            foreach ((Entity edge, Entity toward) in chain)
            {
                if (!em.HasBuffer<SubLane>(edge))
                    continue;
                DynamicBuffer<SubLane> lanes = em.GetBuffer<SubLane>(edge, true);
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity lane = lanes[i].m_SubLane;
                    if (em.HasComponent<SlaveLane>(lane) && em.HasComponent<CarLane>(lane) && em.HasComponent<Curve>(lane)
                        && HeadsFor(em, em.GetComponentData<Curve>(lane).m_Bezier, toward))
                        result.Add(lane);
                }
            }
            return result;
        }
    }
}
