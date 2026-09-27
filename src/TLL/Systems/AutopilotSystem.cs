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
    /// A round runs every 4096 simulation frames, about one game hour. Each
    /// round turns the counters of the control job into rates and updates the
    /// recent, daily and peak figures. The layout is reviewed every 16 rounds
    /// against the peak figures, the traffic the junction must cope with, and
    /// changes only when two reviews in a row agree.
    /// </summary>
    public partial class AutopilotSystem : TllSystemBase
    {
        private const int kLayoutEvery = 16;
        private const int kConfirmRounds = 2;
        private const int kMinFlashRounds = 2;

        /// <summary>Share of the new round in the recent figure: about the last three rounds count.</summary>
        private const float kRecentWeight = 0.3f;

        /// <summary>Share of the new round in the daily figure: about one game day (64 rounds) counts.</summary>
        private const float kDailyWeight = 0.02f;

        /// <summary>Per round the peak falls back by this factor, so an old peak fades over about a day.</summary>
        private const float kPeakDecay = 0.985f;

        /// <summary>A layout review needs at least this much traffic, in vehicles per hour, to mean anything.</summary>
        private const float kMinimumVolume = 30f;

        private SimulationSystem m_Simulation;
        private CityConfigurationSystem m_CityConfiguration;
        private EntityQuery m_Query;
        private long m_LastStep;
        private int m_Round;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 4096;
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
            bool first = m_LastStep == 0 || now <= m_LastStep;
            long elapsedSteps = now - m_LastStep;
            m_LastStep = now;
            m_Round++;
            bool layoutRound = m_Round % kLayoutEvery == 0;
            Setting settings = Mod.Settings;

            using (NativeArray<Entity> nodes = m_Query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    DynamicBuffer<MovementCounter> counters = EntityManager.GetBuffer<MovementCounter>(node);
                    if (!first)
                        Measure(node, counters, elapsedSteps);
                    for (int i = 0; i < counters.Length; i++)
                    {
                        ref MovementCounter c = ref counters.ElementAt(i);
                        c.Vehicles = 0;
                        c.PedestrianSteps = 0f;
                        c.QueueSteps = 0f;
                    }
                    if (first || settings == null || !settings.AutoManageAll)
                        continue;
                    ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
                    if (junction.Origin != JunctionOrigin.Auto)
                        continue;
                    try
                    {
                        Decide(node, junction, settings, layoutRound);
                    }
                    catch (Exception e)
                    {
                        // One junction the autopilot cannot judge keeps its
                        // current settings; the others go on.
                        Mod.Log.Error(e, $"Junction {node}: the autopilot could not decide, it keeps its settings.");
                    }
                }
            }
        }

        /// <summary>Turns the round's counts into rates and updates the running figures.</summary>
        private void Measure(Entity node, DynamicBuffer<MovementCounter> counters, long elapsedSteps)
        {
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
            }
        }

        private void Decide(Entity node, ManagedJunction junction, Setting settings, bool layoutRound)
        {
            AutopilotState state = EntityManager.HasComponent<AutopilotState>(node)
                ? EntityManager.GetComponentData<AutopilotState>(node)
                : new AutopilotState { RoundsSinceFlashChange = kMinFlashRounds };
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
            if (state.RoundsSinceFlashChange < ushort.MaxValue)
                state.RoundsSinceFlashChange++;
            bool wantFlash = settings.AutoFlash && majorApproach >= 0 && FlashAdvisor.Decide(flashing, major, minor, minorTotal);
            if (wantFlash != flashing && state.RoundsSinceFlashChange >= kMinFlashRounds)
            {
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
                state.RoundsSinceFlashChange = 0;
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

            if (layoutRound)
                ReviewLayout(node, ref junction, ref state, settings, statistics, peak, opposite, ref rebuild);

            if (changed || rebuild)
                EntityManager.SetComponentData(node, junction);
            if (rebuild)
            {
                // An empty plan makes the set-up generate one for the new
                // layout; the rebuild of the node brings the poles along.
                EntityManager.GetBuffer<JunctionPhase>(node).Clear();
                EntityManager.AddComponent<RebuildRequest>(node);
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

        private void ReviewLayout(Entity node, ref ManagedJunction junction, ref AutopilotState state, Setting settings,
            DynamicBuffer<MovementStatistics> statistics, float[] peakByApproach, int[] opposite, ref bool rebuild)
        {
            if (settings.AutoLayout != AutoLayout.Automatic)
            {
                PlanStrategy fixedChoice = settings.InitialStrategy();
                if (junction.Strategy != fixedChoice)
                {
                    junction.Strategy = fixedChoice;
                    rebuild = true;
                }
                return;
            }

            JunctionLayout layout = JunctionAnalysis.Analyse(EntityManager, node, m_CityConfiguration.leftHandTraffic);
            if (layout == null || layout.Keys.Count != statistics.Length)
                return;
            var volumes = new float[statistics.Length];
            float total = 0f;
            for (int m = 0; m < volumes.Length; m++)
            {
                volumes[m] = statistics[m].Peak;
                if (!layout.Model.Movements[m].IsPedestrian)
                    total += volumes[m];
            }
            if (total < kMinimumVolume)
                return;

            PlanEstimate[] estimates = JunctionAdvisor.EvaluateAll(layout.Model, volumes, DelayParameters.Default);
            state.HasEstimate = true;
            state.LayoutDelay = new float4(estimates[0].AverageDelay, estimates[1].AverageDelay, estimates[2].AverageDelay, estimates[3].AverageDelay);
            state.LayoutSaturation = new float4(estimates[0].WorstSaturation, estimates[1].WorstSaturation, estimates[2].WorstSaturation, estimates[3].WorstSaturation);

            PlanStrategy choice = JunctionAdvisor.Choose(junction.Strategy, estimates);
            PlanEstimate best = estimates[Array.IndexOf(JunctionAdvisor.Strategies, choice)];
            Road(peakByApproach, opposite, out _, out float major, out float minor, out _);
            state.SignalAdvice = SignalAdvisor.Decide(true, major, minor, best.AverageDelay);

            if (choice == junction.Strategy)
            {
                state.PendingRounds = 0;
                return;
            }
            if (state.Pending == choice)
            {
                state.PendingRounds++;
            }
            else
            {
                state.Pending = choice;
                state.PendingRounds = 1;
            }
            if (state.PendingRounds < kConfirmRounds)
                return;
            if (settings.VerboseLogging)
                Mod.Log.Info($"Autopilot: junction {node} changes from {junction.Strategy} to {choice}, expected mean delay {best.AverageDelay:0.0} s.");
            junction.Strategy = choice;
            state.PendingRounds = 0;
            rebuild = true;
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
