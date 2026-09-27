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
    public class AutoManageSystem : TllSystemBase
    {
        private EntityQuery m_UnmanagedQuery;
        private EntityQuery m_ManagedQuery;
        private EntityQuery m_AllSignalsQuery;
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
        }

        protected override void OnSafeUpdate()
        {
            Setting settings = Mod.Settings;
            if (settings == null || !m_Control.Available)
                return;

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
                ManagedJunction auto = ManagedJunction.Create(JunctionOrigin.Auto, settings.AutoControl, settings.AutoStrategy);
                foreach (Entity node in nodes)
                    EntityManager.SetComponentData(node, auto);
                EntityManager.AddComponent<JunctionDirty>(nodes);
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
