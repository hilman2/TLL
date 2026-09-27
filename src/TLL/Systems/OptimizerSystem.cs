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
    public class OptimizerSystem : TllSystemBase
    {
        /// <summary>Statistics must cover at least this long before they are used.</summary>
        private static readonly int kMinimumSample = SimTime.ToSteps(120f);

        /// <summary>Longest green the adaptive and actuated modes may give one phase.</summary>
        private static readonly ushort kMaxGreenCap = (ushort)SimTime.ToSteps(90f);

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
                // The demand-driven modes use the split as the typical green
                // and allow half as much again before forcing a change.
                int max = result.Green[i] + result.Green[i] / 2;
                phase.Data.MaxGreen = (ushort)Math.Max(phase.Data.MinGreen + 1, Math.Min(max, kMaxGreenCap));
            }
        }

        private static void ResetStatistics(DynamicBuffer<JunctionPhase> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer.ElementAt(i).Data.Stats.Clear();
        }
    }
}
