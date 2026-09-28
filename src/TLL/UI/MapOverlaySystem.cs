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
    /// - congested roads, from the game's own traffic flow data (setting);
    /// - junctions whose plan TLL changed after their roads changed
    ///   (PlanNotice), while the panel is open;
    /// - in the planner, the phase being edited on the junction's lanes.
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
        private static readonly Color kHover = new Color(1f, 0.85f, 0.3f, 1f);
        private static readonly Color kHoverFill = new Color(1f, 0.85f, 0.3f, 0.15f);
        private static readonly Color kNotice = new Color(0.79f, 0.66f, 1f, 1f);
        private static readonly Color kNoticeFill = new Color(0.79f, 0.66f, 1f, 0.12f);
        private static readonly Color kHoverLane = new Color(1f, 1f, 1f, 0.95f);

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
        private LaneToolSystem m_LaneTool;
        private Planner.PlannerSystem m_Planner;
        private EntityQuery m_ProblemQuery;
        private EntityQuery m_RoadQuery;
        private EntityQuery m_NoticeQuery;
        private readonly List<Mark> m_Problems = new List<Mark>();
        private readonly List<Mark> m_Congestion = new List<Mark>();
        private DateTime m_Collected;
        private bool m_CongestionReported;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_UI = World.GetOrCreateSystemManaged<TllUISystem>();
            m_LaneTool = World.GetOrCreateSystemManaged<LaneToolSystem>();
            m_Planner = World.GetOrCreateSystemManaged<Planner.PlannerSystem>();
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
            m_NoticeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PlanNotice>(), ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
        }

        protected override void OnSafeUpdate()
        {
            Setting settings = Mod.Settings;
            bool problems = settings != null && settings.ShowProblems;
            bool congestion = settings != null && settings.ShowCongestion;
            if (!congestion)
                m_CongestionReported = false;
            Entity selected = m_UI.PanelOpen ? m_UI.Selected : Entity.Null;
            if (selected != Entity.Null && (!EntityManager.Exists(selected) || !EntityManager.HasComponent<Node>(selected)))
                selected = Entity.Null;
            Entity hovered = m_UI.PanelOpen ? m_UI.Hovered : Entity.Null;
            if (hovered != Entity.Null && (!EntityManager.Exists(hovered) || !EntityManager.HasComponent<Node>(hovered)))
                hovered = Entity.Null;
            bool notices = m_UI.PanelOpen && !m_NoticeQuery.IsEmptyIgnoreFilter;
            Entity planned = m_Planner.Node;
            if (planned != Entity.Null && (!EntityManager.Exists(planned) || !EntityManager.HasComponent<Node>(planned)))
                planned = Entity.Null;
            if (!problems && !congestion && !notices && selected == Entity.Null && hovered == Entity.Null && planned == Entity.Null)
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
            if (notices)
            {
                // Plans TLL changed on its own after their roads changed,
                // until the player has looked at them.
                using (NativeArray<Node> nodes = m_NoticeQuery.ToComponentDataArray<Node>(Allocator.Temp))
                {
                    foreach (Node node in nodes)
                        buffer.DrawCircle(kNotice, kNoticeFill, 1.5f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), node.m_Position, 30f);
                }
            }
            // The lane tool draws the junction's lanes itself; the signal
            // colours on top of them would only confuse. In the planner, the
            // phase being edited replaces what the signals show now.
            if (planned != Entity.Null && !m_LaneTool.IsActive)
                DrawPlanned(buffer, planned);
            else if (selected != Entity.Null && !m_LaneTool.IsActive)
                DrawSelected(buffer, selected);
            if (hovered != Entity.Null && hovered != selected)
            {
                // A row of the panel's lists under the pointer: where is that?
                float3 position = EntityManager.GetComponentData<Node>(hovered).m_Position;
                buffer.DrawCircle(kHover, kHoverFill, 1.5f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), position, 60f);
            }
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

        /// <summary>
        /// The planner's selected phase on the road: the lanes of its
        /// movements in green, those giving way in amber, and the movement
        /// under the pointer in the planner's diagram in white on top, green
        /// or not, so the player sees which lanes an arrow stands for.
        /// </summary>
        private void DrawPlanned(OverlayRenderSystem.Buffer buffer, Entity node)
        {
            float3 position = EntityManager.GetComponentData<Node>(node).m_Position;
            buffer.DrawCircle(kRing, kFill, 0.8f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), position, 40f);
            if (!EntityManager.HasBuffer<JunctionLane>(node))
                return;
            ulong green = m_Planner.PreviewGreen;
            ulong permitted = m_Planner.PreviewPermitted;
            int hovered = m_Planner.HoveredMovement;
            DynamicBuffer<JunctionLane> lanes = EntityManager.GetBuffer<JunctionLane>(node, true);
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    int movement = lanes[i].Movement;
                    bool isHovered = movement == hovered;
                    bool inPhase = movement < 64 && (green & (1UL << movement)) != 0UL;
                    // The hovered movement comes last, over the others.
                    if ((pass == 0 && (!inPhase || isHovered)) || (pass == 1 && !isHovered))
                        continue;
                    Entity lane = lanes[i].Lane;
                    if (!EntityManager.HasComponent<Curve>(lane))
                        continue;
                    Color color = isHovered ? kHoverLane : (permitted & (1UL << movement)) != 0UL ? kYield : kGo;
                    float width = isHovered ? kLaneWidth * 1.8f : kLaneWidth;
                    Bezier4x3 curve = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
                    if ((lanes[i].Flags & JunctionLaneFlags.Pedestrian) != 0)
                        buffer.DrawDashedCurve(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, curve, width, 1.2f, 0.8f);
                    else
                        buffer.DrawCurve(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, curve, width, new float2(1f, 1f));
                }
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

        /// <summary>
        /// Share of the roads with traffic that can be marked at most: the
        /// ones losing the most travel time. Every road that ends at a signal
        /// is slowed by its red phases; only the worst of them are jams.
        /// </summary>
        private const float kWorstShare = 0.1f;

        /// <summary>
        /// Below this a time-of-day slot of a road carried no traffic worth
        /// the name. Its duration and distance have both decayed towards
        /// zero, and their ratio says nothing.
        /// </summary>
        private const float kMinFlow = 1e-3f;

        private void CollectCongestion()
        {
            var candidates = new List<(float lost, Mark mark)>();
            int withTraffic = 0;
            using (NativeArray<Entity> edges = m_RoadQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Road> roads = m_RoadQuery.ToComponentDataArray<Road>(Allocator.Temp))
            using (NativeArray<Curve> curves = m_RoadQuery.ToComponentDataArray<Curve>(Allocator.Temp))
            {
                for (int i = 0; i < roads.Length; i++)
                {
                    // Per time-of-day slot, the game sums over the road's lanes
                    // the time its vehicles spent, as the distance they would
                    // have covered at full speed, and the distance they did
                    // cover; both per metre of lane.
                    float4 duration = roads[i].m_TrafficFlowDuration0 + roads[i].m_TrafficFlowDuration1;
                    float4 distance = roads[i].m_TrafficFlowDistance0 + roads[i].m_TrafficFlowDistance1;
                    bool4 used = duration > kMinFlow;
                    int slots = math.csum(math.select(int4.zero, new int4(1), used));
                    if (slots == 0)
                        continue;
                    withTraffic++;
                    // The measure of the game's traffic flow view, half the
                    // mean and half the worst slot, over the slots with traffic.
                    float4 speed = math.select(1f, math.saturate(distance / duration), used);
                    float mean = math.csum(math.select(0f, speed, used)) / slots;
                    float level = 0.5f * mean + 0.5f * math.cmin(speed);
                    if (math.isnan(level) || level >= kCongested)
                        continue;
                    // What the difference costs: distance not covered, in the
                    // same units, which grows with the number of vehicles held.
                    float lost = math.csum(math.select(0f, math.max(0f, duration - distance), used));
                    candidates.Add((lost, new Mark
                    {
                        Curve = curves[i].m_Bezier,
                        Width = RoadWidth(edges[i]),
                        Strength = math.saturate((kCongested - level) / kCongested),
                    }));
                }
            }
            candidates.Sort((a, b) => b.lost.CompareTo(a.lost));
            int keep = Math.Min(candidates.Count, (int)Math.Ceiling(withTraffic * kWorstShare));
            for (int i = 0; i < keep; i++)
                m_Congestion.Add(candidates[i].mark);
            if (!m_CongestionReported)
            {
                // Once per switching on: the thresholds are derived from the
                // game's code, and these figures show how they fit a city.
                m_CongestionReported = true;
                Mod.Log.Info($"Congestion marks: {withTraffic} roads with traffic, {candidates.Count} slow, {keep} marked; lost travel of the worst {(keep > 0 ? candidates[0].lost : 0f):0.###}, of the last marked {(keep > 0 ? candidates[keep - 1].lost : 0f):0.###}.");
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
