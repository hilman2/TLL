using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using TLL.Components;
using TLL.Systems;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using CarLane = Game.Net.CarLane;
using SubLane = Game.Net.SubLane;

namespace TLL.UI
{
    /// <summary>
    /// Tool for changing which lane leads into which at one junction. Every
    /// car lane of its roads is marked where it meets the junction: blue
    /// where traffic comes in, green where it goes out. A click on a blue
    /// mark picks that lane; a click on a green one then connects the two,
    /// or takes their connection away. Right click drops the picked lane, and
    /// with none picked, closes the tool. The changes are rules on the
    /// junction (LaneConnectionRule), applied whenever the game builds its
    /// lanes (LaneRuleSystem).
    /// </summary>
    public partial class LaneToolSystem : ToolBaseSystem
    {
        public const string kToolID = "TLL.LaneTool";

        /// <summary>How far from a lane's mark, in metres, the pointer still picks it.</summary>
        private const float kPickRadius = 1.8f;

        private static readonly Color kIn = new Color(0.3f, 0.65f, 1f, 0.95f);
        private static readonly Color kOut = new Color(0.3f, 0.85f, 0.45f, 0.95f);
        private static readonly Color kLane = new Color(1f, 1f, 1f, 0.55f);
        private static readonly Color kPicked = new Color(0.3f, 0.65f, 1f, 1f);
        private static readonly Color kAdded = new Color(0.3f, 0.85f, 0.45f, 1f);
        private static readonly Color kRemoved = new Color(0.9f, 0.25f, 0.25f, 0.9f);
        private static readonly Color kForbidden = new Color(0.96f, 0.6f, 0.2f, 0.8f);

        private OverlayRenderSystem m_Overlay;
        private Entity m_Node;
        private List<Entity> m_Edges = new List<Entity>();
        private List<LaneEnd> m_Ends = new List<LaneEnd>();
        private int m_Picked = -1;
        private int m_Hovered = -1;

        public override string toolID => kToolID;

        public bool IsActive => m_ToolSystem.activeTool == this;

        /// <summary>The junction being edited, or Null.</summary>
        public Entity Node => IsActive ? m_Node : Entity.Null;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
        }

        /// <summary>Starts editing the lanes of <paramref name="node"/>, or stops when it is already open.</summary>
        public void Toggle(Entity node)
        {
            if (IsActive)
            {
                m_ToolSystem.activeTool = m_DefaultToolSystem;
                return;
            }
            if (node == Entity.Null || !EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
                return;
            m_Node = node;
            m_Picked = -1;
            m_ToolSystem.activeTool = this;
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            m_Hovered = -1;
        }

        public override PrefabBase GetPrefab()
        {
            return null;
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            return false;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain | TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.TramTrack | Layer.PublicTransportRoad;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            try
            {
                if (m_Node == Entity.Null || !EntityManager.Exists(m_Node) || EntityManager.HasComponent<Deleted>(m_Node))
                {
                    m_ToolSystem.activeTool = m_DefaultToolSystem;
                    return inputDeps;
                }
                // The lanes change with every rebuild of the junction, the
                // player's own changes included.
                m_Edges = NetGeometry.ConnectedEdges(EntityManager, m_Node);
                m_Ends = LaneEnds.Collect(EntityManager, m_Node, m_Edges);
                if (m_Picked >= m_Ends.Count)
                    m_Picked = -1;
                m_Hovered = Hovered();

                if (cancelAction.WasPressedThisFrame())
                {
                    if (m_Picked >= 0)
                        m_Picked = -1;
                    else
                        m_ToolSystem.activeTool = m_DefaultToolSystem;
                    return inputDeps;
                }
                if (applyAction.WasPressedThisFrame() && m_Hovered >= 0)
                {
                    if (m_Ends[m_Hovered].Incoming)
                        m_Picked = m_Hovered;
                    else if (m_Picked >= 0)
                        Toggle(m_Ends[m_Picked], m_Ends[m_Hovered]);
                }
                Draw();
            }
            catch (System.Exception e)
            {
                Mod.Log.Critical(e, "The lane tool failed and was closed.");
                m_ToolSystem.activeTool = m_DefaultToolSystem;
            }
            return inputDeps;
        }

        /// <summary>
        /// The lane mark under the pointer: before a lane is picked, only
        /// lanes leading in; after, both, so another can be picked instead.
        /// </summary>
        private int Hovered()
        {
            if (!GetRaycastResult(out Entity _, out RaycastHit hit))
                return -1;
            int best = -1;
            float bestDistance = kPickRadius;
            for (int i = 0; i < m_Ends.Count; i++)
            {
                if (m_Picked < 0 && !m_Ends[i].Incoming)
                    continue;
                float d = math.distance(m_Ends[i].Position.xz, hit.m_HitPosition.xz);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Connects two lanes, or takes their connection away. A connection
        /// the game makes gets a rule that removes it; one the player added
        /// or removed loses its rule, which restores the game's lanes.
        /// </summary>
        private void Toggle(LaneEnd from, LaneEnd to)
        {
            DynamicBuffer<LaneConnectionRule> rules = EntityManager.HasBuffer<LaneConnectionRule>(m_Node)
                ? EntityManager.GetBuffer<LaneConnectionRule>(m_Node)
                : EntityManager.AddBuffer<LaneConnectionRule>(m_Node);
            for (int i = 0; i < rules.Length; i++)
            {
                LaneConnectionRule r = rules[i];
                if (r.FromEdge == from.Edge && r.FromLane == from.Index && r.ToEdge == to.Edge && r.ToLane == to.Index)
                {
                    rules.RemoveAt(i);
                    EntityManager.AddComponent<RebuildRequest>(m_Node);
                    return;
                }
            }
            rules.Add(new LaneConnectionRule
            {
                FromEdge = from.Edge,
                FromLane = from.Index,
                ToEdge = to.Edge,
                ToLane = to.Index,
                Change = Connected(from, to) ? LaneConnectionChange.Removed : LaneConnectionChange.Added,
            });
            EntityManager.AddComponent<RebuildRequest>(m_Node);
        }

        private bool Connected(LaneEnd from, LaneEnd to)
        {
            DynamicBuffer<SubLane> lanes = EntityManager.GetBuffer<SubLane>(m_Node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!EntityManager.HasComponent<CarLane>(lane) || EntityManager.HasComponent<MasterLane>(lane))
                    continue;
                Lane path = EntityManager.GetComponentData<Lane>(lane);
                if (path.m_StartNode.Equals(from.Node) && path.m_EndNode.Equals(to.Node))
                    return true;
            }
            return false;
        }

        private void Draw()
        {
            OverlayRenderSystem.Buffer buffer = m_Overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();
            const OverlayRenderSystem.StyleFlags projected = OverlayRenderSystem.StyleFlags.Projected;

            // The junction's lanes as they run now.
            DynamicBuffer<SubLane> lanes = EntityManager.GetBuffer<SubLane>(m_Node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!EntityManager.HasComponent<CarLane>(lane) || EntityManager.HasComponent<MasterLane>(lane) || !EntityManager.HasComponent<Curve>(lane))
                    continue;
                Lane path = EntityManager.GetComponentData<Lane>(lane);
                Bezier4x3 curve = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
                bool picked = m_Picked >= 0 && path.m_StartNode.Equals(m_Ends[m_Picked].Node);
                bool added = path.m_MiddleNode.GetLaneIndex() >= 0xE000;
                bool forbidden = (EntityManager.GetComponentData<CarLane>(lane).m_Flags & CarLaneFlags.Forbidden) != 0;
                Color color = added ? kAdded : picked ? kPicked : kLane;
                float width = picked || added ? 0.45f : 0.25f;
                if (forbidden)
                    buffer.DrawDashedCurve(kForbidden, kForbidden, 0f, projected, curve, width, 1.2f, 0.8f);
                else
                    buffer.DrawCurve(color, color, 0f, projected, curve, width, new float2(1f, 1f));
            }

            // Connections taken away: the lanes are gone, so the line is
            // drawn from the rule, between the two lane ends.
            if (EntityManager.HasBuffer<LaneConnectionRule>(m_Node))
            {
                DynamicBuffer<LaneConnectionRule> rules = EntityManager.GetBuffer<LaneConnectionRule>(m_Node, true);
                for (int i = 0; i < rules.Length; i++)
                {
                    if (rules[i].Change != LaneConnectionChange.Removed)
                        continue;
                    int from = LaneEnds.Find(m_Ends, rules[i].FromEdge, rules[i].FromLane, incoming: true);
                    int to = LaneEnds.Find(m_Ends, rules[i].ToEdge, rules[i].ToLane, incoming: false);
                    if (from < 0 || to < 0)
                        continue;
                    Bezier4x3 curve = NetUtils.FitCurve(m_Ends[from].Position, m_Ends[from].Direction, m_Ends[to].Direction, m_Ends[to].Position);
                    buffer.DrawDashedCurve(kRemoved, kRemoved, 0f, projected, curve, 0.3f, 0.8f, 0.8f);
                }
            }

            for (int i = 0; i < m_Ends.Count; i++)
            {
                LaneEnd end = m_Ends[i];
                Color ring = end.Incoming ? kIn : kOut;
                bool strong = i == m_Picked || i == m_Hovered;
                Color fill = strong ? new Color(ring.r, ring.g, ring.b, 0.8f) : new Color(ring.r, ring.g, ring.b, 0.15f);
                buffer.DrawCircle(ring, fill, 0.25f, projected, new float2(0f, 1f), end.Position, strong ? 2.2f : 1.6f);
            }
        }
    }
}
