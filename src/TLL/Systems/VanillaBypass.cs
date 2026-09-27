using System.Reflection;
using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using TLL.Components;
using Unity.Entities;

namespace TLL.Systems
{
    /// <summary>
    /// Makes the game's traffic light system leave managed junctions alone.
    ///
    /// The game's TrafficLightSystem keeps its query in the private field
    /// m_TrafficLightQuery and reads it on every update. Replacing that query
    /// with one that excludes <see cref="ManagedJunction"/> is the whole
    /// intervention: every other junction still runs the game's own code,
    /// unchanged, whatever a game update does to it. Only the field name and
    /// the query's shape have to stay the same, and both are checked here.
    /// </summary>
    internal static class VanillaBypass
    {
        private const string kQueryField = "m_TrafficLightQuery";

        /// <returns>True if the game's system now skips managed junctions.</returns>
        public static bool Apply(World world)
        {
            TrafficLightSystem system = world.GetOrCreateSystemManaged<TrafficLightSystem>();
            FieldInfo field = typeof(TrafficLightSystem).GetField(kQueryField, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(EntityQuery))
            {
                Mod.Log.Error($"TrafficLightSystem.{kQueryField} not found. The game has changed; TLL leaves all traffic lights to the game.");
                return false;
            }

            EntityQuery original = (EntityQuery)field.GetValue(system);
            ComponentType[] expected =
            {
                ComponentType.ReadWrite<TrafficLights>(),
                ComponentType.ReadOnly<UpdateFrame>(),
            };
            foreach (ComponentType type in expected)
            {
                if (!ContainsType(original, type))
                {
                    Mod.Log.Error($"TrafficLightSystem's query no longer contains {type}. The game has changed; TLL leaves all traffic lights to the game.");
                    return false;
                }
            }

            EntityQuery replacement = system.EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<TrafficLights>(), ComponentType.ReadOnly<UpdateFrame>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Destroyed>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<ManagedJunction>(),
                },
            });
            field.SetValue(system, replacement);
            Mod.Log.Info("The game's traffic light system now skips junctions managed by TLL.");
            return true;
        }

        private static bool ContainsType(EntityQuery query, ComponentType type)
        {
            foreach (EntityQueryDesc desc in query.GetEntityQueryDescs())
            {
                foreach (ComponentType t in desc.All)
                {
                    if (t.TypeIndex == type.TypeIndex)
                        return true;
                }
            }
            return false;
        }
    }
}
