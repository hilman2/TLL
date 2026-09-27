using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using TLL.Components;
using TLL.Core.Control;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Edge = Game.Net.Edge;

namespace TLL.UI
{
    /// <summary>
    /// Tool for picking a junction in the world. While it is active, every
    /// junction TLL controls gets a ring in the colour of its mode, and the
    /// junction under the cursor gets a white one. A click selects it in the
    /// panel; cancel (right click, Escape) returns to the default tool.
    /// </summary>
    public partial class JunctionToolSystem : ToolBaseSystem
    {
        public const string kToolID = "TLL.JunctionTool";

        private OverlayRenderSystem m_Overlay;
        private TllUISystem m_UI;
        private EntityQuery m_ManagedQuery;
        private Entity m_Hovered;

        public override string toolID => kToolID;

        public bool IsActive => m_ToolSystem.activeTool == this;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_UI = World.GetOrCreateSystemManaged<TllUISystem>();
            m_ManagedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ManagedJunction>(), ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        public void Toggle()
        {
            m_ToolSystem.activeTool = IsActive ? (ToolBaseSystem)m_DefaultToolSystem : this;
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
            m_Hovered = Entity.Null;
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
            m_ToolRaycastSystem.typeMask = TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.TramTrack | Layer.PublicTransportRoad;
            m_ToolRaycastSystem.raycastFlags |= RaycastFlags.SubElements;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            try
            {
                m_Hovered = HoveredJunction();
                if (applyAction.WasPressedThisFrame() && m_Hovered != Entity.Null)
                    m_UI.Select(m_Hovered, moveCamera: false);
                if (cancelAction.WasPressedThisFrame())
                {
                    m_ToolSystem.activeTool = m_DefaultToolSystem;
                    return inputDeps;
                }
                Draw();
            }
            catch (System.Exception e)
            {
                Mod.Log.Critical(e, "The junction tool failed and was closed.");
                m_ToolSystem.activeTool = m_DefaultToolSystem;
            }
            return inputDeps;
        }

        /// <summary>
        /// The node under the cursor, if it is a junction. A hit on a road
        /// counts for the end of the road nearer to the cursor.
        /// </summary>
        private Entity HoveredJunction()
        {
            if (!GetRaycastResult(out Entity entity, out RaycastHit hit))
                return Entity.Null;
            if (EntityManager.HasComponent<Edge>(entity))
            {
                Edge edge = EntityManager.GetComponentData<Edge>(entity);
                entity = hit.m_CurvePosition < 0.5f ? edge.m_Start : edge.m_End;
            }
            if (!EntityManager.HasComponent<Node>(entity) || !EntityManager.HasBuffer<ConnectedEdge>(entity))
                return Entity.Null;
            // Two roads meeting end to end are not a junction.
            return EntityManager.GetBuffer<ConnectedEdge>(entity, true).Length >= 3 ? entity : Entity.Null;
        }

        private void Draw()
        {
            OverlayRenderSystem.Buffer buffer = m_Overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            using (NativeArray<Entity> nodes = m_ManagedQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    ControlMode mode = EntityManager.GetComponentData<ManagedJunction>(node).Mode;
                    float3 position = EntityManager.GetComponentData<Node>(node).m_Position;
                    buffer.DrawCircle(ColorOf(mode), new Color(0f, 0f, 0f, 0f), 1.5f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), position, 26f);
                }
            }
            if (m_Hovered != Entity.Null)
            {
                float3 position = EntityManager.GetComponentData<Node>(m_Hovered).m_Position;
                buffer.DrawCircle(Color.white, new Color(1f, 1f, 1f, 0.15f), 2.5f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), position, 32f);
            }
        }

        private static Color ColorOf(ControlMode mode)
        {
            switch (mode)
            {
                case ControlMode.Adaptive:
                    return new Color(0.24f, 0.75f, 0.42f, 0.9f);
                case ControlMode.Actuated:
                    return new Color(0.24f, 0.6f, 0.9f, 0.9f);
                case ControlMode.Coordinated:
                    return new Color(0.7f, 0.45f, 0.95f, 0.9f);
                case ControlMode.Flashing:
                    return new Color(0.96f, 0.72f, 0.24f, 0.9f);
                default:
                    return new Color(0.85f, 0.85f, 0.85f, 0.9f);
            }
        }
    }
}
