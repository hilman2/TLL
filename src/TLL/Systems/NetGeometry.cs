using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;
using Edge = Game.Net.Edge;

namespace TLL.Systems
{
    /// <summary>
    /// Road geometry around nodes, shared by the systems that number a
    /// junction's approaches. An approach is identified by its position in
    /// the node's ConnectedEdge buffer; every TLL system uses that order.
    /// </summary>
    internal static class NetGeometry
    {
        public static List<Entity> ConnectedEdges(EntityManager em, Entity node)
        {
            var edges = new List<Entity>();
            DynamicBuffer<ConnectedEdge> connected = em.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < connected.Length; i++)
                edges.Add(connected[i].m_Edge);
            return edges;
        }

        /// <summary>Unit direction, in the ground plane, from the node out along the edge.</summary>
        public static float2 Outward(EntityManager em, Entity node, Entity edge)
        {
            Edge e = em.GetComponentData<Edge>(edge);
            Bezier4x3 curve = em.GetComponentData<Curve>(edge).m_Bezier;
            float3 direction = e.m_Start == node ? curve.b - curve.a : curve.c - curve.d;
            return math.normalizesafe(direction.xz);
        }

        /// <summary>Direction of each approach in degrees, counter-clockwise seen from above.</summary>
        public static float[] ApproachAngles(EntityManager em, Entity node, List<Entity> edges)
        {
            var angles = new float[edges.Count];
            for (int i = 0; i < edges.Count; i++)
            {
                float2 d = Outward(em, node, edges[i]);
                angles[i] = math.degrees(math.atan2(d.y, d.x));
            }
            return angles;
        }

        /// <summary>The node at the other end of the edge.</summary>
        public static Entity OtherEnd(EntityManager em, Entity edge, Entity node)
        {
            Edge e = em.GetComponentData<Edge>(edge);
            return e.m_Start == node ? e.m_End : e.m_Start;
        }
    }
}
