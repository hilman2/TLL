using Game.Net;
using Game.Rendering;
using TLL.Systems;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace TLL.UI
{
    /// <summary>
    /// Marks the junction shown in the TLL panel with a ring on the map,
    /// for as long as the panel is open.
    /// </summary>
    public partial class SelectionOverlaySystem : TllSystemBase
    {
        private static readonly Color kRing = new Color(0.15f, 0.76f, 1f, 1f);
        private static readonly Color kFill = new Color(0.15f, 0.76f, 1f, 0.12f);

        private OverlayRenderSystem m_Overlay;
        private TllUISystem m_UI;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_UI = World.GetOrCreateSystemManaged<TllUISystem>();
        }

        protected override void OnSafeUpdate()
        {
            Entity node = m_UI.Selected;
            if (!m_UI.PanelOpen || node == Entity.Null || !EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
                return;
            OverlayRenderSystem.Buffer buffer = m_Overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();
            float3 position = EntityManager.GetComponentData<Node>(node).m_Position;
            buffer.DrawCircle(kRing, kFill, 3f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), position, 36f);
        }
    }
}
