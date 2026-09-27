using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using TLL.Components;
using TLL.Core.Control;
using TLL.Core.Coordination;
using TLL.Core.Optimization;
using TLL.Core.Planning;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Systems
{
    /// <summary>
    /// Builds green waves over the automatic junctions: finds corridors,
    /// puts each on one cycle with offsets for both directions, and switches
    /// its junctions to coordinated control. Junctions that no longer belong
    /// to a corridor go back to the automation's normal mode.
    ///
    /// Runs every 16384 simulation frames, about every four simulated
    /// minutes, and when the player asks for it. Manual junctions are never
    /// touched.
    /// </summary>
    public partial class CoordinationSystem : TllSystemBase
    {
        /// <summary>Junctions further apart than this along the road are not coordinated; platoons disperse on the way.</summary>
        private const float kMaxSpacing = 800f;

        /// <summary>Most roads followed through nodes without signals before giving up.</summary>
        private const int kMaxHops = 24;

        /// <summary>Through a junction without signals the road counts as going on if it bends less than 30 degrees.</summary>
        private const float kStraightCos = 0.866f;

        private const float kDefaultSpeed = 13.9f;

        private EntityQuery m_Query;
        private SignalControlSystem m_Control;

        public int Corridors { get; private set; }

        public int CoordinatedJunctions { get; private set; }

        /// <summary>Simulation frames between two automatic rounds.</summary>
        private const uint kRoundFrames = 16384;

        private SimulationSystem m_Simulation;
        private uint m_LastRound;
        private bool m_HasRun;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            // Checks often so a request from the panel is served quickly;
            // the actual round runs only every kRoundFrames.
            return 256;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Control = World.GetOrCreateSystemManaged<SignalControlSystem>();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<ManagedJunction>(),
                    ComponentType.ReadOnly<JunctionRuntime>(),
                    ComponentType.ReadWrite<JunctionPhase>(),
                    ComponentType.ReadOnly<JunctionMovement>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<JunctionDirty>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        protected override void OnSafeUpdate()
        {
            Setting settings = Mod.Settings;
            if (settings == null || !m_Control.Available)
                return;
            uint frame = m_Simulation.frameIndex;
            bool due = !m_HasRun || frame - m_LastRound >= kRoundFrames;
            if (!due && !Requests.RebuildGreenWaves)
                return;
            Requests.RebuildGreenWaves = false;
            m_LastRound = frame;
            m_HasRun = true;

            var nodes = new List<Entity>();
            using (NativeArray<Entity> all = m_Query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in all)
                {
                    ManagedJunction j = EntityManager.GetComponentData<ManagedJunction>(node);
                    if (j.Origin == JunctionOrigin.Auto && j.Mode != ControlMode.Flashing)
                        nodes.Add(node);
                }
            }

            var inCorridor = new HashSet<Entity>();
            int corridors = 0;
            if (settings.AutoGreenWaves && nodes.Count >= 2)
            {
                SignalNetwork network = BuildNetwork(nodes, out List<List<Entity>> edgesOf);
                int group = 1;
                foreach (CorridorPath path in CorridorFinder.Find(network, kMaxSpacing))
                {
                    if (Apply(path, nodes, edgesOf, group))
                    {
                        foreach (int i in path.Junctions)
                            inCorridor.Add(nodes[i]);
                        group++;
                        corridors++;
                    }
                }
            }

            foreach (Entity node in nodes)
            {
                if (!inCorridor.Contains(node))
                    Dissolve(node, settings);
            }
            Corridors = corridors;
            CoordinatedJunctions = inCorridor.Count;
            if (settings.VerboseLogging)
                Mod.Log.Info($"Green waves: {corridors} corridor(s) over {inCorridor.Count} junction(s).");
        }

        private SignalNetwork BuildNetwork(List<Entity> nodes, out List<List<Entity>> edgesOf)
        {
            var network = new SignalNetwork();
            var index = new Dictionary<Entity, int>();
            edgesOf = new List<List<Entity>>();
            for (int i = 0; i < nodes.Count; i++)
            {
                index[nodes[i]] = i;
                List<Entity> edges = NetGeometry.ConnectedEdges(EntityManager, nodes[i]);
                edgesOf.Add(edges);
                network.OppositeOf.Add(ChordModel.FindOpposites(NetGeometry.ApproachAngles(EntityManager, nodes[i], edges)));
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                List<Entity> edges = edgesOf[i];
                for (int a = 0; a < edges.Count; a++)
                {
                    if (!Walk(nodes[i], edges[a], index, out int k, out Entity arrival, out float length))
                        continue;
                    // Each road is found from both ends; keep it once.
                    if (k <= i)
                        continue;
                    int b = edgesOf[k].IndexOf(arrival);
                    if (b < 0)
                        continue;
                    network.Links.Add(new SignalLink
                    {
                        A = i,
                        ApproachA = a,
                        B = k,
                        ApproachB = b,
                        Length = length,
                        Speed = SpeedOf(edges[a]),
                        Weight = CarLaneCount(edges[a]),
                    });
                }
            }
            return network;
        }

        /// <summary>
        /// Follows the road from a signalled junction until it reaches the
        /// next one. Through a node joining two roads it simply continues;
        /// through a junction without signals it takes the road that goes on
        /// straight, if there is one.
        /// </summary>
        private bool Walk(Entity start, Entity firstEdge, Dictionary<Entity, int> signalled, out int target, out Entity arrival, out float length)
        {
            target = -1;
            arrival = Entity.Null;
            length = 0f;
            Entity node = start;
            Entity edge = firstEdge;
            for (int hop = 0; hop < kMaxHops; hop++)
            {
                if (!EntityManager.HasComponent<Curve>(edge))
                    return false;
                length += EntityManager.GetComponentData<Curve>(edge).m_Length;
                if (length > kMaxSpacing)
                    return false;
                Entity next = NetGeometry.OtherEnd(EntityManager, edge, node);
                if (signalled.TryGetValue(next, out target))
                {
                    arrival = edge;
                    return target >= 0;
                }
                if (!EntityManager.HasBuffer<ConnectedEdge>(next))
                    return false;

                // Heading of the traffic as it arrives at the next node.
                float2 heading = -NetGeometry.Outward(EntityManager, next, edge);
                Entity best = Entity.Null;
                float bestDot = kStraightCos;
                DynamicBuffer<ConnectedEdge> connected = EntityManager.GetBuffer<ConnectedEdge>(next, true);
                for (int i = 0; i < connected.Length; i++)
                {
                    Entity candidate = connected[i].m_Edge;
                    if (candidate == edge)
                        continue;
                    float dot = math.dot(heading, NetGeometry.Outward(EntityManager, next, candidate));
                    if (dot > bestDot || connected.Length == 2)
                    {
                        best = candidate;
                        bestDot = dot;
                    }
                }
                if (best == Entity.Null)
                    return false;
                node = next;
                edge = best;
            }
            return false;
        }

        private bool Apply(CorridorPath path, List<Entity> nodes, List<List<Entity>> edgesOf, int group)
        {
            var members = new List<CorridorMember>();
            for (int k = 0; k < path.Junctions.Count; k++)
            {
                int j = path.Junctions[k];
                Entity node = nodes[j];
                List<Entity> edges = edgesOf[j];
                int[] opposite = ChordModel.FindOpposites(NetGeometry.ApproachAngles(EntityManager, node, edges));
                int back = path.ApproachBack[k];
                int ahead = path.ApproachAhead[k];
                // At the ends of the corridor the missing side is the one
                // straight across from the side that exists.
                if (back < 0 && ahead >= 0)
                    back = opposite[ahead];
                if (ahead < 0 && back >= 0)
                    ahead = opposite[back];

                ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
                DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(node, true);
                DynamicBuffer<JunctionMovement> movements = EntityManager.GetBuffer<JunctionMovement>(node, true);
                var member = new CorridorMember
                {
                    Phases = new PhaseData[phases.Length],
                    PhaseMovements = new ulong[phases.Length],
                    Ratios = new float[phases.Length],
                    Intergreen = junction.Yellow + junction.AllRed + junction.Prepare,
                    DesiredCycle = EntityManager.GetComponentData<JunctionRuntime>(node).DesiredCycle,
                    MovementA = MovementBetween(movements, EdgeAt(edges, back), EdgeAt(edges, ahead)),
                    MovementB = MovementBetween(movements, EdgeAt(edges, ahead), EdgeAt(edges, back)),
                };
                for (int p = 0; p < phases.Length; p++)
                {
                    member.Phases[p] = phases[p].Data;
                    member.PhaseMovements[p] = phases[p].Movements;
                    member.Ratios[p] = phases[p].FlowRatio;
                }
                if (member.MovementA < 0 && member.MovementB < 0)
                    return false;
                members.Add(member);
            }

            CoordinationPlan plan = Coordinator.Plan(path, members, OptimizerLimits.Default);
            for (int k = 0; k < path.Junctions.Count; k++)
            {
                Entity node = nodes[path.Junctions[k]];
                ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
                junction.Mode = ControlMode.Coordinated;
                junction.Offset = plan.Offsets[k];
                junction.Group = group;
                EntityManager.SetComponentData(node, junction);

                DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(node);
                for (int p = 0; p < phases.Length; p++)
                {
                    ref JunctionPhase phase = ref phases.ElementAt(p);
                    phase.Data.Green = plan.Greens[k][p];
                    if (phase.Data.MaxGreen < phase.Data.Green)
                        phase.Data.MaxGreen = phase.Data.Green;
                    if (plan.Coordinated[k][p])
                        phase.Data.Flags |= PhaseFlags.Coordinated;
                    else
                        phase.Data.Flags &= ~PhaseFlags.Coordinated;
                }
            }
            if (Mod.Settings != null && Mod.Settings.VerboseLogging)
                Mod.Log.Info($"Green wave {group}: {path.Junctions.Count} junctions, cycle {Core.SimTime.ToSeconds(plan.Cycle):0} s, band {Core.SimTime.ToSeconds(plan.BandwidthA):0} s / {Core.SimTime.ToSeconds(plan.BandwidthB):0} s.");
            return true;
        }

        /// <summary>Takes a junction out of a green wave it no longer belongs to.</summary>
        private void Dissolve(Entity node, Setting settings)
        {
            ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
            if (junction.Mode != ControlMode.Coordinated && junction.Group == 0)
                return;
            junction.Mode = settings.AutoControl();
            junction.Group = 0;
            junction.Offset = 0;
            EntityManager.SetComponentData(node, junction);
            DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(node);
            for (int p = 0; p < phases.Length; p++)
                phases.ElementAt(p).Data.Flags &= ~PhaseFlags.Coordinated;
        }

        private static Entity EdgeAt(List<Entity> edges, int approach)
        {
            return approach >= 0 && approach < edges.Count ? edges[approach] : Entity.Null;
        }

        private static int MovementBetween(DynamicBuffer<JunctionMovement> movements, Entity source, Entity target)
        {
            if (source == Entity.Null || target == Entity.Null)
                return -1;
            for (int i = 0; i < movements.Length; i++)
            {
                JunctionMovement m = movements[i];
                if (m.Source == source && m.Target == target && m.Kind != MovementKind.Pedestrian)
                    return i;
            }
            return -1;
        }

        private float SpeedOf(Entity edge)
        {
            if (!EntityManager.HasBuffer<SubLane>(edge))
                return kDefaultSpeed;
            DynamicBuffer<SubLane> lanes = EntityManager.GetBuffer<SubLane>(edge, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                if (EntityManager.HasComponent<CarLane>(lanes[i].m_SubLane))
                {
                    float limit = EntityManager.GetComponentData<CarLane>(lanes[i].m_SubLane).m_SpeedLimit;
                    if (limit > 0f)
                        return limit;
                }
            }
            return kDefaultSpeed;
        }

        private float CarLaneCount(Entity edge)
        {
            if (!EntityManager.HasBuffer<SubLane>(edge))
                return 0f;
            int count = 0;
            DynamicBuffer<SubLane> lanes = EntityManager.GetBuffer<SubLane>(edge, true);
            for (int i = 0; i < lanes.Length; i++)
                count += EntityManager.HasComponent<CarLane>(lanes[i].m_SubLane) ? 1 : 0;
            return count;
        }
    }
}
