using System;
using TLL.Components;
using TLL.Core;
using TLL.Core.Advisor;
using TLL.Core.Control;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Metrics
{
    /// <summary>
    /// Builds the records of the metrics log from the junctions' counters.
    /// The tables and their fields are listed in tools/metrics/README.md.
    /// </summary>
    internal static class MetricsRecords
    {
        /// <summary>
        /// One record per junction and autopilot round in "rounds", and one
        /// per phase in "phase_rounds", from the counters of the round that
        /// just ended; call before the counters are cleared.
        /// </summary>
        public static void Round(EntityManager em, Entity node, ManagedJunction junction, JunctionRuntime runtime,
            DynamicBuffer<MovementCounter> counters, long elapsedSteps, uint frame, uint round, float worstQueue, bool backlog)
        {
            MetricsRow row = MetricsLog.Row("rounds", frame);
            if (row == null || elapsedSteps <= 0)
                return;
            float seconds = SimTime.ToSeconds((int)Math.Min(int.MaxValue, elapsedSteps));
            long vehicles = 0;
            long flowing = 0;
            float wait = 0f;
            float freeWait = 0f;
            float pedestrians = 0f;
            float blockedMax = 0f;
            float blockedSum = 0f;
            int withTraffic = 0;
            for (int i = 0; i < counters.Length; i++)
            {
                MovementCounter c = counters[i];
                // Crosswalks have a length; their counters are people, turned
                // into people per hour as AutopilotSystem.Measure does.
                if (c.Length > 0f)
                {
                    float crossing = math.max(3f, c.Length / DelayModel.WalkingSpeed);
                    pedestrians += c.PedestrianSteps * SimTime.SecondsPerStep / crossing / seconds * 3600f;
                    continue;
                }
                vehicles += c.Vehicles;
                flowing += c.Flowing;
                wait += c.QueueSteps * SimTime.SecondsPerStep;
                freeWait += c.FreeQueueSteps * SimTime.SecondsPerStep;
                float blocked = (float)c.BlockedSteps / elapsedSteps;
                blockedMax = math.max(blockedMax, blocked);
                if (c.Vehicles > 0 || c.QueueSteps > 0f)
                {
                    blockedSum += blocked;
                    withTraffic++;
                }
            }
            MetricsLog.Write(Junction(row, node, junction, round, frame)
                .Add("turn_on_red", (junction.Options & JunctionOptions.TurnOnRed) != 0)
                .Add("scramble_on_demand", (junction.Options & JunctionOptions.ScrambleOnDemand) != 0)
                .Add("diverting", runtime.Conflicts.Divert)
                .Add("elapsed_s", seconds)
                .Add("vehicles", vehicles)
                .Add("vehicles_per_h", vehicles / seconds * 3600f)
                .Add("flowing", flowing)
                .Add("wait_s", wait)
                .Add("free_wait_s", freeWait)
                .Add("wait_per_vehicle_s", vehicles > 0 ? wait / vehicles : float.NaN)
                .Add("blocked_share_max", blockedMax)
                .Add("blocked_share_mean", withTraffic > 0 ? blockedSum / withTraffic : 0f)
                .Add("pedestrians_per_h", pedestrians)
                .Add("worst_free_queue", worstQueue)
                .Add("backlog", backlog));

            if (!em.HasBuffer<JunctionPhase>(node))
                return;
            DynamicBuffer<JunctionPhase> phases = em.GetBuffer<JunctionPhase>(node, true);
            for (int p = 0; p < phases.Length; p++)
            {
                PhaseData d = phases[p].Data;
                PhaseMetrics m = d.Metrics;
                MetricsLog.Write(Junction(MetricsLog.Row("phase_rounds", frame), node, junction, round, frame)
                    .Add("phase", p)
                    .Add("flags", d.Flags.ToString())
                    .Add("min_green_s", SimTime.ToSeconds(d.MinGreen))
                    .Add("max_green_s", SimTime.ToSeconds(d.MaxGreen))
                    .Add("planned_green_s", SimTime.ToSeconds(d.Green))
                    .Add("walk_green_s", SimTime.ToSeconds(d.WalkGreen))
                    .Add("greens", m.Greens)
                    .Add("green_s", SimTime.ToSeconds((int)m.GreenSteps))
                    .Add("served", m.Served)
                    .Add("failures", m.Failures)
                    .Add("residual_queue", m.ResidualQueue)
                    .Add("wait_at_start_s", SimTime.ToSeconds((int)m.WaitAtStart))
                    .Add("end_empty", m.EndEmpty)
                    .Add("end_maximum", m.EndMaximum)
                    .Add("end_starved", m.EndStarved)
                    .Add("end_outweighed", m.EndOutweighed)
                    .Add("end_blocked", m.EndBlocked)
                    .Add("end_emergency", m.EndEmergency)
                    .Add("end_schedule", m.EndSchedule));
            }
        }

        /// <summary>Starts the next round of the phases' counters.</summary>
        public static void ClearPhases(EntityManager em, Entity node)
        {
            if (!em.HasBuffer<JunctionPhase>(node))
                return;
            DynamicBuffer<JunctionPhase> phases = em.GetBuffer<JunctionPhase>(node);
            for (int p = 0; p < phases.Length; p++)
                phases.ElementAt(p).Data.Metrics.Clear();
        }

        /// <summary>
        /// A record in "decisions": what changed at a junction and why. The
        /// caller adds the details and writes it; null while the log is off.
        /// </summary>
        public static MetricsRow Decision(uint frame, Entity node, string kind)
        {
            MetricsRow row = MetricsLog.Row("decisions", frame);
            return row?.Add("node", node.Index).Add("kind", kind).Add("minute", MinuteOfDay(frame));
        }

        /// <summary>A record in "reviews", with the junction's key fields; null while the log is off.</summary>
        public static MetricsRow Review(uint frame, Entity node, ManagedJunction junction, uint round)
        {
            MetricsRow row = MetricsLog.Row("reviews", frame);
            return row == null ? null : Junction(row, node, junction, round, frame);
        }

        /// <summary>Short column names for the layouts, per JunctionAdvisor.Strategies.</summary>
        public static readonly string[] LayoutColumns = { "permissive", "protected", "split", "scramble" };

        private static MetricsRow Junction(MetricsRow row, Entity node, ManagedJunction junction, uint round, uint frame)
        {
            return row.Add("node", node.Index)
                .Add("round", round)
                .Add("window", Systems.AutopilotSystem.TimeWindow(round))
                .Add("minute", MinuteOfDay(frame))
                .Add("origin", junction.Origin.ToString())
                .Add("mode", junction.Mode.ToString())
                .Add("layout", junction.Strategy.ToString())
                .Add("wave_group", junction.Group);
        }

        /// <summary>Minutes since the start of the game day, 0 to 1439.</summary>
        private static int MinuteOfDay(uint frame)
        {
            return (int)(frame % Game.Simulation.TimeSystem.kTicksPerDay * 1440L / Game.Simulation.TimeSystem.kTicksPerDay);
        }
    }
}
