using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using TLL.Components;
using TLL.Core.Advisor;
using TLL.Metrics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Systems
{
    /// <summary>
    /// Puts signs on junctions without signals where one pair of approaches
    /// carries most of the traffic: a priority road for that pair, give way
    /// for the rest (PriorityAdvisor). It works from the game's own count of
    /// the traffic on each lane (LaneFlow), so it needs no measurement of its
    /// own and reaches every junction of the city. Each round looks at one
    /// sixteenth of the junctions, so each is reviewed every 16 rounds, six
    /// game hours. Junctions where the player chose a sign are left alone.
    /// </summary>
    public partial class PriorityAutopilotSystem : TllSystemBase
    {
        private const int kSlices = 16;

        /// <summary>
        /// Most junctions changed in one round. Each change rebuilds a junction
        /// and its roads in one frame; the first rounds in a large city would
        /// otherwise rebuild hundreds at once. The rest wait for their next
        /// turn.
        /// </summary>
        private const int kMaxChanges = 32;

        /// <summary>
        /// Updates of the game's lane traffic count per game hour: it counts
        /// every 512 of the day's 262144 frames (TrafficFlowSystem), and a
        /// lane's figure is the distance driven on it per update.
        /// </summary>
        private const float kFlowUpdatesPerHour = 262144f / 512f / 24f;

        private SimulationSystem m_Simulation;
        private Game.City.CityConfigurationSystem m_CityConfiguration;
        private EntityQuery m_Query;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return AutopilotSystem.kRoundFrames;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_CityConfiguration = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.ReadOnly<SubLane>() },
                None = new[] { ComponentType.ReadOnly<TrafficLights>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            RequireForUpdate(m_Query);
        }

        protected override void OnSafeUpdate()
        {
            Setting settings = Mod.Settings;
            if (settings == null)
                return;
            uint slice = m_Simulation.frameIndex / AutopilotSystem.kRoundFrames % kSlices;
            int reviewed = 0;
            int changed = 0;
            using (NativeArray<Entity> nodes = m_Query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    if ((uint)node.Index % kSlices != slice || changed >= kMaxChanges)
                        continue;
                    reviewed++;
                    if (Review(node, settings))
                        changed++;
                }
            }
            if (changed > 0)
                Mod.Log.Info($"Junctions without signals: {changed} of {reviewed} changed their signs or lane arrows.");
        }

        /// <returns>Whether the junction's signs or lane arrows changed.</returns>
        private bool Review(Entity node, Setting settings)
        {
            List<Entity> edges = NetGeometry.ConnectedEdges(EntityManager, node);
            if (edges.Count < 3 || edges.Count > 64)
                return false;
            bool ours = false;
            bool player = false;
            if (EntityManager.HasBuffer<PriorityRule>(node))
            {
                DynamicBuffer<PriorityRule> rules = EntityManager.GetBuffer<PriorityRule>(node, true);
                for (int i = 0; i < rules.Length; i++)
                {
                    player |= rules[i].ByPlayer;
                    ours |= !rules[i].ByPlayer;
                }
            }
            float[,] flow = Flow(node, edges, ours || player);
            bool changed = !player && ReviewSigns(node, edges, flow, settings.AutoPrioritySigns, settings.VerboseLogging);
            changed |= ReviewLanes(node, flow, settings.AutoLaneArrows);
            return changed;
        }

        /// <summary>
        /// The autopilot's lane rules for the junction (LaneArrowReview),
        /// judged by the load of its busiest lanes; with the setting off, its
        /// rules go.
        /// </summary>
        private bool ReviewLanes(Entity node, float[,] flow, bool enabled)
        {
            List<LaneConnectionRule> rules = null;
            string summary = null;
            if (enabled && flow != null)
            {
                rules = LaneArrowReview.Decide(EntityManager, node, flow, null, m_CityConfiguration.leftHandTraffic, out summary);
            }
            else if (!enabled && EntityManager.HasBuffer<LaneConnectionRule>(node))
            {
                DynamicBuffer<LaneConnectionRule> stored = EntityManager.GetBuffer<LaneConnectionRule>(node, true);
                var players = new List<LaneConnectionRule>();
                for (int i = 0; i < stored.Length; i++)
                {
                    if (!stored[i].Auto)
                        players.Add(stored[i]);
                }
                if (players.Count != stored.Length)
                    rules = players;
            }
            if (rules == null)
                return false;
            DynamicBuffer<LaneConnectionRule> buffer = EntityManager.HasBuffer<LaneConnectionRule>(node)
                ? EntityManager.GetBuffer<LaneConnectionRule>(node)
                : EntityManager.AddBuffer<LaneConnectionRule>(node);
            buffer.Clear();
            foreach (LaneConnectionRule rule in rules)
                buffer.Add(rule);
            EntityManager.AddComponent<RebuildRequest>(node);
            if (summary != null)
                Mod.Log.Info($"Lane arrows: junction {node} changes {summary}.");
            MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, node, "lanes")?.Add("summary", summary ?? "off").Add("rules", rules.Count));
            return true;
        }

        /// <returns>Whether the junction's signs changed.</returns>
        private bool ReviewSigns(Entity node, List<Entity> edges, float[,] flow, bool enabled, bool verbose)
        {
            bool hasRules = EntityManager.HasBuffer<PriorityRule>(node);
            ulong current = 0UL;
            bool any = false;
            // The rules no longer match the roads: one was added, replaced or removed.
            bool stale = false;
            if (hasRules)
            {
                DynamicBuffer<PriorityRule> rules = EntityManager.GetBuffer<PriorityRule>(node, true);
                for (int i = 0; i < rules.Length; i++)
                {
                    any = true;
                    int approach = edges.IndexOf(rules[i].Edge);
                    stale |= approach < 0;
                    if (rules[i].Sign == PrioritySign.Priority && approach >= 0)
                        current |= 1UL << approach;
                }
                stale |= any && rules.Length != edges.Count;
            }
            ulong wanted = enabled && flow != null ? PriorityAdvisor.Choose(flow, current) : 0UL;
            if (wanted == current && !stale && (wanted != 0UL || !any))
                return false;

            DynamicBuffer<PriorityRule> buffer = hasRules ? EntityManager.GetBuffer<PriorityRule>(node) : EntityManager.AddBuffer<PriorityRule>(node);
            buffer.Clear();
            for (int e = 0; wanted != 0UL && e < edges.Count; e++)
            {
                PrioritySign sign = (wanted & (1UL << e)) != 0 ? PrioritySign.Priority : PrioritySign.Yield;
                buffer.Add(new PriorityRule { Edge = edges[e], Sign = sign });
            }
            // The roads' lanes carry the stop lines and markings.
            EntityManager.AddComponent<RebuildRequest>(node);
            foreach (Entity edge in edges)
                EntityManager.AddComponent<RebuildRequest>(edge);
            if (verbose)
                Mod.Log.Info($"Priority signs: junction {node} {(wanted == 0UL ? "goes back to the game's rule" : $"gets a priority road between approaches {Describe(wanted)}")}.");
            MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, node, "signs")?.Add("priority", (long)wanted).Add("approaches", edges.Count));
            return true;
        }

        /// <summary>
        /// Vehicles per hour from approach [s] into approach [t], from the
        /// traffic on the junction's lanes; null where the autopilot keeps
        /// out: roundabouts, motorway junctions, the game's all-way stop, and
        /// roads that only meet a path.
        /// </summary>
        /// <param name="signs">The junction has signs of TLL now, which explain any Stop on its lanes.</param>
        private float[,] Flow(Entity node, List<Entity> edges, bool signs)
        {
            int n = edges.Count;
            var flow = new float[n, n];
            ulong used = 0UL;
            DynamicBuffer<SubLane> lanes = EntityManager.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!EntityManager.HasComponent<CarLane>(lane) || EntityManager.HasComponent<MasterLane>(lane))
                    continue;
                CarLaneFlags flags = EntityManager.GetComponentData<CarLane>(lane).m_Flags;
                if ((flags & (CarLaneFlags.Roundabout | CarLaneFlags.Highway)) != 0 || ((flags & CarLaneFlags.Stop) != 0 && !signs))
                    return null;
                Lane path = EntityManager.GetComponentData<Lane>(lane);
                int s = IndexOf(path.m_StartNode, edges);
                int t = IndexOf(path.m_EndNode, edges);
                if (s < 0 || t < 0)
                    continue;
                used |= (1UL << s) | (1UL << t);
                if (!EntityManager.HasComponent<LaneFlow>(lane) || !EntityManager.HasComponent<Curve>(lane))
                    continue;
                float length = math.max(1f, EntityManager.GetComponentData<Curve>(lane).m_Length);
                flow[s, t] += math.cmax(EntityManager.GetComponentData<LaneFlow>(lane).m_Distance) / length * kFlowUpdatesPerHour;
            }
            // A road that only meets a path or a service way is no junction
            // for cars.
            return math.countbits(used) < 3 ? null : flow;
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

        private static string Describe(ulong mask)
        {
            var parts = new List<string>();
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) != 0)
                    parts.Add(i.ToString());
            }
            return string.Join(" and ", parts);
        }
    }
}
