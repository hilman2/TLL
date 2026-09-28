using System;
using Game;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using TLL.Components;
using TLL.Core;
using TLL.Core.Control;
using TLL.Core.Optimization;
using Unity.Collections;
using Unity.Entities;

namespace TLL.Systems
{
    /// <summary>
    /// Adjusts cycle and greens of junctions with automatic timing, from the
    /// statistics their controllers collected since the last round.
    ///
    /// Runs every 4096 simulation frames, about once per simulated minute.
    /// Rounds with too little data are skipped, so a junction is only
    /// retimed from a meaningful sample.
    /// </summary>
    public partial class OptimizerSystem : TllSystemBase
    {
        /// <summary>Statistics must cover at least this long before they are used.</summary>
        private static readonly int kMinimumSample = SimTime.ToSteps(120f);

        private SimulationSystem m_Simulation;
        private EntityQuery m_Query;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 4096;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ManagedJunction>(),
                    ComponentType.ReadWrite<JunctionRuntime>(),
                    ComponentType.ReadWrite<JunctionPhase>(),
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
            OptimizerLimits limits = OptimizerLimits.Default;
            using (NativeArray<Entity> nodes = m_Query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
                    JunctionRuntime runtime = EntityManager.GetComponentData<JunctionRuntime>(node);
                    DynamicBuffer<JunctionPhase> buffer = EntityManager.GetBuffer<JunctionPhase>(node);

                    if (runtime.StatsSince == 0 || now < runtime.StatsSince)
                    {
                        // First round after a (re)build or a load: start the sample now.
                        ResetStatistics(buffer);
                        runtime.StatsSince = now;
                        EntityManager.SetComponentData(node, runtime);
                        continue;
                    }
                    int elapsed = (int)Math.Min(int.MaxValue, now - runtime.StatsSince);
                    if (elapsed < kMinimumSample)
                        continue;

                    Measure(buffer, junction, elapsed, limits, ref runtime);
                    // Coordinated junctions are timed by their green wave
                    // (CoordinationSystem), which uses what Measure recorded.
                    bool retime = (junction.Options & JunctionOptions.AutoTiming) != 0
                        && junction.Mode != ControlMode.Flashing
                        && junction.Mode != ControlMode.Coordinated;
                    if (retime)
                        Retime(buffer, junction, elapsed, limits);

                    ResetStatistics(buffer);
                    runtime.StatsSince = now;
                    EntityManager.SetComponentData(node, runtime);
                }
            }
        }

        /// <summary>Records flow ratios and the cycle the junction would like, for the green waves.</summary>
        private static void Measure(DynamicBuffer<JunctionPhase> buffer, ManagedJunction junction, int elapsed, OptimizerLimits limits, ref JunctionRuntime runtime)
        {
            var phases = new PhaseData[buffer.Length];
            for (int i = 0; i < phases.Length; i++)
                phases[i] = buffer[i].Data;
            float[] ratios = SplitOptimizer.FlowRatios(phases, elapsed);
            for (int i = 0; i < ratios.Length; i++)
                buffer.ElementAt(i).FlowRatio = ratios[i];
            int intergreen = junction.Yellow + junction.AllRed + junction.Prepare;
            runtime.DesiredCycle = SplitOptimizer.OptimalCycle(ratios, intergreen, limits);
        }

        private static void Retime(DynamicBuffer<JunctionPhase> buffer, ManagedJunction junction, int elapsed, OptimizerLimits limits)
        {
            var phases = new PhaseData[buffer.Length];
            for (int i = 0; i < phases.Length; i++)
                phases[i] = buffer[i].Data;
            int intergreen = junction.Yellow + junction.AllRed + junction.Prepare;

            SplitResult result = SplitOptimizer.Optimize(phases, elapsed, intergreen, limits);

            for (int i = 0; i < phases.Length; i++)
            {
                ref JunctionPhase phase = ref buffer.ElementAt(i);
                phase.Data.Green = result.Green[i];
                // The demand-driven modes end a green when its queue has left;
                // the maximum only caps it, and follows how often the greens
                // were cut off there (MaxGreenTuner), not the split.
                phase.Data.MaxGreen = (ushort)MaxGreenTuner.Next(in phase.Data);
            }
        }

        private static void ResetStatistics(DynamicBuffer<JunctionPhase> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer.ElementAt(i).Data.Stats.Clear();
        }
    }
}
