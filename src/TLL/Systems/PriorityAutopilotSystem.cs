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
        private EntityQuery m_Query;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return AutopilotSystem.kRoundFrames;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
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
                    if (Review(node, settings.AutoPrioritySigns, settings.VerboseLogging))
                        changed++;
                }
            }
            if (changed > 0)
                Mod.Log.Info($"Priority signs: {changed} of {reviewed} junctions without signals changed their signs.");
        }

        /// <returns>Whether the junction's signs changed.</returns>
        private bool Review(Entity node, bool enabled, bool verbose)
        {
            bool hasRules = EntityManager.HasBuffer<PriorityRule>(node);
            ulong current = 0UL;
            bool any = false;
            // The rules no longer match the roads: one was added, replaced or removed.
            bool stale = false;
            List<Entity> edges = NetGeometry.ConnectedEdges(EntityManager, node);
            if (hasRules)
            {
                DynamicBuffer<PriorityRule> rules = EntityManager.GetBuffer<PriorityRule>(node, true);
                for (int i = 0; i < rules.Length; i++)
                {
                    if (rules[i].ByPlayer)
                        return false;
                    any = true;
                    int approach = edges.IndexOf(rules[i].Edge);
                    stale |= approach < 0;
                    if (rules[i].Sign == PrioritySign.Priority && approach >= 0)
                        current |= 1UL << approach;
                }
                stale |= any && rules.Length != edges.Count;
            }
            ulong wanted = enabled && edges.Count >= 3 && edges.Count <= 64 ? Wanted(node, edges, current, any) : 0UL;
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
        /// The approaches that should form the priority road, from the
        /// traffic on the junction's lanes; 0 to leave the game's rule. Also
        /// 0 where the game's rule is not to be touched: roundabouts,
        /// motorway junctions and the game's all-way stop.
        /// </summary>
        /// <param name="ours">The junction has signs of the autopilot now, which explain any Stop or Yield on its lanes.</param>
        private ulong Wanted(Entity node, List<Entity> edges, ulong current, bool ours)
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
                if ((flags & (CarLaneFlags.Roundabout | CarLaneFlags.Highway)) != 0 || ((flags & CarLaneFlags.Stop) != 0 && !ours))
                    return 0UL;
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
            if (math.countbits(used) < 3)
                return 0UL;
            return PriorityAdvisor.Choose(flow, current);
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
