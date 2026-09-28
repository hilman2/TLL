using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using TLL.Components;
using Unity.Collections;
using Unity.Entities;

namespace TLL.Systems
{
    /// <summary>
    /// Carries out the city-wide automation and the maintenance actions of the
    /// options screen: takes over new signalled junctions while the
    /// automation is on, hands automatic junctions back when it is switched
    /// off, and serves the release and rebuild buttons.
    ///
    /// Runs every 256 simulation frames. Taking over a junction only adds
    /// components; the plan is built by <see cref="JunctionInitSystem"/> on
    /// the following frame.
    /// </summary>
    public partial class AutoManageSystem : TllSystemBase
    {
        private EntityQuery m_UnmanagedQuery;
        private EntityQuery m_ManagedQuery;
        private EntityQuery m_AllSignalsQuery;
        private EntityQuery m_SignalsRemovedQuery;
        private EntityQuery m_LaneRulesQuery;
        private SignalControlSystem m_Control;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 256;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Control = World.GetOrCreateSystemManaged<SignalControlSystem>();
            ComponentType[] skip =
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Temp>(),
            };
            m_UnmanagedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TrafficLights>() },
                None = new[]
                {
                    ComponentType.ReadOnly<ManagedJunction>(),
                    ComponentType.ReadOnly<JunctionExcluded>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            m_ManagedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ManagedJunction>() },
                None = skip,
            });
            m_AllSignalsQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TrafficLights>() },
                None = skip,
            });
            m_SignalsRemovedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ManagedJunction>() },
                None = new[]
                {
                    ComponentType.ReadOnly<TrafficLights>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            m_LaneRulesQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<TurnRule>(),
                    ComponentType.ReadOnly<PriorityRule>(),
                    ComponentType.ReadOnly<LaneConnectionRule>(),
                },
                None = skip,
            });
        }

        /// <summary>
        /// Takes every lane rule of TLL out of the city, the player's and the
        /// autopilot's, and has the game build the lanes of those junctions
        /// and their roads anew, as it would without TLL.
        /// </summary>
        private void ResetLanes()
        {
            int count = 0;
            using (NativeArray<Entity> nodes = m_LaneRulesQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    foreach (Entity edge in NetGeometry.ConnectedEdges(EntityManager, node))
                        EntityManager.AddComponent<RebuildRequest>(edge);
                    count++;
                }
                EntityManager.RemoveComponent<TurnRule>(nodes);
                EntityManager.RemoveComponent<PriorityRule>(nodes);
                EntityManager.RemoveComponent<LaneConnectionRule>(nodes);
                EntityManager.AddComponent<RebuildRequest>(nodes);
            }
            Mod.Log.Info($"Lane rules removed from {count} junction(s); the game builds their lanes anew.");
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // Buttons pressed in the main menu, or in a city left before the
            // simulation got to them, are not meant for the city loaded now.
            Requests.ReleaseAll = false;
            Requests.RebuildVanilla = false;
            Requests.ResetAllToAutomatic = false;
            Requests.RebuildGreenWaves = false;
            Requests.ResetLanes = false;
            // Every city loaded is a session of its own in the metrics log.
            Metrics.MetricsLog.NewSession();
        }

        protected override void OnSafeUpdate()
        {
            Setting settings = Mod.Settings;
            if (settings == null || !m_Control.Available)
                return;

            // The player removed the signals (the game takes TrafficLights
            // off the node): nothing is left to control, and stale data would
            // come back if signals are added again later.
            if (!m_SignalsRemovedQuery.IsEmptyIgnoreFilter)
            {
                using (NativeArray<Entity> nodes = m_SignalsRemovedQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity node in nodes)
                        JunctionInitSystem.Release(EntityManager, node, exclude: false);
                    Mod.Log.Info($"Released {nodes.Length} junction(s) whose signals were removed.");
                }
            }

            if (Requests.ReleaseAll)
            {
                Requests.ReleaseAll = false;
                settings.AutoManageAll = false;
                ReleaseWhere(_ => true);
            }
            if (Requests.RebuildVanilla)
            {
                Requests.RebuildVanilla = false;
                RebuildVanilla();
            }
            if (Requests.ResetAllToAutomatic)
            {
                Requests.ResetAllToAutomatic = false;
                settings.AutoManageAll = true;
                settings.ApplyAndSave();
                ResetAllToAutomatic(settings);
            }
            if (Requests.ResetLanes)
            {
                Requests.ResetLanes = false;
                // Otherwise the autopilot would put its rules back.
                settings.AutoTurnBans = false;
                settings.AutoPrioritySigns = false;
                settings.AutoLaneArrows = false;
                settings.ApplyAndSave();
                ResetLanes();
            }

            if (settings.AutoManageAll)
                TakeOverNew(settings);
            else
                ReleaseWhere(j => j.Origin == JunctionOrigin.Auto);
        }

        private void TakeOverNew(Setting settings)
        {
            if (m_UnmanagedQuery.IsEmptyIgnoreFilter)
                return;
            using (NativeArray<Entity> nodes = m_UnmanagedQuery.ToEntityArray(Allocator.Temp))
            {
                // One structural change for all nodes, then plain data writes.
                EntityManager.AddComponent<ManagedJunction>(m_UnmanagedQuery);
                ManagedJunction auto = ManagedJunction.Create(JunctionOrigin.Auto, settings.AutoControl(), settings.InitialStrategy());
                foreach (Entity node in nodes)
                    EntityManager.SetComponentData(node, auto);
                // A full rebuild, not just JunctionDirty: the signal poles get
                // their heads from the lane groups only when the node is updated.
                EntityManager.AddComponent<RebuildRequest>(nodes);
                Mod.Log.Info($"Automation took over {nodes.Length} junction(s).");
            }
        }

        private delegate bool JunctionFilter(ManagedJunction junction);

        private void ReleaseWhere(JunctionFilter filter)
        {
            if (m_ManagedQuery.IsEmptyIgnoreFilter)
                return;
            int released = 0;
            using (NativeArray<Entity> nodes = m_ManagedQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    if (!filter(EntityManager.GetComponentData<ManagedJunction>(node)))
                        continue;
                    JunctionInitSystem.Release(EntityManager, node, exclude: false);
                    released++;
                }
            }
            if (released > 0)
                Mod.Log.Info($"Returned {released} junction(s) to the game.");
        }

        /// <summary>
        /// Puts every junction back under the automation: the ones set by
        /// the player get the automatic settings and a generated plan, and the
        /// ones handed back to the game are taken over again. The traffic
        /// measured so far stays, so the autopilot decides from it at once.
        /// </summary>
        private void ResetAllToAutomatic(Setting settings)
        {
            int reset = 0;
            using (NativeArray<Entity> nodes = m_AllSignalsQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    // Excluded junctions are taken over by TakeOverNew right
                    // after; one the planner cannot handle is excluded again.
                    if (EntityManager.HasComponent<JunctionExcluded>(node))
                        EntityManager.RemoveComponent<JunctionExcluded>(node);
                    if (!EntityManager.HasComponent<ManagedJunction>(node))
                        continue;
                    if (EntityManager.GetComponentData<ManagedJunction>(node).Origin == JunctionOrigin.Auto)
                        continue;
                    EntityManager.SetComponentData(node, ManagedJunction.Create(JunctionOrigin.Auto, settings.AutoControl(), settings.InitialStrategy()));
                    if (EntityManager.HasBuffer<JunctionPhase>(node))
                        EntityManager.GetBuffer<JunctionPhase>(node).Clear();
                    EntityManager.RemoveComponent<AutopilotState>(node);
                    EntityManager.AddComponent<RebuildRequest>(node);
                    reset++;
                }
            }
            Requests.RebuildGreenWaves = true;
            Mod.Log.Info($"Reset {reset} junction(s) set by the player to automatic.");
        }

        /// <summary>
        /// Tags every signalled junction TLL does not manage as updated, so
        /// the game rebuilds its lanes and signal groups from scratch. This
        /// clears what other traffic light mods left in the save.
        /// </summary>
        private void RebuildVanilla()
        {
            int count = 0;
            using (NativeArray<Entity> nodes = m_AllSignalsQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    if (EntityManager.HasComponent<ManagedJunction>(node))
                        continue;
                    EntityManager.AddComponent<RebuildRequest>(node);
                    count++;
                }
            }
            Mod.Log.Info($"Asked the game to rebuild the signals of {count} junction(s).");
        }
    }
}
