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
        public static string Describe(EntityManager em, Entity node)
        {
            var text = new StringBuilder();
            text.Append($"Diagnostics for junction {node}\n");
            if (!em.HasComponent<ManagedJunction>(node))
            {
                text.Append("  not managed by TLL\n");
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
                text.Append($"  scramble on demand {(junction.Options & JunctionOptions.ScrambleOnDemand) != 0}, diverting {runtime.Conflicts.Divert}, conflicts {runtime.Conflicts.Count}/{PedestrianConflicts.Window} [{history}], this green {runtime.ConflictThisGreen}\n");
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
                    text.Append($"  movement {m} [{roads.IndexOf(moves[m].Source)}->{roads.IndexOf(moves[m].Target)} {moves[m].Kind}]: recent {st.Recent:0}/h, peak {st.Peak:0}/h, queue {st.RecentQueue:0.0} (last round {st.LastQueue:0.0}, peak {st.PeakQueue:0.0})\n");
                }
            }

            DynamicBuffer<JunctionMovement> movements = em.GetBuffer<JunctionMovement>(node, true);
            var edges = Systems.NetGeometry.ConnectedEdges(em, node);
            DynamicBuffer<JunctionPhase> phases = em.GetBuffer<JunctionPhase>(node, true);
            for (int p = 0; p < phases.Length; p++)
            {
                var d = phases[p].Data;
                text.Append($"  phase {p + 1}: demand {d.Demand:0.#}, pressure {d.Pressure:0.#}, approaching {d.Approaching:0.#}, call {d.PedestrianCall}, waiting {SimTime.ToSeconds(d.WaitSteps):0} s, flags {d.Flags}\n");
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
