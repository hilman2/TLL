using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using TLL.Components;
using TLL.Core.Advisor;
using TLL.Core.Control;
using TLL.Core.Coordination;
using TLL.Core.Optimization;
using TLL.Core.Planning;
using TLL.Metrics;
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
    /// Only links with enough measured traffic for their length are
    /// considered, and only waves with a usable band run (see
    /// <see cref="Coupling"/>); everything else stays adaptive.
    ///
    /// Runs every 16384 simulation frames, a sixteenth of a game day, and
    /// when the player asks for it. Manual junctions are never touched.
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

        /// <summary>
        /// A running wave with the same junctions keeps its plan while the new
        /// cycle stays within this share of its own. Every new plan shifts
        /// the offsets, and the controllers need a cycle or two to follow;
        /// replanning on every small change of the traffic kept the waves
        /// from ever settling.
        /// </summary>
        private const float kKeepCycle = 0.1f;

        /// <summary>Rounds of the autopilot (22.5 game minutes each) a junction stays out of waves after one did not help: a game day.</summary>
        private const ushort kWaveBanRounds = 64;

        private EntityQuery m_Query;
        private EntityQuery m_Rebuilding;
        private SignalControlSystem m_Control;

        public int Corridors { get; private set; }

        public int CoordinatedJunctions { get; private set; }

        /// <summary>Simulation frames between two automatic rounds.</summary>
        private const uint kRoundFrames = 16384;

        private SimulationSystem m_Simulation;
        private uint m_LastRound;
        private bool m_HasRun;

        /// <summary>Checks a round waits for junctions being rebuilt: 16 of 256 frames.</summary>
        private const int kMaxWaits = 16;
        private int m_Waited;

        // Per round, for the log line.
        private int m_LinksTooQuiet;
        private int m_BandTooNarrow;
        private int m_Hurting;

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
            m_Rebuilding = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ManagedJunction>() },
                Any = new[] { ComponentType.ReadOnly<JunctionDirty>(), ComponentType.ReadOnly<RebuildRequest>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
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
            // A junction being rebuilt is missing from the query below, and a
            // wave through it would come apart there. Its new plan changes
            // the wave's anyway: the round waits until it is done, though not
            // for ever, should one never finish.
            if (!m_Rebuilding.IsEmptyIgnoreFilter && m_Waited < kMaxWaits)
            {
                m_Waited++;
                return;
            }
            m_Waited = 0;
            Requests.RebuildGreenWaves = false;
            m_LastRound = frame;
            m_HasRun = true;

            var nodes = new List<Entity>();
            var banned = new List<Entity>();
            using (NativeArray<Entity> all = m_Query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in all)
                {
                    ManagedJunction j = EntityManager.GetComponentData<ManagedJunction>(node);
                    if (j.Origin != JunctionOrigin.Auto || j.Mode == ControlMode.Flashing)
                        continue;
                    if (EntityManager.HasComponent<AutopilotState>(node) && EntityManager.GetComponentData<AutopilotState>(node).WaveBan > 0)
                        banned.Add(node);
                    else
                        nodes.Add(node);
                }
            }
            foreach (Entity node in banned)
                Dissolve(node, settings);

            var inCorridor = new HashSet<Entity>();
            int corridors = 0;
            m_LinksTooQuiet = 0;
            m_BandTooNarrow = 0;
            m_Hurting = 0;
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
            Mod.Log.Info($"Green waves: {corridors} corridor(s) over {inCorridor.Count} junction(s); {m_LinksTooQuiet} link(s) with too little traffic, {m_BandTooNarrow} corridor(s) with too narrow a band, "
                + $"{m_Hurting} ended because they did not help, {banned.Count} junction(s) kept out for now.");
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

            // The walk through unsignalled junctions takes the straightest
            // road, which need not lead back the same way, so a link may be
            // found from one end only. Each is kept once, from wherever.
            var found = new HashSet<(int, int, int, int)>();
            for (int i = 0; i < nodes.Count; i++)
            {
                List<Entity> edges = edgesOf[i];
                for (int a = 0; a < edges.Count; a++)
                {
                    if (!Walk(nodes[i], edges[a], index, out int k, out Entity arrival, out float length) || k == i)
                        continue;
                    int b = edgesOf[k].IndexOf(arrival);
                    if (b < 0)
                        continue;
                    if (!found.Add(i < k ? (i, a, k, b) : (k, b, i, a)))
                        continue;
                    // Traffic on the link in both directions, as the two
                    // junctions measured it arriving from it, at its peak.
                    float volume = InflowFrom(nodes[i], edges[a]) + InflowFrom(nodes[k], arrival);
                    if (!Coupling.Couple(volume, length, InOneWave(nodes[i], nodes[k])))
                    {
                        m_LinksTooQuiet++;
                        continue;
                    }
                    network.Links.Add(new SignalLink
                    {
                        A = i,
                        ApproachA = a,
                        B = k,
                        ApproachB = b,
                        Length = length,
                        Speed = SpeedOf(edges[a]),
                        Weight = volume,
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
                // Any other signal on the way, one the player runs, one the
                // game runs, or one waiting for its plan, breaks the link:
                // platoons do not pass it on schedule.
                if (EntityManager.HasComponent<TrafficLights>(next) || !EntityManager.HasBuffer<ConnectedEdge>(next))
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
            // An end junction without traffic going on along the corridor,
            // such as the stem of a T or a bend, is left out; the rest of the
            // corridor still makes a wave.
            while (path.Junctions.Count >= 2 && !HasThrough(Member(path, 0, nodes, edgesOf)))
            {
                path.Junctions.RemoveAt(0);
                path.ApproachBack.RemoveAt(0);
                path.ApproachAhead.RemoveAt(0);
                path.Links.RemoveAt(0);
                path.ApproachBack[0] = -1;
            }
            while (path.Junctions.Count >= 2 && !HasThrough(Member(path, path.Junctions.Count - 1, nodes, edgesOf)))
            {
                int last = path.Junctions.Count - 1;
                path.Junctions.RemoveAt(last);
                path.ApproachBack.RemoveAt(last);
                path.ApproachAhead.RemoveAt(last);
                path.Links.RemoveAt(last - 1);
                path.ApproachAhead[last - 1] = -1;
            }
            if (path.Junctions.Count < 2)
                return false;

            var members = new List<CorridorMember>();
            bool hasA = false;
            bool hasB = false;
            bool running = true;
            int runningGroup = EntityManager.GetComponentData<ManagedJunction>(nodes[path.Junctions[0]]).Group;
            for (int k = 0; k < path.Junctions.Count; k++)
            {
                CorridorMember member = Member(path, k, nodes, edgesOf);
                if (!HasThrough(member))
                    return false;
                hasA |= member.MovementA >= 0;
                hasB |= member.MovementB >= 0;
                ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(nodes[path.Junctions[k]]);
                running &= junction.Mode == ControlMode.Coordinated && runningGroup != 0 && junction.Group == runningGroup;
                members.Add(member);
            }

            if (running && Hurts(path, nodes, runningGroup))
            {
                WriteWave("end", path, nodes, runningGroup, default, running);
                return false;
            }

            CoordinationPlan plan = Coordinator.Plan(path, members, OptimizerLimits.Default);
            if (!Coupling.BandWorthIt(plan.BandwidthA, plan.BandwidthB, plan.Cycle, hasA, hasB, running))
            {
                m_BandTooNarrow++;
                WriteWave("reject", path, nodes, group, plan, running);
                if (Mod.Settings != null && Mod.Settings.VerboseLogging)
                {
                    var text = new System.Text.StringBuilder($"Green wave not started: {path.Junctions.Count} junctions, cycle {Core.SimTime.ToSeconds(plan.Cycle):0} s,"
                        + $" band {Core.SimTime.ToSeconds(plan.BandwidthA):0} s / {Core.SimTime.ToSeconds(plan.BandwidthB):0} s, {(running ? "running" : "new")};");
                    for (int k = 0; k < path.Junctions.Count; k++)
                    {
                        CorridorMember m = members[k];
                        text.Append($" {nodes[path.Junctions[k]]}: {m.Phases.Length} phases, desired cycle {Core.SimTime.ToSeconds(m.DesiredCycle):0} s;");
                    }
                    Mod.Log.Info(text.ToString());
                }
                return false;
            }
            if (running && Unchanged(path, nodes, runningGroup, plan.Cycle))
            {
                // Same wave, same timing: only the number, which counts anew
                // in every round, follows.
                foreach (int j in path.Junctions)
                {
                    ManagedJunction kept = EntityManager.GetComponentData<ManagedJunction>(nodes[j]);
                    kept.Group = group;
                    EntityManager.SetComponentData(nodes[j], kept);
                }
                WriteWave("keep", path, nodes, group, plan, running);
                return true;
            }
            WriteWave(running ? "replan" : "start", path, nodes, group, plan, running);
            for (int k = 0; k < path.Junctions.Count; k++)
            {
                Entity node = nodes[path.Junctions[k]];
                ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
                // A junction new to this wave starts its measurement in it
                // afresh, so a wave that failed once gets a fair new trial.
                if (!running && EntityManager.HasComponent<AutopilotState>(node))
                {
                    AutopilotState state = EntityManager.GetComponentData<AutopilotState>(node);
                    for (int layout = 0; layout < LayoutMemory.Layouts; layout++)
                        state.Memory.Set(layout, true, default);
                    EntityManager.SetComponentData(node, state);
                }
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

        /// <summary>What the coordinator needs to know about the junction at position <paramref name="k"/> of the corridor.</summary>
        private CorridorMember Member(CorridorPath path, int k, List<Entity> nodes, List<List<Entity>> edgesOf)
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
            return member;
        }

        /// <summary>
        /// Whether a running wave has measurably made its junctions wait
        /// longer than they did alone (JunctionAdvisor.WaveHurts). If so, its
        /// junctions stay out of waves for a game day.
        /// </summary>
        private bool Hurts(CorridorPath path, List<Entity> nodes, int group)
        {
            int n = path.Junctions.Count;
            var alone = new Calibration[n];
            var inWave = new Calibration[n];
            var weights = new float[n];
            for (int k = 0; k < n; k++)
            {
                Entity node = nodes[path.Junctions[k]];
                if (!EntityManager.HasComponent<AutopilotState>(node))
                    continue;
                LayoutMemory memory = EntityManager.GetComponentData<AutopilotState>(node).Memory;
                int layout = System.Array.IndexOf(JunctionAdvisor.Strategies, EntityManager.GetComponentData<ManagedJunction>(node).Strategy);
                if (layout < 0)
                    continue;
                alone[k] = memory.Get(layout, false);
                inWave[k] = memory.Get(layout, true);
                weights[k] = Vehicles(node);
            }
            if (!JunctionAdvisor.WaveHurts(alone, inWave, weights))
                return false;
            for (int k = 0; k < n; k++)
            {
                Entity node = nodes[path.Junctions[k]];
                if (!EntityManager.HasComponent<AutopilotState>(node))
                    continue;
                AutopilotState state = EntityManager.GetComponentData<AutopilotState>(node);
                state.WaveBan = kWaveBanRounds;
                EntityManager.SetComponentData(node, state);
            }
            m_Hurting++;
            if (Mod.Settings != null && Mod.Settings.VerboseLogging)
            {
                var text = new System.Text.StringBuilder($"Green wave {group} ends: its junctions waited longer in it than alone.");
                for (int k = 0; k < n; k++)
                {
                    if (inWave[k].Measured)
                        text.Append($" {nodes[path.Junctions[k]]}: {inWave[k].Factor:0.00} in the wave, {alone[k].Factor:0.00} alone;");
                }
                Mod.Log.Info(text.ToString());
            }
            return true;
        }

        /// <summary>
        /// Whether a running wave still has exactly these junctions, all on
        /// the timing it gave them, and the new plan's cycle is close to it:
        /// then its plan stays. A member rebuilt since, with other phases,
        /// breaks the timing and so gets a new plan.
        /// </summary>
        private bool Unchanged(CorridorPath path, List<Entity> nodes, int group, int newCycle)
        {
            int members = 0;
            foreach (Entity node in nodes)
            {
                ManagedJunction j = EntityManager.GetComponentData<ManagedJunction>(node);
                if (j.Mode == ControlMode.Coordinated && j.Group == group)
                    members++;
            }
            if (members != path.Junctions.Count)
                return false;
            int cycle = -1;
            foreach (int j in path.Junctions)
            {
                int own = CycleOf(nodes[j]);
                if (cycle < 0)
                    cycle = own;
                else if (math.abs(own - cycle) > 1)
                    return false;
            }
            return cycle > 0 && math.abs(newCycle - cycle) <= cycle * kKeepCycle;
        }

        /// <summary>The cycle a junction's timed plan runs, in steps: its greens and the changes between them.</summary>
        private int CycleOf(Entity node)
        {
            ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
            int intergreen = junction.Yellow + junction.AllRed + junction.Prepare;
            DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(node, true);
            int cycle = 0;
            for (int p = 0; p < phases.Length; p++)
                cycle += phases[p].Data.Green + intergreen;
            return cycle;
        }

        /// <summary>Recent vehicles per hour through the junction, the weight of its verdict on a wave.</summary>
        private float Vehicles(Entity node)
        {
            if (!EntityManager.HasBuffer<MovementStatistics>(node))
                return 0f;
            DynamicBuffer<JunctionMovement> movements = EntityManager.GetBuffer<JunctionMovement>(node, true);
            DynamicBuffer<MovementStatistics> statistics = EntityManager.GetBuffer<MovementStatistics>(node, true);
            float sum = 0f;
            for (int m = 0; m < movements.Length && m < statistics.Length; m++)
            {
                if (movements[m].Kind != MovementKind.Pedestrian)
                    sum += statistics[m].Recent;
            }
            return sum;
        }

        private static bool HasThrough(CorridorMember member)
        {
            return member.MovementA >= 0 || member.MovementB >= 0;
        }

        /// <summary>Takes a junction out of a green wave it no longer belongs to.</summary>
        private void Dissolve(Entity node, Setting settings)
        {
            ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
            if (junction.Mode != ControlMode.Coordinated && junction.Group == 0)
                return;
            MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, node, "wave")?.Add("action", "leave").Add("group", junction.Group));
            junction.Mode = settings.AutoControl();
            junction.Group = 0;
            junction.Offset = 0;
            EntityManager.SetComponentData(node, junction);
            DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(node);
            for (int p = 0; p < phases.Length; p++)
                phases.ElementAt(p).Data.Flags &= ~PhaseFlags.Coordinated;
        }

        /// <summary>
        /// A record in the metrics log for what a round did with a corridor,
        /// at its first junction: start, keep, replan, reject or end, with its
        /// members, cycle and bands.
        /// </summary>
        private void WriteWave(string action, CorridorPath path, List<Entity> nodes, int group, CoordinationPlan plan, bool running)
        {
            MetricsRow row = MetricsRecords.Decision(m_Simulation.frameIndex, nodes[path.Junctions[0]], "wave");
            if (row == null)
                return;
            var members = new System.Text.StringBuilder();
            foreach (int j in path.Junctions)
                members.Append(members.Length > 0 ? "," : "").Append(nodes[j].Index);
            MetricsLog.Write(row.Add("action", action)
                .Add("group", group)
                .Add("running", running)
                .Add("members", members.ToString())
                .Add("junctions", path.Junctions.Count)
                .Add("cycle_s", Core.SimTime.ToSeconds(plan.Cycle))
                .Add("band_a_s", Core.SimTime.ToSeconds(plan.BandwidthA))
                .Add("band_b_s", Core.SimTime.ToSeconds(plan.BandwidthB)));
        }

        private static Entity EdgeAt(List<Entity> edges, int approach)
        {
            return approach >= 0 && approach < edges.Count ? edges[approach] : Entity.Null;
        }

        /// <summary>
        /// The movement carrying the corridor's traffic through the junction:
        /// the cars', where cars and trams run separately; the tram's only
        /// where there is nothing else.
        /// </summary>
        private static int MovementBetween(DynamicBuffer<JunctionMovement> movements, Entity source, Entity target)
        {
            if (source == Entity.Null || target == Entity.Null)
                return -1;
            int tram = -1;
            for (int i = 0; i < movements.Length; i++)
            {
                JunctionMovement m = movements[i];
                if (m.Source != source || m.Target != target || m.Kind == MovementKind.Pedestrian)
                    continue;
                if (m.Kind != MovementKind.Track)
                    return i;
                if (tram < 0)
                    tram = i;
            }
            return tram;
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

        /// <summary>Peak vehicles per hour arriving at the junction from the given road.</summary>
        private float InflowFrom(Entity node, Entity edge)
        {
            if (!EntityManager.HasBuffer<MovementStatistics>(node))
                return 0f;
            DynamicBuffer<JunctionMovement> movements = EntityManager.GetBuffer<JunctionMovement>(node, true);
            DynamicBuffer<MovementStatistics> statistics = EntityManager.GetBuffer<MovementStatistics>(node, true);
            float sum = 0f;
            for (int m = 0; m < movements.Length && m < statistics.Length; m++)
            {
                if (movements[m].Source == edge && movements[m].Kind != MovementKind.Pedestrian)
                    sum += statistics[m].Peak;
            }
            return sum;
        }

        private bool InOneWave(Entity a, Entity b)
        {
            ManagedJunction ja = EntityManager.GetComponentData<ManagedJunction>(a);
            ManagedJunction jb = EntityManager.GetComponentData<ManagedJunction>(b);
            return ja.Mode == ControlMode.Coordinated && jb.Mode == ControlMode.Coordinated && ja.Group != 0 && ja.Group == jb.Group;
        }
    }
}
