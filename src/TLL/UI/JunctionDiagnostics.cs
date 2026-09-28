using System.Text;
using Game.Creatures;
using Game.Net;
using Game.Objects;
using TLL.Components;
using TLL.Core;
using TLL.Core.Advisor;
using TLL.Core.Control;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.UI
{
    /// <summary>
    /// Writes everything that decides whether traffic moves at one junction
    /// into the log: the controller, every signalled lane with its signal,
    /// and for the vehicle at the front of each approach why the game holds
    /// it (the vehicle's Blocker: a signal, the vehicle ahead, crossing
    /// traffic, ...). For reports of vehicles standing at green.
    /// </summary>
    internal static class JunctionDiagnostics
    {
        public static string Describe(EntityManager em, Entity node, bool leftHandTraffic)
        {
            var text = new StringBuilder();
            text.Append($"Diagnostics for junction {node}\n");
            if (!em.HasComponent<ManagedJunction>(node))
            {
                text.Append("  not managed by TLL\n");
                DescribeRules(em, node, text);
                return text.ToString();
            }
            ManagedJunction junction = em.GetComponentData<ManagedJunction>(node);
            text.Append($"  origin {junction.Origin}, mode {junction.Mode}, layout {junction.Strategy}\n");
            if (em.HasComponent<JunctionRuntime>(node))
            {
                JunctionRuntime runtime = em.GetComponentData<JunctionRuntime>(node);
                var s = runtime.State;
                text.Append($"  stage {s.Stage}, phase {s.Phase + 1}, next {s.Next + 1}, {SimTime.ToSeconds(s.StageSteps):0} s in stage, walk {s.Walk}\n");
                // The last eight vehicle greens with turns across a crosswalk,
                // newest first: x where a turning vehicle stopped for people.
                string history = "";
                for (int b = 0; b < PedestrianConflicts.Window; b++)
                    history += (runtime.Conflicts.History & (1 << b)) != 0 ? 'x' : '.';
                text.Append($"  scramble on demand {(junction.Options & JunctionOptions.ScrambleOnDemand) != 0}, diverting {runtime.Conflicts.Divert} (kept {(junction.Options & JunctionOptions.PedestriansDiverted) != 0}),conflicts {runtime.Conflicts.Count}/{PedestrianConflicts.Window} [{history}], this green {runtime.ConflictThisGreen}\n");
            }
            text.Append($"  turn on red {(junction.Options & JunctionOptions.TurnOnRed) != 0}\n");
            if (em.HasComponent<AutopilotState>(node))
            {
                // What the autopilot decides flashing on: it starts below
                // both thresholds of FlashAdvisor and ends above either.
                AutopilotState a = em.GetComponentData<AutopilotState>(node);
                float load = FlashAdvisor.SideLoad(a.MajorVolume, a.MinorVolume);
                text.Append($"  autopilot: main road {a.MajorVolume:0}/h, busiest side road {a.MinorVolume:0}/h, side road load {load:0.00}"
                    + $" (flashing starts below {FlashAdvisor.StartBelow:0}/h in total and load {FlashAdvisor.StartSaturation:0.00}, ends above {FlashAdvisor.EndAbove:0}/h or {FlashAdvisor.EndSaturation:0.00}),"
                    + $" {a.Flash.RoundsSinceChange} rounds since the last change, last flashing ended in a backlog {a.Flash.EndedByBacklog}\n");
                // The layout memory: per layout, measured wait over the
                // model's, periods measured, and how often a queue stayed.
                text.Append($"  measured wait {a.MeasuredWait:0.0} s against the model's {a.ModelledWait:0.0} s, out of green waves for {a.WaveBan} rounds\n");
                for (int i = 0; i < LayoutMemory.Layouts; i++)
                {
                    Calibration alone = a.Memory.Get(i, false);
                    Calibration wave = a.Memory.Get(i, true);
                    text.Append($"  layout {JunctionAdvisor.Strategies[i]}: alone x{alone.Factor:0.00} over {alone.Samples} periods, backlog {alone.Backlog:0.00};"
                        + $" in a wave x{wave.Factor:0.00} over {wave.Samples} periods, backlog {wave.Backlog:0.00}; estimate {a.LayoutDelay[i]:0.0} s\n");
                }
            }
            if (em.HasComponent<JunctionRuntime>(node))
            {
                JunctionRuntime p = em.GetComponentData<JunctionRuntime>(node);
                text.Append($"  measurement period: {p.PeriodRounds} rounds, {p.PeriodVehicles:0} vehicles, {p.PeriodWait:0} s waiting"
                    + $" ({(p.PeriodVehicles > 0f ? p.PeriodWait / p.PeriodVehicles : 0f):0.0} s each), backlog {p.PeriodBacklog}, wave {p.PeriodWave}, mixed {p.PeriodMixed}\n");
            }
            if (em.HasBuffer<MovementStatistics>(node))
            {
                DynamicBuffer<MovementStatistics> stats = em.GetBuffer<MovementStatistics>(node, true);
                DynamicBuffer<JunctionMovement> moves = em.GetBuffer<JunctionMovement>(node, true);
                var roads = Systems.NetGeometry.ConnectedEdges(em, node);
                for (int m = 0; m < stats.Length && m < moves.Length; m++)
                {
                    MovementStatistics st = stats[m];
                    text.Append($"  movement {m} [{roads.IndexOf(moves[m].Source)}->{roads.IndexOf(moves[m].Target)} {moves[m].Kind}]: recent {st.Recent:0}/h, peak {st.Peak:0}/h, queue {st.RecentQueue:0.0} (last round with the exit free {st.LastQueue:0.0}, peak {st.PeakQueue:0.0})\n");
                }
            }

            DescribeModel(em, node, leftHandTraffic, text);

            DynamicBuffer<JunctionMovement> movements = em.GetBuffer<JunctionMovement>(node, true);
            var edges = Systems.NetGeometry.ConnectedEdges(em, node);
            DynamicBuffer<JunctionPhase> phases = em.GetBuffer<JunctionPhase>(node, true);
            for (int p = 0; p < phases.Length; p++)
            {
                var d = phases[p].Data;
                text.Append($"  phase {p + 1}: demand {d.Demand:0.#}, queue {d.Queue:0.#}, pressure {d.Pressure:0.#}, approaching {d.Approaching:0.#}, call {d.PedestrianCall}, waiting {SimTime.ToSeconds(d.WaitSteps):0} s, flags {d.Flags}"
                    + $"; green min {SimTime.ToSeconds(d.MinGreen):0} s, max {SimTime.ToSeconds(d.MaxGreen):0} s, planned {SimTime.ToSeconds(d.Green):0} s, walk {SimTime.ToSeconds(d.WalkGreen):0} s"
                    + $"; last green {SimTime.ToSeconds(d.LastGreen):0} s, ended: {d.LastEnd}"
                    + $"; since the optimiser's last round {d.Stats.Greens} greens of {(d.Stats.Greens > 0 ? SimTime.ToSeconds((int)(d.Stats.GreenSteps / d.Stats.Greens)) : 0f):0} s on average, {d.Stats.MaxOuts} at the maximum, {d.Stats.GapOuts} ran empty\n");
            }

            DynamicBuffer<JunctionLane> lanes = em.GetBuffer<JunctionLane>(node, true);
            for (int l = 0; l < lanes.Length; l++)
            {
                JunctionLane lane = lanes[l];
                string movement = lane.Movement < movements.Length
                    ? $"{edges.IndexOf(movements[lane.Movement].Source)}->{edges.IndexOf(movements[lane.Movement].Target)} {movements[lane.Movement].Kind}"
                    : "?";
                text.Append($"  lane {lane.Lane} [{movement}] flags {lane.Flags}");
                if (em.HasComponent<LaneSignal>(lane.Lane))
                {
                    LaneSignal signal = em.GetComponentData<LaneSignal>(lane.Lane);
                    text.Append($", signal {signal.m_Signal}, groups 0x{signal.m_GroupMask:x4}");
                }
                if (em.HasBuffer<LaneObject>(lane.Lane))
                {
                    DynamicBuffer<LaneObject> inside = em.GetBuffer<LaneObject>(lane.Lane, true);
                    text.Append($", {inside.Length} inside");
                    for (int i = 0; i < inside.Length && i < 3; i++)
                        text.Append($"; {inside[i].m_LaneObject} at {Speed(em, inside[i].m_LaneObject):0.0} m/s{HeldBy(em, inside[i].m_LaneObject)}");
                }
                else
                {
                    text.Append(em.HasComponent<MasterLane>(lane.Lane) ? ", master lane" : ", no vehicle list");
                }
                text.Append('\n');
                if (lane.Approach != Entity.Null && !SeenBefore(lanes, l))
                    text.Append($"    approach {lane.Approach}: {Front(em, lane.Approach)}\n");
                if (lane.Exit != Entity.Null)
                    text.Append($"    exit {lane.Exit}: {Fill(em, lane.Exit)}\n");
            }
            return text.ToString();
        }

        /// <summary>
        /// For a junction without TLL's signals: TLL's rules, and per car
        /// lane the flags that decide who may go and who gives way.
        /// </summary>
        private static void DescribeRules(EntityManager em, Entity node, StringBuilder text)
        {
            if (!em.HasBuffer<ConnectedEdge>(node) || !em.HasBuffer<SubLane>(node))
                return;
            var edges = Systems.NetGeometry.ConnectedEdges(em, node);
            DescribeRuleBuffers(em, node, edges, text);
            const CarLaneFlags shown = CarLaneFlags.Yield | CarLaneFlags.Stop | CarLaneFlags.RightOfWay | CarLaneFlags.Forbidden
                | CarLaneFlags.Unsafe | CarLaneFlags.UTurnLeft | CarLaneFlags.UTurnRight | CarLaneFlags.TurnLeft | CarLaneFlags.TurnRight;
            DynamicBuffer<SubLane> lanes = em.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!em.HasComponent<CarLane>(lane))
                    continue;
                Lane path = em.GetComponentData<Lane>(lane);
                int from = -1;
                int to = -1;
                for (int e = 0; e < edges.Count; e++)
                {
                    if (path.m_StartNode.OwnerEquals(new Game.Pathfind.PathNode(edges[e], 0)))
                        from = e;
                    if (path.m_EndNode.OwnerEquals(new Game.Pathfind.PathNode(edges[e], 0)))
                        to = e;
                }
                string flow = em.HasComponent<LaneFlow>(lane) ? $", flow {math.cmax(em.GetComponentData<LaneFlow>(lane).m_Distance):0.#}" : "";
                text.Append($"  lane {lane} {from}->{to}{(em.HasComponent<MasterLane>(lane) ? " master" : "")}: {em.GetComponentData<CarLane>(lane).m_Flags & shown}{flow}\n");
            }
        }

        /// <summary>TLL's rules for the junction's lanes, with roads as indices into <paramref name="edges"/>.</summary>
        private static void DescribeRuleBuffers(EntityManager em, Entity node, System.Collections.Generic.List<Entity> edges, StringBuilder text)
        {
            if (em.HasBuffer<TurnRule>(node))
            {
                DynamicBuffer<TurnRule> rules = em.GetBuffer<TurnRule>(node, true);
                for (int i = 0; i < rules.Length; i++)
                    text.Append($"  turn rule {edges.IndexOf(rules[i].From)}->{edges.IndexOf(rules[i].To)}: {rules[i].Flags}, {rules[i].Volume:0}/h before\n");
            }
            if (em.HasBuffer<PriorityRule>(node))
            {
                DynamicBuffer<PriorityRule> rules = em.GetBuffer<PriorityRule>(node, true);
                for (int i = 0; i < rules.Length; i++)
                    text.Append($"  sign rule approach {edges.IndexOf(rules[i].Edge)}: {rules[i].Sign}, {rules[i].Flags}\n");
            }
            if (em.HasBuffer<LaneConnectionRule>(node))
            {
                DynamicBuffer<LaneConnectionRule> rules = em.GetBuffer<LaneConnectionRule>(node, true);
                for (int i = 0; i < rules.Length; i++)
                    text.Append($"  lane rule {edges.IndexOf(rules[i].FromEdge)}.{rules[i].FromLane} -> {edges.IndexOf(rules[i].ToEdge)}.{rules[i].ToLane}: {rules[i].Change}\n");
            }
        }

        /// <summary>
        /// The junction as the phase planner sees it: per movement, which
        /// movements share an approach lane with it and which it may never
        /// have green with. A movement tied by a shared lane to one that
        /// conflicts keeps whole approaches apart; this shows where.
        /// </summary>
        private static void DescribeModel(EntityManager em, Entity node, bool leftHandTraffic, StringBuilder text)
        {
            Systems.JunctionLayout layout = Systems.JunctionAnalysis.Analyse(em, node, leftHandTraffic);
            if (layout == null)
                return;
            DescribeRuleBuffers(em, node, layout.Edges, text);
            Core.Planning.JunctionModel model = layout.Model;
            int n = model.Movements.Count;
            string Name(int m) => $"{model.Movements[m].Source}->{model.Movements[m].Target} {model.Movements[m].Kind}";
            text.Append($"  model: {model.ApproachCount} approaches, opposite {string.Join(",", model.OppositeOf)}, {n} movements\n");
            for (int a = 0; a < n; a++)
            {
                var shares = new StringBuilder();
                var hard = new StringBuilder();
                var yields = new StringBuilder();
                for (int b = 0; b < n; b++)
                {
                    if ((model.SharesLaneWith(a) & (1UL << b)) != 0)
                        shares.Append(shares.Length > 0 ? ", " : "").Append(Name(b));
                    Core.Planning.Relation r = model.Conflicts.Get(a, b);
                    if (r == Core.Planning.Relation.Hard)
                        hard.Append(hard.Length > 0 ? ", " : "").Append(Name(b));
                    else if (r == Core.Planning.Relation.Yields)
                        yields.Append(yields.Length > 0 ? ", " : "").Append(Name(b));
                }
                text.Append($"  model {Name(a)}: shares a lane with [{shares}]; never with [{hard}]; gives way to [{yields}]\n");
            }
        }

        private static bool SeenBefore(DynamicBuffer<JunctionLane> lanes, int index)
        {
            for (int k = 0; k < index; k++)
            {
                if (lanes[k].Approach == lanes[index].Approach)
                    return true;
            }
            return false;
        }

        /// <summary>The vehicles on a lane, and what holds the one nearest its end.</summary>
        private static string Front(EntityManager em, Entity lane)
        {
            if (!em.HasBuffer<LaneObject>(lane) || !em.HasComponent<Curve>(lane))
                return "no data";
            DynamicBuffer<LaneObject> objects = em.GetBuffer<LaneObject>(lane, true);
            if (objects.Length == 0)
                return "empty";
            float length = em.GetComponentData<Curve>(lane).m_Length;
            int front = 0;
            int standing = 0;
            for (int i = 0; i < objects.Length; i++)
            {
                if (math.cmax(objects[i].m_CurvePosition) > math.cmax(objects[front].m_CurvePosition))
                    front = i;
                if (Speed(em, objects[i].m_LaneObject) < 1.5f)
                    standing++;
            }
            Entity vehicle = objects[front].m_LaneObject;
            float distance = (1f - math.cmax(objects[front].m_CurvePosition)) * length;
            return $"{objects.Length} vehicles, {standing} standing; front {vehicle} {distance:0} m from the line at {Speed(em, vehicle):0.0} m/s{HeldBy(em, vehicle)}";
        }

        /// <summary>
        /// Why the game holds the vehicle, from its Blocker. A tram or a
        /// truck with trailer keeps it on the leading part, its Controller.
        /// </summary>
        private static string HeldBy(EntityManager em, Entity vehicle)
        {
            Entity holder = vehicle;
            if (em.HasComponent<Game.Vehicles.Controller>(vehicle))
            {
                Entity controller = em.GetComponentData<Game.Vehicles.Controller>(vehicle).m_Controller;
                if (controller != Entity.Null)
                    holder = controller;
            }
            if (!em.HasComponent<Game.Vehicles.Blocker>(holder))
                return ", no blocker data";
            var blocker = em.GetComponentData<Game.Vehicles.Blocker>(holder);
            string where = blocker.m_Blocker != Entity.Null && em.Exists(blocker.m_Blocker) && em.HasComponent<Game.Objects.Transform>(blocker.m_Blocker)
                ? $" at {Where(em, blocker.m_Blocker)}"
                : "";
            return $", held by {blocker.m_Type} {blocker.m_Blocker} ({KindOf(em, blocker.m_Blocker)}{where})";
        }

        /// <summary>Which lane the blocking object is on, so it can be found in the same dump.</summary>
        private static string Where(EntityManager em, Entity entity)
        {
            if (em.HasComponent<Game.Vehicles.CarCurrentLane>(entity))
                return $"lane {em.GetComponentData<Game.Vehicles.CarCurrentLane>(entity).m_Lane}";
            if (em.HasComponent<Game.Creatures.HumanCurrentLane>(entity))
                return $"lane {em.GetComponentData<Game.Creatures.HumanCurrentLane>(entity).m_Lane}";
            if (em.HasComponent<Game.Vehicles.TrainCurrentLane>(entity))
                return $"lane {em.GetComponentData<Game.Vehicles.TrainCurrentLane>(entity).m_Front.m_Lane}";
            return "unknown lane";
        }

        private static string Fill(EntityManager em, Entity lane)
        {
            if (!em.HasBuffer<LaneObject>(lane) || !em.HasComponent<Curve>(lane))
                return "no data";
            float length = em.GetComponentData<Curve>(lane).m_Length;
            int count = em.GetBuffer<LaneObject>(lane, true).Length;
            return $"{count} vehicles on {length:0} m";
        }

        private static float Speed(EntityManager em, Entity entity)
        {
            return em.HasComponent<Moving>(entity) ? math.length(em.GetComponentData<Moving>(entity).m_Velocity) : 0f;
        }

        private static string KindOf(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null)
                return "nothing";
            if (!em.Exists(entity))
                return "gone";
            if (em.HasComponent<Game.Vehicles.Vehicle>(entity))
                return em.HasComponent<Game.Vehicles.Train>(entity) ? "tram or train" : "vehicle";
            if (em.HasComponent<Creature>(entity))
                return "pedestrian, counts as a conflict for the scramble";
            if (em.HasComponent<Game.Net.Node>(entity))
                return "junction";
            if (em.HasComponent<Lane>(entity))
                return "lane";
            return "other";
        }
    }
}
