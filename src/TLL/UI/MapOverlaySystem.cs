using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using TLL.Components;
using TLL.Systems;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace TLL.UI
{
    /// <summary>
    /// Draws TLL's marks on the map:
    ///
    /// - the junction shown in the panel, with its signalled lanes in the
    ///   colour they show right now, while the panel is open;
    /// - problem junctions, from TLL's long-term measurement (setting);
    /// - congested roads, from the game's own traffic flow data (setting).
    ///
    /// The last two change over hours, so they are collected every couple of
    /// seconds and drawn from that list in every frame.
    /// </summary>
    public partial class MapOverlaySystem : TllSystemBase
    {
        private static readonly Color kRing = new Color(0.15f, 0.76f, 1f, 1f);
        private static readonly Color kFill = new Color(0.15f, 0.76f, 1f, 0.08f);
        private static readonly Color kGo = new Color(0.24f, 0.75f, 0.42f, 0.95f);
        private static readonly Color kYield = new Color(0.96f, 0.72f, 0.24f, 0.95f);
        /// <summary>Width of a lane line in metres: a thin line on a 3 m lane.</summary>
        private const float kLaneWidth = 0.35f;
        private static readonly Color kProblem = new Color(0.9f, 0.2f, 0.2f, 1f);

        /// <summary>
        /// Rush-hour queue, in vehicles on the worst movement, from which a
        /// junction counts as a problem. Shared with the panel's list.
        /// </summary>
        public const float ProblemQueue = 5f;

        /// <summary>Queue at which the mark reaches full strength.</summary>
        private const float kSevereQueue = 15f;

        /// <summary>
        /// Flow level below which a road counts as congested, on the scale of
        /// the game's traffic flow view, where 1 is free flow.
        /// </summary>
        private const float kCongested = 0.6f;

        private static readonly TimeSpan kRefresh = TimeSpan.FromSeconds(2);

        private struct Mark
        {
            public Bezier4x3 Curve;
            public float3 Position;
            public float Width;
            public float Strength;
        }

        private OverlayRenderSystem m_Overlay;
        private TllUISystem m_UI;
        private EntityQuery m_ProblemQuery;
        private EntityQuery m_RoadQuery;
        private readonly List<Mark> m_Problems = new List<Mark>();
        private readonly List<Mark> m_Congestion = new List<Mark>();
        private DateTime m_Collected;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_UI = World.GetOrCreateSystemManaged<TllUISystem>();
            m_ProblemQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ManagedJunction>(), ComponentType.ReadOnly<JunctionHealth>(), ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_RoadQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Road>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Edge>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        protected override void OnSafeUpdate()
        {
            Setting settings = Mod.Settings;
            bool problems = settings != null && settings.ShowProblems;
            bool congestion = settings != null && settings.ShowCongestion;
            Entity selected = m_UI.PanelOpen ? m_UI.Selected : Entity.Null;
            if (selected != Entity.Null && (!EntityManager.Exists(selected) || !EntityManager.HasComponent<Node>(selected)))
                selected = Entity.Null;
            if (!problems && !congestion && selected == Entity.Null)
                return;

            if (DateTime.UtcNow - m_Collected >= kRefresh)
            {
                m_Collected = DateTime.UtcNow;
                m_Problems.Clear();
                m_Congestion.Clear();
                if (problems)
                    CollectProblems();
                if (congestion)
                    CollectCongestion();
            }

            OverlayRenderSystem.Buffer buffer = m_Overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();
            if (congestion)
            {
                foreach (Mark m in m_Congestion)
                {
                    Color color = kProblem;
                    color.a = 0.25f + 0.5f * m.Strength;
                    buffer.DrawCurve(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, m.Curve, m.Width, new float2(1f, 1f));
                }
            }
            if (problems)
            {
                foreach (Mark m in m_Problems)
                {
                    Color fill = kProblem;
                    fill.a = 0.1f + 0.25f * m.Strength;
                    buffer.DrawCircle(kProblem, fill, 2f + 2f * m.Strength, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), m.Position, 40f);
                }
            }
            if (selected != Entity.Null)
                DrawSelected(buffer, selected);
        }

        private void DrawSelected(OverlayRenderSystem.Buffer buffer, Entity node)
        {
            float3 position = EntityManager.GetComponentData<Node>(node).m_Position;
            buffer.DrawCircle(kRing, kFill, 0.8f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), position, 40f);
            if (!EntityManager.HasBuffer<JunctionLane>(node))
                return;
            DynamicBuffer<JunctionLane> lanes = EntityManager.GetBuffer<JunctionLane>(node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].Lane;
                if (!EntityManager.HasComponent<LaneSignal>(lane) || !EntityManager.HasComponent<Curve>(lane))
                    continue;
                // Only the lanes that may go: with every red lane drawn too,
                // a large junction turns into a tangle.
                LaneSignalType signal = EntityManager.GetComponentData<LaneSignal>(lane).m_Signal;
                if (signal != LaneSignalType.Go && signal != LaneSignalType.Yield)
                    continue;
                Color color = signal == LaneSignalType.Go ? kGo : kYield;
                Bezier4x3 curve = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
                if ((lanes[i].Flags & JunctionLaneFlags.Pedestrian) != 0)
                    buffer.DrawDashedCurve(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, curve, kLaneWidth, 1.2f, 0.8f);
                else
                    buffer.DrawCurve(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, curve, kLaneWidth, new float2(1f, 1f));
            }
        }

        private void CollectProblems()
        {
            using (NativeArray<JunctionHealth> health = m_ProblemQuery.ToComponentDataArray<JunctionHealth>(Allocator.Temp))
            using (NativeArray<Node> nodes = m_ProblemQuery.ToComponentDataArray<Node>(Allocator.Temp))
            {
                for (int i = 0; i < health.Length; i++)
                {
                    if (health[i].WorstQueue < ProblemQueue)
                        continue;
                    float strength = math.saturate((health[i].WorstQueue - ProblemQueue) / (kSevereQueue - ProblemQueue));
                    m_Problems.Add(new Mark { Position = nodes[i].m_Position, Strength = strength });
                }
            }
        }

        private void CollectCongestion()
        {
            using (NativeArray<Entity> edges = m_RoadQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Road> roads = m_RoadQuery.ToComponentDataArray<Road>(Allocator.Temp))
            using (NativeArray<Curve> curves = m_RoadQuery.ToComponentDataArray<Curve>(Allocator.Temp))
            {
                for (int i = 0; i < roads.Length; i++)
                {
                    // The same measure the game's traffic flow view colours
                    // roads by: the mean over the day plus the worst part of
                    // it, 1 for free flow.
                    float4 speed = NetUtils.GetTrafficFlowSpeed(roads[i]);
                    float level = math.csum(speed) * 0.125f + math.cmin(speed) * 0.5f;
                    if (math.isnan(level) || level >= kCongested)
                        continue;
                    m_Congestion.Add(new Mark
                    {
                        Curve = curves[i].m_Bezier,
                        Width = RoadWidth(edges[i]),
                        Strength = math.saturate((kCongested - level) / kCongested),
                    });
                }
            }
        }

        private float RoadWidth(Entity edge)
        {
            if (EntityManager.HasComponent<Composition>(edge))
            {
                Entity composition = EntityManager.GetComponentData<Composition>(edge).m_Edge;
                if (EntityManager.HasComponent<NetCompositionData>(composition))
                    return math.max(4f, EntityManager.GetComponentData<NetCompositionData>(composition).m_Width * 0.8f);
            }
            return 8f;
        }
    }
}
