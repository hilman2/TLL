using System;
using System.Collections.Generic;
using Game;
using Game.City;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using TLL.Components;
using TLL.Core;
using TLL.Core.Advisor;
using TLL.Core.Control;
using TLL.Core.Planning;
using TLL.Metrics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Systems
{
    /// <summary>
    /// Measures the traffic of every managed junction and, for automatic
    /// junctions, decides how they are run: phase layout, control mode and
    /// flashing yellow at low traffic.
    ///
    /// A round runs every 4096 simulation frames. A game day has 262144
    /// (TimeSystem.kTicksPerDay), so that is 64 rounds a day, one every 22.5
    /// minutes on the game clock. Each round turns the counters of the
    /// control job into rates and updates the recent, daily and peak figures.
    /// The layout is reviewed every 4 rounds (1.5 game hours) against the peak
    /// figures, the traffic the junction must cope with, and changes only
    /// when two reviews in a row agree. The review also decides whether
    /// pedestrians stay in their own phase (see PedestrianConflicts), at
    /// every junction that has one on demand.
    /// </summary>
    public partial class AutopilotSystem : TllSystemBase
    {
        /// <summary>
        /// Rounds from one review to the next. The peak figures a review
        /// works from change slowly, and the two reviews a change needs and
        /// the margin JunctionAdvisor asks for keep it from flipping; more
        /// rounds in between only make it slower to follow the city.
        /// </summary>
        public const int kLayoutEvery = 4;
        public const int kRoundFrames = 4096;

        /// <summary>Share of the new round in the recent figure: the last three or so rounds, about a game hour, count.</summary>
        private const float kRecentWeight = 0.3f;

        /// <summary>Share of the new round in the daily figure: about the last 50 rounds, most of a game day, count.</summary>
        private const float kDailyWeight = 0.02f;

        /// <summary>Per round the peak falls back by this factor: to half in 46 rounds, about 17 game hours.</summary>
        private const float kPeakDecay = 0.985f;

        /// <summary>
        /// Per round within its window, a time-of-day window's peak falls
        /// back by this factor: by about 15 % a day over its 8 rounds, so the
        /// last few days count.
        /// </summary>
        private const float kWindowDecay = 0.98f;

        /// <summary>A layout review needs at least this much traffic, in vehicles per hour, to mean anything.</summary>
        private const float kMinimumVolume = 30f;

        private SimulationSystem m_Simulation;
        private CityConfigurationSystem m_CityConfiguration;
        private EntityQuery m_Query;
        // Summed from one review round of all junctions to the next, for one log line.
        private int m_Estimated;
        private readonly System.Diagnostics.Stopwatch m_ReviewTime = new System.Diagnostics.Stopwatch();
        private int m_LayoutChanges;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kRoundFrames;
        }

        /// <summary>
        /// Simulation frames from <paramref name="frameIndex"/> to the next
        /// round in which <paramref name="node"/> is reviewed.
        /// </summary>
        public static uint FramesToReview(uint frameIndex, Entity node)
        {
            uint round = frameIndex / kRoundFrames + 1;
            while ((round + (uint)node.Index) % kLayoutEvery != 0)
                round++;
            return round * kRoundFrames - frameIndex;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_CityConfiguration = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<ManagedJunction>(),
                    ComponentType.ReadWrite<MovementCounter>(),
                    ComponentType.ReadWrite<MovementStatistics>(),
                    ComponentType.ReadOnly<JunctionMovement>(),
                    ComponentType.ReadWrite<JunctionRuntime>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<JunctionDirty>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            RequireForUpdate(m_Query);
        }

        protected override void OnSafeUpdate()
        {
            long now = SimTime.StepOfFrame(m_Simulation.frameIndex);
            // Rounds are counted on the game's clock, which the save keeps,
            // so the regular layout reviews come on time however often the
            // city is loaded. Each junction has its review in a different
            // one of the kLayoutEvery rounds, which also spreads the work.
            uint round = m_Simulation.frameIndex / kRoundFrames;
            Setting settings = Mod.Settings;

            using (NativeArray<Entity> nodes = m_Query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    DynamicBuffer<MovementCounter> counters = EntityManager.GetBuffer<MovementCounter>(node);
                    JunctionRuntime runtime = EntityManager.GetComponentData<JunctionRuntime>(node);
                    // The counters run since the junction was last built or
                    // measured. After a build, a load, or with the clock
                    // behind (another city), that start is not known and the
                    // round is not measured.
                    ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
                    float worstQueue = 0f;
                    if (runtime.CountsSince > 0 && now > runtime.CountsSince)
                    {
                        Measure(node, counters, now - runtime.CountsSince, round);
                        worstQueue = WorstQueuePerLane(node);
                        AddToPeriod(ref runtime, counters, junction.Mode, worstQueue);
                        MetricsRecords.Round(EntityManager, node, junction, runtime, counters, now - runtime.CountsSince, m_Simulation.frameIndex, round, worstQueue, worstQueue >= kBacklogQueue);
                    }
                    MetricsRecords.ClearPhases(EntityManager, node);
                    for (int i = 0; i < counters.Length; i++)
                    {
                        ref MovementCounter c = ref counters.ElementAt(i);
                        c.Vehicles = 0;
                        c.PedestrianSteps = 0f;
                        c.QueueSteps = 0f;
                        c.FreeQueueSteps = 0f;
                        c.Flowing = 0;
                        c.BlockedSteps = 0;
                    }
                    runtime.CountsSince = now;
                    bool layoutRound = (round + (uint)node.Index) % kLayoutEvery == 0;
                    EntityManager.SetComponentData(node, runtime);
                    UpdateHealth(node);
                    if (settings != null && settings.AutoManageAll && junction.Origin == JunctionOrigin.Auto)
                    {
                        try
                        {
                            Decide(node, junction, settings, layoutRound, round, runtime, worstQueue);
                        }
                        catch (Exception e)
                        {
                            // One junction the autopilot cannot judge keeps its
                            // current settings; the others go on.
                            Mod.Log.Error(e, $"Junction {node}: the autopilot could not decide, it keeps its settings.");
                        }
                    }
                    // The review has used the period; the next one starts.
                    // Decide may have changed the entity's components, so the
                    // runtime is written back by entity, not by reference.
                    if (layoutRound && EntityManager.HasComponent<JunctionRuntime>(node))
                    {
                        JunctionRuntime next = EntityManager.GetComponentData<JunctionRuntime>(node);
                        ClearPeriod(ref next);
                        EntityManager.SetComponentData(node, next);
                    }
                }
            }
            MetricsLog.Flush();
            if (round % kLayoutEvery == 0)
            {
                Mod.Log.Info($"Autopilot: {m_Estimated} layout estimate(s) in {m_ReviewTime.Elapsed.TotalMilliseconds:0} ms, {m_LayoutChanges} layout change(s) in the last {kLayoutEvery} rounds.");
                m_Estimated = 0;
                m_LayoutChanges = 0;
                m_ReviewTime.Reset();
            }
        }

        /// <summary>Turns the round's counts into rates and updates the running figures.</summary>
        private void Measure(Entity node, DynamicBuffer<MovementCounter> counters, long elapsedSteps, uint round)
        {
            int window = TimeWindow(round);
            DynamicBuffer<MovementStatistics> statistics = EntityManager.GetBuffer<MovementStatistics>(node);
            if (statistics.Length != counters.Length || elapsedSteps <= 0)
                return;
            float seconds = SimTime.ToSeconds((int)Math.Min(int.MaxValue, elapsedSteps));
            for (int i = 0; i < counters.Length; i++)
            {
                MovementCounter c = counters[i];
                float rate;
                if (c.Length > 0f)
                {
                    // Crosswalk: person-steps on it, divided by the steps one
                    // person needs to cross, is the number of people.
                    float crossing = math.max(3f, c.Length / DelayModel.WalkingSpeed);
                    rate = c.PedestrianSteps * SimTime.SecondsPerStep / crossing / seconds * 3600f;
                }
                else
                {
                    rate = c.Vehicles / seconds * 3600f;
                }
                float queue = c.QueueSteps / elapsedSteps;

                ref MovementStatistics s = ref statistics.ElementAt(i);
                bool fresh = s.Recent == 0f && s.Daily == 0f && s.Peak == 0f;
                s.Recent = fresh ? rate : s.Recent + kRecentWeight * (rate - s.Recent);
                s.Daily = fresh ? rate : s.Daily + kDailyWeight * (rate - s.Daily);
                s.Peak = math.max(s.Peak * kPeakDecay, s.Recent);
                s.Queue = fresh ? queue : s.Queue + kDailyWeight * (queue - s.Queue);
                // The peak follows the smoothed queue, like Peak follows
                // Recent: one jammed round alone does not make a problem spot.
                s.RecentQueue = fresh ? queue : s.RecentQueue + kRecentWeight * (queue - s.RecentQueue);
                s.PeakQueue = math.max(s.PeakQueue * kPeakDecay, s.RecentQueue);
                s.LastQueue = c.FreeQueueSteps / elapsedSteps;
                s.SetWindow(window, math.max(s.Window(window) * kWindowDecay, s.Recent));
            }
        }

        /// <summary>Sums up the junction's statistics for the problem list and the map.</summary>
        private void UpdateHealth(Entity node)
        {
            DynamicBuffer<MovementStatistics> statistics = EntityManager.GetBuffer<MovementStatistics>(node, true);
            DynamicBuffer<MovementCounter> counters = EntityManager.GetBuffer<MovementCounter>(node, true);
            var health = new JunctionHealth();
            for (int i = 0; i < statistics.Length && i < counters.Length; i++)
            {
                // Crosswalks have a length; their "queue" is not a traffic jam.
                if (counters[i].Length > 0f)
                    continue;
                health.WorstQueue = math.max(health.WorstQueue, statistics[i].PeakQueue);
            }
            if (EntityManager.HasComponent<JunctionHealth>(node))
                EntityManager.SetComponentData(node, health);
            else
                EntityManager.AddComponentData(node, health);
        }

        /// <param name="round">The autopilot round, for the time-of-day windows.</param>
        /// <param name="period">The runtime with the measurement period this round ends.</param>
        /// <param name="worstQueue">Mean vehicles waiting per lane over the round, on the worst approach.</param>
        private void Decide(Entity node, ManagedJunction junction, Setting settings, bool layoutRound, uint round, JunctionRuntime period, float worstQueue)
        {
            AutopilotState state = EntityManager.HasComponent<AutopilotState>(node)
                ? EntityManager.GetComponentData<AutopilotState>(node)
                : new AutopilotState { Flash = FlashSchedule.Start, Layout = LayoutSchedule.Start };
            DynamicBuffer<MovementStatistics> statistics = EntityManager.GetBuffer<MovementStatistics>(node, true);
            DynamicBuffer<JunctionMovement> movements = EntityManager.GetBuffer<JunctionMovement>(node, true);
            List<Entity> edges = NetGeometry.ConnectedEdges(EntityManager, node);
            int[] opposite = ChordModel.FindOpposites(NetGeometry.ApproachAngles(EntityManager, node, edges));

            var recent = new float[edges.Count];
            var peak = new float[edges.Count];
            for (int m = 0; m < movements.Length && m < statistics.Length; m++)
            {
                if (movements[m].Kind == MovementKind.Pedestrian)
                    continue;
                int source = edges.IndexOf(movements[m].Source);
                if (source < 0)
                    continue;
                recent[source] += statistics[m].Recent;
                peak[source] += statistics[m].Peak;
            }
            Road(recent, opposite, out int majorApproach, out float major, out float minor, out float minorTotal);
            state.MajorVolume = major;
            state.MinorVolume = minor;

            // Structural changes wait until the end: adding a component moves
            // the entity and invalidates the buffers read above.
            bool changed = false;
            bool dirty = false;
            bool rebuild = false;

            // Flashing yellow at low traffic.
            bool flashing = junction.Mode == ControlMode.Flashing;
            bool wantFlash = state.Flash.Round(flashing, settings.AutoFlash && majorApproach >= 0, major, minor, minorTotal, worstQueue,
                state.SignalAdvice == SignalAdvice.RemoveSignals);
            if (wantFlash != flashing)
            {
                bool backlog = !wantFlash && worstQueue >= FlashSchedule.BacklogQueue;
                if (backlog && settings.VerboseLogging)
                    Mod.Log.Info($"Autopilot: junction {node} stops flashing, {worstQueue:0.#} vehicles waiting per lane.");
                MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, node, "flash")?
                    .Add("to", wantFlash)
                    .Add("reason", backlog ? "backlog" : !settings.AutoFlash ? "setting"
                        : state.SignalAdvice == SignalAdvice.RemoveSignals ? "advice" : "traffic")
                    .Add("major_per_h", major)
                    .Add("minor_per_h", minor)
                    .Add("minor_total_per_h", minorTotal)
                    .Add("side_load", FlashAdvisor.SideLoad(major, minor))
                    .Add("worst_free_queue", worstQueue));
                if (wantFlash)
                {
                    junction.Mode = ControlMode.Flashing;
                    // The lanes of the main road are marked when the junction
                    // is set up, so a new main road needs a new set-up.
                    if (junction.MajorApproach != edges[majorApproach])
                    {
                        junction.MajorApproach = edges[majorApproach];
                        dirty = true;
                    }
                }
                else
                {
                    // Back to the setting's mode, also for a junction that was
                    // in a green wave before: its offset is stale by now, and
                    // the next coordination round takes it back in if it fits.
                    junction.Mode = settings.AutoControl();
                }
                changed = true;
            }

            // Control mode of a junction that neither flashes nor runs in a
            // green wave follows the setting.
            ControlMode wanted = settings.AutoControl();
            if (junction.Mode != ControlMode.Flashing && junction.Mode != ControlMode.Coordinated && junction.Mode != wanted)
            {
                junction.Mode = wanted;
                changed = true;
            }

            if (state.WaveBan > 0)
                state.WaveBan--;
            if (state.LayoutHold > 0)
                state.LayoutHold--;

            // The turns to forbid, reviewed with the layout. New rules take
            // effect with the rebuild below, and the layout waits for the
            // next review: the junction it would be chosen for is about to
            // change.
            List<TurnRule> turnRules = null;
            if (layoutRound && junction.Mode != ControlMode.Flashing)
            {
                m_ReviewTime.Start();
                try
                {
                    turnRules = ReviewTurns(node, settings, statistics, movements);
                }
                catch (Exception e)
                {
                    Mod.Log.Error(e, $"Junction {node}: the autopilot could not review its turns.");
                }
                m_ReviewTime.Stop();
            }

            // The lane arrows, one change at a time: not while the turns change.
            List<LaneConnectionRule> laneRules = null;
            if (layoutRound && turnRules == null && junction.Mode != ControlMode.Flashing)
            {
                m_ReviewTime.Start();
                try
                {
                    laneRules = ReviewLaneArrows(node, settings, statistics, movements, edges);
                }
                catch (Exception e)
                {
                    Mod.Log.Error(e, $"Junction {node}: the autopilot could not review its lane arrows.");
                }
                m_ReviewTime.Stop();
            }

            // A junction without an estimate yet gets one at once, from the
            // saved statistics; only changing its layout waits for the
            // regular reviews.
            // A failing estimate must not cost the decisions above.
            if (layoutRound || !state.HasEstimate)
            {
                m_ReviewTime.Start();
                try
                {
                    ReviewLayout(node, ref junction, ref state, settings, statistics, peak, opposite, layoutRound, round, period,
                        turnRules != null || laneRules != null, ref changed, ref rebuild);
                }
                catch (Exception e)
                {
                    Mod.Log.Error(e, $"Junction {node}: the autopilot could not compare layouts.");
                }
                m_ReviewTime.Stop();
            }

            if (turnRules != null)
            {
                DynamicBuffer<TurnRule> buffer = EntityManager.HasBuffer<TurnRule>(node)
                    ? EntityManager.GetBuffer<TurnRule>(node)
                    : EntityManager.AddBuffer<TurnRule>(node);
                buffer.Clear();
                foreach (TurnRule rule in turnRules)
                    buffer.Add(rule);
                // The game builds the junction's lanes anew, LaneRuleSystem
                // flags them, and the set-up plans the signals without them.
                rebuild = true;
            }
            if (laneRules != null)
            {
                DynamicBuffer<LaneConnectionRule> buffer = EntityManager.HasBuffer<LaneConnectionRule>(node)
                    ? EntityManager.GetBuffer<LaneConnectionRule>(node)
                    : EntityManager.AddBuffer<LaneConnectionRule>(node);
                buffer.Clear();
                foreach (LaneConnectionRule rule in laneRules)
                    buffer.Add(rule);
                rebuild = true;
            }
            if (changed || rebuild)
                EntityManager.SetComponentData(node, junction);
            if (rebuild)
            {
                // The set-up generates a new plan for an automatic junction on
                // every rebuild and keeps the timing of phases that stay the
                // same; the rebuild of the node brings the poles along.
                EntityManager.AddComponent<RebuildRequest>(node);
                // A junction in a green wave has a new plan the wave does not
                // know yet; it is planned again without waiting for its round.
                if (junction.Mode == ControlMode.Coordinated)
                    Requests.RebuildGreenWaves = true;
            }
            else if (dirty)
            {
                EntityManager.AddComponent<JunctionDirty>(node);
            }
            if (EntityManager.HasComponent<AutopilotState>(node))
                EntityManager.SetComponentData(node, state);
            else
                EntityManager.AddComponentData(node, state);
        }

        /// <summary>
        /// The turn rules the junction should have from now on, or null to
        /// keep its own (TurnReview). With the setting off, the autopilot's
        /// rules go and the player's stay.
        /// </summary>
        private List<TurnRule> ReviewTurns(Entity node, Setting settings, DynamicBuffer<MovementStatistics> statistics, DynamicBuffer<JunctionMovement> movements)
        {
            if (!settings.AutoTurnBans)
            {
                if (!EntityManager.HasBuffer<TurnRule>(node))
                    return null;
                DynamicBuffer<TurnRule> stored = EntityManager.GetBuffer<TurnRule>(node, true);
                var players = new List<TurnRule>();
                for (int i = 0; i < stored.Length; i++)
                {
                    if (stored[i].ByPlayer)
                        players.Add(stored[i]);
                }
                return players.Count != stored.Length ? players : null;
            }

            // By the day's peak, like the phases are laid out: a turn is not
            // forbidden in the morning and allowed again at night.
            var volumeOf = new Dictionary<(Entity, Entity, MovementKind), float>();
            float total = 0f;
            for (int m = 0; m < movements.Length && m < statistics.Length; m++)
            {
                if (movements[m].Kind == MovementKind.Pedestrian)
                    continue;
                volumeOf[(movements[m].Source, movements[m].Target, movements[m].Kind)] = statistics[m].Peak;
                total += statistics[m].Peak;
            }
            if (total < kMinimumVolume)
                return null;
            DelayParameters parameters = DelayParameters.Default;
            parameters.TurnOnRed = settings.TurnOnRed;
            List<TurnRule> rules = TurnReview.Decide(EntityManager, node, m_CityConfiguration.leftHandTraffic, parameters, volumeOf, out string summary);
            if (rules == null)
                return null;
            Mod.Log.Info($"Autopilot: junction {node}{summary}.");
            MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, node, "turns")?.Add("summary", summary).Add("rules", rules.Count));
            return rules;
        }

        /// <summary>
        /// The lane rules the junction should have from now on, or null to
        /// keep its own (LaneArrowReview), judged by the delay of the best
        /// phase layout at the day's peak. With the setting off, the
        /// autopilot's lane rules go and the player's stay.
        /// </summary>
        private List<LaneConnectionRule> ReviewLaneArrows(Entity node, Setting settings, DynamicBuffer<MovementStatistics> statistics,
            DynamicBuffer<JunctionMovement> movements, List<Entity> edges)
        {
            if (!settings.AutoLaneArrows)
            {
                if (!EntityManager.HasBuffer<LaneConnectionRule>(node))
                    return null;
                DynamicBuffer<LaneConnectionRule> stored = EntityManager.GetBuffer<LaneConnectionRule>(node, true);
                var players = new List<LaneConnectionRule>();
                for (int i = 0; i < stored.Length; i++)
                {
                    if (!stored[i].Auto)
                        players.Add(stored[i]);
                }
                return players.Count != stored.Length ? players : null;
            }
            JunctionLayout layout = JunctionAnalysis.Analyse(EntityManager, node, m_CityConfiguration.leftHandTraffic);
            if (layout == null || layout.Keys.Count != statistics.Length || movements.Length != statistics.Length)
                return null;
            var flow = new float[edges.Count, edges.Count];
            var volumes = new float[statistics.Length];
            float total = 0f;
            for (int m = 0; m < statistics.Length; m++)
            {
                volumes[m] = statistics[m].Peak;
                MovementKind kind = movements[m].Kind;
                if (kind == MovementKind.Pedestrian || kind == MovementKind.Track)
                    continue;
                int s = edges.IndexOf(movements[m].Source);
                int t = edges.IndexOf(movements[m].Target);
                if (s < 0 || t < 0)
                    continue;
                flow[s, t] += statistics[m].Peak;
                total += statistics[m].Peak;
            }
            if (total < kMinimumVolume)
                return null;
            DelayParameters parameters = DelayParameters.Default;
            parameters.TurnOnRed = settings.TurnOnRed;
            var model = new LaneArrowReview.SignalModel { Layout = layout, Volumes = volumes, Parameters = parameters };
            List<LaneConnectionRule> rules = LaneArrowReview.Decide(EntityManager, node, flow, model, m_CityConfiguration.leftHandTraffic, out string summary);
            if (rules == null)
                return null;
            Mod.Log.Info($"Autopilot: junction {node} changes its lane arrows: {summary}.");
            MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, node, "lanes")?.Add("summary", summary).Add("rules", rules.Count));
            return rules;
        }

        /// <summary>
        /// Estimates every layout from the peak traffic for the panel, and
        /// with <paramref name="decide"/> set, a regular review, changes the
        /// layout where the setting leaves it to the autopilot, and sets
        /// turning on red where it pays.
        /// </summary>
        /// <param name="hold">Keep the layout this time: the junction's turns change with this review.</param>
        private void ReviewLayout(Entity node, ref ManagedJunction junction, ref AutopilotState state, Setting settings,
            DynamicBuffer<MovementStatistics> statistics, float[] peakByApproach, int[] opposite, bool decide, uint round, JunctionRuntime period,
            bool hold, ref bool changed, ref bool rebuild)
        {
            // The layout the measurement period ran with, before a setting
            // may change it below.
            PlanStrategy ran = junction.Strategy;
            bool ranScramble = (junction.Options & JunctionOptions.Scramble) != 0;
            if (decide && settings.AutoLayout != AutoLayout.Automatic)
            {
                PlanStrategy fixedChoice = settings.InitialStrategy();
                bool fixedScramble = settings.InitialScramble();
                if (junction.Strategy != fixedChoice || ranScramble != fixedScramble)
                {
                    junction.Strategy = fixedChoice;
                    junction.Options = fixedScramble ? junction.Options | JunctionOptions.Scramble : junction.Options & ~JunctionOptions.Scramble;
                    rebuild = true;
                }
            }

            JunctionLayout layout = JunctionAnalysis.Analyse(EntityManager, node, m_CityConfiguration.leftHandTraffic);
            if (layout == null || layout.Keys.Count != statistics.Length)
                return;
            // The traffic the layout must cope with until the next review:
            // what this time of day, and the next window if the review
            // period reaches into it, brought on the last days. Until the
            // windows have seen a day, the day's peak, so that a layout
            // chosen early copes with the rush hour.
            int window = TimeWindow(round);
            int nextWindow = TimeWindow(round + kLayoutEvery);
            var volumes = new float[statistics.Length];
            var recent = new float[statistics.Length];
            // The phases are laid out by the day's peak, as the set-up lays
            // them out (JunctionInitSystem): what is estimated is what runs.
            var weights = new float[statistics.Length];
            float total = 0f;
            for (int m = 0; m < volumes.Length; m++)
            {
                MovementStatistics s = statistics[m];
                float windowed = math.max(s.Window(window), s.Window(nextWindow));
                volumes[m] = windowed > 0f ? math.max(windowed, s.Recent) : s.Peak;
                recent[m] = s.Recent;
                weights[m] = s.Peak;
                if (!layout.Model.Movements[m].IsPedestrian)
                    total += volumes[m];
            }
            state.TooQuiet = total < kMinimumVolume;
            if (state.TooQuiet)
                return;

            int running = Array.IndexOf(JunctionAdvisor.Strategies, ran);
            bool wave = junction.Mode == ControlMode.Coordinated;
            bool recorded = false;
            if (decide)
            {
                recorded = Remember(ref state, layout.Model, junction, ran, running, wave, ranScramble, recent, weights, period);
                state.Memory.Fade(running, ranScramble);
            }

            // With turning on red allowed, the layouts are compared as the
            // junction would run them, with it wherever it pays; each with a
            // scramble and without. Then the model's estimates are corrected
            // by what the junction measured when it ran them.
            DelayParameters parameters = DelayParameters.Default;
            parameters.TurnOnRed = settings.TurnOnRed;
            PlanEstimate[] estimates = JunctionAdvisor.EvaluateAll(layout.Model, volumes, parameters, weights);
            PlanEstimate[] corrected = JunctionAdvisor.Correct(estimates, state.Memory, running, wave, false, ranScramble);
            PlanEstimate[] scrambled = JunctionAdvisor.EvaluateAll(layout.Model, volumes, parameters, weights, scramble: true);
            PlanEstimate[] scrambledCorrected = JunctionAdvisor.Correct(scrambled, state.Memory, running, wave, true, ranScramble);
            state.HasEstimate = true;
            m_Estimated++;
            state.LayoutDelay = Pack(corrected, e => e.AverageDelay);
            state.LayoutSaturation = Pack(corrected, e => e.WorstSaturation);
            state.ScrambleDelay = Pack(scrambledCorrected, e => e.AverageDelay);
            state.ScrambleSaturation = Pack(scrambledCorrected, e => e.WorstSaturation);

            PlanStrategy choice = JunctionAdvisor.Choose(junction.Strategy, ranScramble, corrected, scrambledCorrected, out bool choiceScramble);
            int chosen = Array.IndexOf(JunctionAdvisor.Strategies, choice);
            PlanEstimate best = choiceScramble ? scrambledCorrected[chosen] : corrected[chosen];
            Road(peakByApproach, opposite, out _, out float major, out float minor, out _);
            state.SignalAdvice = SignalAdvisor.Decide(true, major, minor, best.AverageDelay);

            if (!decide)
                return;
            // Turning on red for the layout the junction runs from now on.
            bool automatic = settings.AutoLayout == AutoLayout.Automatic;
            PlanStrategy upcoming = automatic ? choice : junction.Strategy;
            bool upcomingScramble = automatic ? choiceScramble : (junction.Options & JunctionOptions.Scramble) != 0;
            int next = Array.IndexOf(JunctionAdvisor.Strategies, upcoming);
            bool turnOnRed = settings.TurnOnRed && next >= 0 && JunctionAdvisor.WantsTurnOnRed(
                DelayModel.Estimate(layout.Model, PhasePlanner.Build(layout.Model, upcoming, weights, upcomingScramble), volumes, DelayParameters.Default),
                upcomingScramble ? scrambled[next] : estimates[next]);
            if (turnOnRed != ((junction.Options & JunctionOptions.TurnOnRed) != 0))
            {
                junction.Options ^= JunctionOptions.TurnOnRed;
                changed = true;
                MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, node, "turn_on_red")?.Add("to", turnOnRed).Add("layout", upcoming.ToString()));
            }

            bool jammed = running >= 0 && state.Memory.Get(running, wave, ranScramble).Backlog >= LayoutMemory.BacklogShare;
            // A layout the green waves chose stays while their trial runs and
            // while the wave does: a change would rebuild the junction and
            // take its timing out of the wave. Whether the wave helps, the
            // measurement decides (CoordinationSystem.Hurts), which then
            // frees the layout again.
            bool held = state.LayoutHold > 0 || wave || hold;
            bool change = automatic && !held && state.Layout.Review(junction.Strategy, ranScramble, choice, choiceScramble, jammed);
            WriteReview(node, junction, round, state, estimates, corrected, scrambled, scrambledCorrected, wave, choice, choiceScramble,
                jammed, change, recorded, period, held);
            if (!change)
                return;
            bool tried = state.Memory.Get(chosen, wave, choiceScramble).Measured;
            PlanEstimate now = running < 0 ? default : ranScramble ? scrambledCorrected[running] : corrected[running];
            MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, node, "layout")?
                .Add("from", junction.Strategy.ToString())
                .Add("to", choice.ToString())
                .Add("from_scramble", ranScramble)
                .Add("to_scramble", choiceScramble)
                .Add("jammed", jammed)
                .Add("tried", tried)
                .Add("expected_delay_s", best.AverageDelay)
                .Add("current_delay_s", running >= 0 ? now.AverageDelay : float.NaN)
                .Add("window", window));
            if (settings.VerboseLogging)
            {
                Mod.Log.Info($"Autopilot: junction {node} changes from {Name(junction.Strategy, ranScramble)} to {Name(choice, choiceScramble)},"
                    + $" expected mean delay {best.AverageDelay:0.0} s ({(tried ? "measured before" : "not tried yet")}){(jammed ? ", the current layout jams" : "")};"
                    + $" now {now.AverageDelay:0.0} s at load {now.WorstSaturation:0.00}, {Remembered(state.Memory, running, wave, ranScramble)};"
                    + $" then load {best.WorstSaturation:0.00}, {Remembered(state.Memory, chosen, wave, choiceScramble)}; traffic window {window}.");
            }
            m_LayoutChanges++;
            junction.Strategy = choice;
            junction.Options = choiceScramble ? junction.Options | JunctionOptions.Scramble : junction.Options & ~JunctionOptions.Scramble;
            rebuild = true;
        }

        private static string Name(PlanStrategy strategy, bool scramble)
        {
            return scramble ? $"{strategy} with scramble" : strategy.ToString();
        }

        /// <summary>One value per layout, in JunctionAdvisor.Strategies order; -1 for one that cannot run, 0 past the last.</summary>
        private static float4 Pack(PlanEstimate[] estimates, Func<PlanEstimate, float> value)
        {
            var result = new float4(0f);
            for (int i = 0; i < estimates.Length && i < 4; i++)
                result[i] = JunctionAdvisor.IsAvailable(estimates[i]) ? value(estimates[i]) : -1f;
            return result;
        }

        /// <summary>
        /// One record per review in the metrics log: every layout's estimate,
        /// as the model has it and as corrected by the junction's memory, the
        /// memory itself, the measurement the review recorded, and what it
        /// decided.
        /// </summary>
        private void WriteReview(Entity node, ManagedJunction junction, uint round, AutopilotState state, PlanEstimate[] estimates, PlanEstimate[] corrected,
            PlanEstimate[] scrambled, PlanEstimate[] scrambledCorrected, bool wave, PlanStrategy choice, bool choiceScramble,
            bool jammed, bool change, bool recorded, JunctionRuntime period, bool held)
        {
            MetricsRow row = MetricsRecords.Review(m_Simulation.frameIndex, node, junction, round);
            if (row == null)
                return;
            row.Add("wave", wave)
                .Add("choice", choice.ToString())
                .Add("choice_scramble", choiceScramble)
                .Add("jammed", jammed)
                .Add("change", change)
                .Add("held", held)
                .Add("pending", state.Layout.PendingReviews)
                .Add("age", state.Layout.Age)
                .Add("recorded", recorded)
                .Add("period_rounds", period.PeriodRounds)
                .Add("period_vehicles", period.PeriodVehicles)
                .Add("period_backlog", period.PeriodBacklog)
                .Add("measured_wait_s", recorded ? state.MeasuredWait : float.NaN)
                .Add("modelled_wait_s", recorded ? state.ModelledWait : float.NaN);
            for (int i = 0; i < estimates.Length && i < MetricsRecords.LayoutColumns.Length; i++)
            {
                string name = MetricsRecords.LayoutColumns[i];
                Calibration c = state.Memory.Get(i, wave);
                row.Add("est_delay_" + name, estimates[i].AverageDelay)
                    .Add("est_load_" + name, estimates[i].WorstSaturation)
                    .Add("cor_delay_" + name, corrected[i].AverageDelay)
                    .Add("cor_load_" + name, corrected[i].WorstSaturation)
                    .Add("factor_" + name, c.Measured ? c.Factor : float.NaN)
                    .Add("samples_" + name, c.Samples)
                    .Add("backlog_" + name, c.Backlog);
                if (!JunctionAdvisor.IsAvailable(scrambled[i]))
                    continue;
                Calibration s = state.Memory.Get(i, wave, true);
                row.Add("est_delay_" + name + "_scramble", scrambled[i].AverageDelay)
                    .Add("cor_delay_" + name + "_scramble", scrambledCorrected[i].AverageDelay)
                    .Add("cor_load_" + name + "_scramble", scrambledCorrected[i].WorstSaturation)
                    .Add("factor_" + name + "_scramble", s.Measured ? s.Factor : float.NaN)
                    .Add("samples_" + name + "_scramble", s.Samples)
                    .Add("backlog_" + name + "_scramble", s.Backlog);
            }
            MetricsLog.Write(row);
        }

        private static string Remembered(LayoutMemory memory, int layout, bool wave, bool scramble)
        {
            if (layout < 0)
                return "unknown layout";
            Calibration c = memory.Get(layout, wave, scramble);
            return c.Measured ? $"measured x{c.Factor:0.00} over {c.Samples} periods, backlog {c.Backlog:0.00}" : "never measured";
        }

        /// <summary>Measurement periods need this many rounds and vehicles to say anything about a layout.</summary>
        private const int kMinPeriodRounds = 2;
        private const float kMinPeriodVehicles = 20f;

        /// <summary>
        /// Records the measurement period this review ends in the layout
        /// memory: the vehicles' measured mean wait against what the model
        /// expects of the layout for the period's traffic. A period that
        /// mixed running alone and in a wave, or flashed, is left out.
        /// </summary>
        /// <returns>Whether the period was recorded.</returns>
        /// <param name="scramble">The layout ran with a scramble; it is measured apart from the layout without.</param>
        private static bool Remember(ref AutopilotState state, JunctionModel model, ManagedJunction junction, PlanStrategy ran, int running, bool wave,
            bool scramble, float[] recent, float[] weights, JunctionRuntime period)
        {
            if (running < 0 || period.PeriodMixed || period.PeriodWave != wave
                || period.PeriodRounds < kMinPeriodRounds || period.PeriodVehicles < kMinPeriodVehicles)
                return false;
            DelayParameters p = DelayParameters.Default;
            p.TurnOnRed = (junction.Options & JunctionOptions.TurnOnRed) != 0;
            PlanEstimate e = DelayModel.Estimate(model, PhasePlanner.Build(model, ran, weights, scramble), recent, p);
            if (e.Vehicles <= 0f || e.VehicleDelay / e.Vehicles < LayoutMemory.MinModelled)
                return false;
            float measured = period.PeriodWait / period.PeriodVehicles;
            state.Memory.Record(running, wave, scramble, measured, e.VehicleDelay / e.Vehicles, period.PeriodBacklog);
            state.MeasuredWait = measured;
            state.ModelledWait = e.VehicleDelay / e.Vehicles;
            return true;
        }

        /// <summary>The day is split into this many windows, each with its own traffic figures.</summary>
        public const int kWindows = 8;

        /// <summary>The time-of-day window of an autopilot round, 3 game hours each.</summary>
        public static int TimeWindow(uint round)
        {
            uint roundsPerDay = (uint)(Game.Simulation.TimeSystem.kTicksPerDay / kRoundFrames);
            return (int)(round % roundsPerDay * kWindows / roundsPerDay);
        }

        /// <summary>
        /// Mean vehicles waiting per lane over the last round, on the
        /// approach where it is highest: the approach's queue over its lanes,
        /// each approach lane counted once.
        /// </summary>
        private float WorstQueuePerLane(Entity node)
        {
            DynamicBuffer<JunctionMovement> movements = EntityManager.GetBuffer<JunctionMovement>(node, true);
            DynamicBuffer<MovementStatistics> statistics = EntityManager.GetBuffer<MovementStatistics>(node, true);
            DynamicBuffer<JunctionLane> lanes = EntityManager.GetBuffer<JunctionLane>(node, true);
            var queue = new Dictionary<Entity, float>();
            for (int m = 0; m < movements.Length && m < statistics.Length; m++)
            {
                if (movements[m].Kind == MovementKind.Pedestrian)
                    continue;
                queue.TryGetValue(movements[m].Source, out float sum);
                queue[movements[m].Source] = sum + statistics[m].LastQueue;
            }
            var laneCount = new Dictionary<Entity, int>();
            var seen = new HashSet<Entity>();
            for (int l = 0; l < lanes.Length; l++)
            {
                JunctionLane lane = lanes[l];
                if (lane.Approach == Entity.Null || lane.Movement >= movements.Length || !seen.Add(lane.Approach))
                    continue;
                Entity source = movements[lane.Movement].Source;
                laneCount.TryGetValue(source, out int count);
                laneCount[source] = count + 1;
            }
            float worst = 0f;
            foreach (KeyValuePair<Entity, float> approach in queue)
            {
                laneCount.TryGetValue(approach.Key, out int count);
                worst = math.max(worst, approach.Value / math.max(1, count));
            }
            return worst;
        }

        /// <summary>
        /// Mean vehicles waiting per lane over a round, on one approach, from
        /// which the round counts as ending with a backlog for the layout
        /// memory. A signal makes queues at red; this is about twice the
        /// junction queue the problem list starts at, a queue that does not
        /// clear in a cycle.
        /// </summary>
        private const float kBacklogQueue = 6f;

        /// <summary>Adds the round just measured to the runtime's measurement period.</summary>
        private static void AddToPeriod(ref JunctionRuntime runtime, DynamicBuffer<MovementCounter> counters, ControlMode mode, float worstQueue)
        {
            bool wave = mode == ControlMode.Coordinated;
            if (runtime.PeriodRounds == 0)
                runtime.PeriodWave = wave;
            else if (runtime.PeriodWave != wave)
                runtime.PeriodMixed = true;
            if (mode == ControlMode.Flashing)
                runtime.PeriodMixed = true;
            for (int i = 0; i < counters.Length; i++)
            {
                // Crosswalks have a length; their counters are people.
                if (counters[i].Length > 0f)
                    continue;
                runtime.PeriodWait += counters[i].QueueSteps * SimTime.SecondsPerStep;
                runtime.PeriodVehicles += counters[i].Vehicles;
            }
            if (runtime.PeriodRounds < byte.MaxValue)
                runtime.PeriodRounds++;
            runtime.PeriodBacklog |= worstQueue >= kBacklogQueue;
        }

        private static void ClearPeriod(ref JunctionRuntime runtime)
        {
            runtime.PeriodWait = 0f;
            runtime.PeriodVehicles = 0f;
            runtime.PeriodRounds = 0;
            runtime.PeriodBacklog = false;
            runtime.PeriodWave = false;
            runtime.PeriodMixed = false;
        }

        /// <summary>
        /// The main road is the pair of opposite approaches with the most
        /// traffic; the rest are side roads.
        /// </summary>
        private static void Road(float[] byApproach, int[] opposite, out int majorApproach, out float major, out float minor, out float minorTotal)
        {
            majorApproach = -1;
            major = 0f;
            for (int a = 0; a < byApproach.Length; a++)
            {
                int b = opposite[a];
                if (b < 0)
                    continue;
                float sum = byApproach[a] + byApproach[b];
                if (majorApproach < 0 || sum > major)
                {
                    major = sum;
                    majorApproach = a;
                }
            }
            minor = 0f;
            minorTotal = 0f;
            int other = majorApproach >= 0 ? opposite[majorApproach] : -1;
            for (int a = 0; a < byApproach.Length; a++)
            {
                if (a == majorApproach || a == other)
                    continue;
                minor = math.max(minor, byApproach[a]);
                minorTotal += byApproach[a];
            }
        }
    }
}
