using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CompositionFlags = Game.Prefabs.CompositionFlags;

namespace TLL.UI
{
    /// <summary>What regulates a junction. Numbers are shared with the panel.</summary>
    public enum JunctionType
    {
        /// <summary>No lights, no stop signs: the game's rules or TLL's priority signs.</summary>
        RightOfWay = 0,
        AllWayStop = 1,
        TrafficLights = 2,
        Roundabout = 3,
    }

    /// <summary>
    /// Changes a junction's type from the TLL panel with the game's own
    /// tools: the traffic lights and stop signs upgrades of the road menu,
    /// and its roundabouts. The panel picks the tool with the right prefab,
    /// the player clicks the junction, and the game does the rest as from its
    /// road menu: it shows the price, checks the placement and charges it.
    /// </summary>
    /// <remarks>
    /// The lights and the stop signs are upgrade flags on the node
    /// (Game.Net.Upgraded, CompositionFlags.General.TrafficLights,
    /// RemoveTrafficLights, AllWayStop). Writing them directly would skip the
    /// price, which the game works out from the change of the roads'
    /// compositions (Game.Net.CostSystem); the tool keeps it right. Applied
    /// again to a junction that has it, an upgrade tool takes it away.
    /// </remarks>
    internal sealed class JunctionTypes
    {
        public struct Roundabout
        {
            public Entity Prefab;

            /// <summary>Diameter in metres, from the prefab's geometry.</summary>
            public float Size;
            public uint Cost;
        }

        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_Prefabs;
        private readonly ToolSystem m_Tools;
        private bool m_Found;

        public Entity Lights { get; private set; }
        public Entity Stop { get; private set; }

        /// <summary>The road upgrade that adds or takes away the crosswalk where a road meets a junction.</summary>
        public Entity Crosswalk { get; private set; }
        public readonly List<Roundabout> Roundabouts = new List<Roundabout>();

        public JunctionTypes(EntityManager entityManager, PrefabSystem prefabs, ToolSystem tools)
        {
            m_EntityManager = entityManager;
            m_Prefabs = prefabs;
            m_Tools = tools;
        }

        /// <summary>
        /// Looks the prefabs up once: the node upgrades that set traffic
        /// lights and stop signs, and the objects that make a node a
        /// roundabout, smallest first.
        /// </summary>
        public void Find()
        {
            if (m_Found)
                return;
            m_Found = true;
            EntityQuery upgrades = m_EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PlaceableNetData>(), ComponentType.ReadOnly<PrefabData>());
            using (NativeArray<Entity> entities = upgrades.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    PlaceableNetData data = m_EntityManager.GetComponentData<PlaceableNetData>(e);
                    CompositionFlags.Side crosswalk = CompositionFlags.Side.AddCrosswalk | CompositionFlags.Side.RemoveCrosswalk;
                    if (Crosswalk == Entity.Null && (data.m_PlacementFlags & Game.Net.PlacementFlags.IsUpgrade) != 0
                        && ((data.m_SetUpgradeFlags.m_Left | data.m_SetUpgradeFlags.m_Right) & crosswalk) != 0)
                        Crosswalk = e;
                    if ((data.m_PlacementFlags & Game.Net.PlacementFlags.NodeUpgrade) == 0)
                        continue;
                    if (Lights == Entity.Null && (data.m_SetUpgradeFlags.m_General & CompositionFlags.General.TrafficLights) != 0)
                        Lights = e;
                    else if (Stop == Entity.Null && (data.m_SetUpgradeFlags.m_General & CompositionFlags.General.AllWayStop) != 0)
                        Stop = e;
                }
            }
            EntityQuery objects = m_EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetObjectData>(), ComponentType.ReadOnly<PrefabData>());
            using (NativeArray<Entity> entities = objects.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity e in entities)
                {
                    if ((m_EntityManager.GetComponentData<NetObjectData>(e).m_CompositionFlags.m_General & CompositionFlags.General.Roundabout) == 0)
                        continue;
                    float size = m_EntityManager.HasComponent<ObjectGeometryData>(e) ? m_EntityManager.GetComponentData<ObjectGeometryData>(e).m_Size.x : 0f;
                    uint cost = m_EntityManager.HasComponent<PlaceableObjectData>(e) ? m_EntityManager.GetComponentData<PlaceableObjectData>(e).m_ConstructionCost : 0u;
                    Roundabouts.Add(new Roundabout { Prefab = e, Size = size, Cost = cost });
                }
            }
            Roundabouts.Sort((a, b) => a.Size.CompareTo(b.Size));
            Mod.Log.Info($"Junction types: lights {(Lights != Entity.Null ? m_Prefabs.GetPrefabName(Lights) : "none")}, "
                + $"stop signs {(Stop != Entity.Null ? m_Prefabs.GetPrefabName(Stop) : "none")}, "
                + $"crosswalks {(Crosswalk != Entity.Null ? m_Prefabs.GetPrefabName(Crosswalk) : "none")}, {Roundabouts.Count} roundabouts.");
        }

        /// <summary>The price the road menu shows for an upgrade; 0 where there is none.</summary>
        public uint Cost(Entity upgrade)
        {
            return upgrade != Entity.Null && m_EntityManager.HasComponent<PlaceableNetData>(upgrade)
                ? m_EntityManager.GetComponentData<PlaceableNetData>(upgrade).m_DefaultConstructionCost
                : 0u;
        }

        /// <summary>What regulates the node now.</summary>
        public JunctionType TypeOf(Entity node, bool roundabout)
        {
            if (roundabout)
                return JunctionType.Roundabout;
            if (m_EntityManager.HasComponent<TrafficLights>(node))
                return JunctionType.TrafficLights;
            if (m_EntityManager.HasComponent<Upgraded>(node)
                && (m_EntityManager.GetComponentData<Upgraded>(node).m_Flags.m_General & CompositionFlags.General.AllWayStop) != 0)
                return JunctionType.AllWayStop;
            return JunctionType.RightOfWay;
        }

        /// <summary>
        /// The tool that turns a junction of type <paramref name="now"/> into
        /// <paramref name="wanted"/>, or Null where the panel cannot offer
        /// one: a roundabout is taken away with the bulldozer.
        /// </summary>
        public Entity ToolFor(JunctionType now, JunctionType wanted, int roundabout)
        {
            if (now == wanted || now == JunctionType.Roundabout)
                return Entity.Null;
            switch (wanted)
            {
                case JunctionType.TrafficLights:
                    return Lights;
                case JunctionType.AllWayStop:
                    return Stop;
                case JunctionType.RightOfWay:
                    // The upgrade the junction has, applied again, takes it away.
                    return now == JunctionType.TrafficLights ? Lights : Stop;
                case JunctionType.Roundabout:
                    return roundabout >= 0 && roundabout < Roundabouts.Count ? Roundabouts[roundabout].Prefab : Entity.Null;
                default:
                    return Entity.Null;
            }
        }

        /// <summary>Whether the game's tool is open with <paramref name="prefab"/> selected.</summary>
        public bool IsActive(Entity prefab)
        {
            PrefabBase active = m_Tools.activePrefab;
            return prefab != Entity.Null && active != null && m_Prefabs.GetEntity(active) == prefab;
        }

        /// <summary>Opens the game's tool with <paramref name="prefab"/> selected. Returns whether it opened.</summary>
        public bool Activate(Entity prefab)
        {
            if (prefab == Entity.Null || !m_Prefabs.TryGetPrefab(prefab, out PrefabBase asset))
                return false;
            m_Tools.ActivatePrefabTool(asset);
            return true;
        }
    }
}
