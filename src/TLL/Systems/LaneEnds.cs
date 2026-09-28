using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Systems
{
    /// <summary>Where one car lane of a road meets a junction.</summary>
    internal struct LaneEnd
    {
        public Entity Edge;

        /// <summary>The lane's index on its road, as rules name it (LaneConnectionRule).</summary>
        public byte Index;

        /// <summary>The road's lane.</summary>
        public Entity Lane;

        /// <summary>
        /// The lane's path node at the junction: the one a junction lane
        /// leading out of it starts from, or leading into it ends at.
        /// </summary>
        public PathNode Node;

        public float3 Position;

        /// <summary>Direction of travel there, of unit length.</summary>
        public float3 Direction;

        /// <summary>Traffic on the lane drives into the junction; otherwise out of it.</summary>
        public bool Incoming;
    }

    /// <summary>The car lanes of a junction's roads where they meet it.</summary>
    internal static class LaneEnds
    {
        public static List<LaneEnd> Collect(EntityManager em, Entity node, List<Entity> edges)
        {
            var result = new List<LaneEnd>();
            foreach (Entity edge in edges)
            {
                if (!em.HasBuffer<SubLane>(edge))
                    continue;
                Game.Net.Edge road = em.GetComponentData<Game.Net.Edge>(edge);
                if (road.m_Start == road.m_End)
                    continue;
                // Where the junction lies along the road: 0 at its start, 1 at its end.
                float junction = road.m_Start == node ? 0f : 1f;
                DynamicBuffer<SubLane> lanes = em.GetBuffer<SubLane>(edge, true);
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity lane = lanes[i].m_SubLane;
                    // A master lane stands for its group; two-way lanes
                    // have no direction to connect by.
                    if (!em.HasComponent<CarLane>(lane) || em.HasComponent<MasterLane>(lane) || !em.HasComponent<Curve>(lane)
                        || !em.HasComponent<EdgeLane>(lane)
                        || (em.GetComponentData<CarLane>(lane).m_Flags & CarLaneFlags.Twoway) != 0)
                        continue;
                    // Unless a road is a single curve, the game builds each
                    // of its lanes in two pieces, one per half of the road
                    // (LaneSystem.CreateEdgeLanes), and both pieces carry the
                    // lane's index. Only the piece that reaches the junction
                    // counts. Taking the nearer end of every piece instead
                    // also marked the lane in the middle of the road, and
                    // Find then matched either of the two by index.
                    float2 delta = em.GetComponentData<EdgeLane>(lane).m_EdgeDelta;
                    bool incoming = math.abs(delta.y - junction) < 0.001f;
                    if (!incoming && math.abs(delta.x - junction) >= 0.001f)
                        continue;
                    Bezier4x3 curve = em.GetComponentData<Curve>(lane).m_Bezier;
                    Lane path = em.GetComponentData<Lane>(lane);
                    PathNode at = incoming ? path.m_EndNode : path.m_StartNode;
                    result.Add(new LaneEnd
                    {
                        Edge = edge,
                        Index = (byte)(at.GetLaneIndex() & 0xFF),
                        Lane = lane,
                        Node = at,
                        Position = incoming ? curve.d : curve.a,
                        Direction = math.normalizesafe(MathUtils.Tangent(curve, incoming ? 1f : 0f)),
                        Incoming = incoming,
                    });
                }
            }
            return result;
        }

        /// <summary>The lane end a junction lane starts from (<paramref name="incoming"/>) or ends at, or -1.</summary>
        public static int Find(List<LaneEnd> ends, PathNode node, bool incoming)
        {
            for (int i = 0; i < ends.Count; i++)
            {
                if (ends[i].Incoming == incoming && ends[i].Node.Equals(node))
                    return i;
            }
            return -1;
        }

        /// <summary>The lane end of a road with the given index, or -1.</summary>
        public static int Find(List<LaneEnd> ends, Entity edge, byte index, bool incoming)
        {
            for (int i = 0; i < ends.Count; i++)
            {
                if (ends[i].Incoming == incoming && ends[i].Edge == edge && ends[i].Index == index)
                    return i;
            }
            return -1;
        }
    }
}
