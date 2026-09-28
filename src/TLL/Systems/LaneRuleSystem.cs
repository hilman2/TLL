using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Tools;
using TLL.Components;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using NetLaneArchetypeData = Game.Prefabs.NetLaneArchetypeData;
using NetLaneData = Game.Prefabs.NetLaneData;
using PrefabRef = Game.Prefabs.PrefabRef;

namespace TLL.Systems
{
    /// <summary>
    /// Applies the rules TLL keeps for a junction to its lanes, every time
    /// the game has built them anew: forbidden turns (<see cref="TurnRule"/>),
    /// signs (<see cref="PriorityRule"/>), the player's lane connections
    /// (<see cref="LaneConnectionRule"/>), and on the roads before a
    /// junction its solid lines (<see cref="LaneChangeBan"/>, SolidLines).
    ///
    /// The game builds a node's lanes in Modification4 whenever the node is
    /// Updated, and writes each lane's flags afresh, so a rule set once would
    /// be lost with the next change to the junction. The new lanes exist from
    /// the end of Modification4. Everything that reads them runs later: the
    /// lane index of the node (LaneReferencesSystem), who gives way to whom
    /// and the flags of the lanes leading in (LaneOverlapSystem), the signs
    /// (SecondaryObjectSystem), TLL's junction set-up, and at the end of the
    /// frame the route search (LanesModifiedSystem) and the vehicles on
    /// lanes that went (FixLaneObjectsSystem). This system runs first in
    /// Modification4B, so all of them see the rules. To change the rules of a
    /// junction, the node is rebuilt (RebuildRequest): going back to the
    /// game's own lanes is then just not changing them.
    /// </summary>
    public partial class LaneRuleSystem : TllSystemBase
    {
        /// <summary>The flags the game gives the turns its own road upgrades forbid.</summary>
        public const CarLaneFlags ForbiddenFlags = CarLaneFlags.Unsafe | CarLaneFlags.Forbidden;

        /// <summary>The flags that decide who gives way at a junction without signals (LaneOverlapSystem).</summary>
        public const CarLaneFlags PriorityFlags = CarLaneFlags.Yield | CarLaneFlags.Stop | CarLaneFlags.RightOfWay;

        /// <summary>
        /// First index of the middle path nodes of added lanes. The game
        /// numbers its junction lanes from 0 up, 256 per group of lanes
        /// leading in; this stays clear of that.
        /// </summary>
        private const ushort kAddedLaneIndex = 0xE000;

        private EntityQuery m_LaneQuery;
        private EntityQuery m_NodeQuery;
        private EntityQuery m_SolidQuery;
        private Game.City.CityConfigurationSystem m_CityConfiguration;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_LaneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<CarLane>(),
                    ComponentType.ReadOnly<Lane>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_NodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<LaneConnectionRule>(), ComponentType.ReadOnly<Updated>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_SolidQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<SolidLineRule>(), ComponentType.ReadOnly<Updated>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_CityConfiguration = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
        }

        protected override void OnSafeUpdate()
        {
            // The lanes the game built this frame, by junction. The junction's
            // buffer of lanes does not list the new ones yet. Lanes of road
            // pieces inside solid lines are collected by their road.
            var byNode = new Dictionary<Entity, List<Entity>>();
            var byEdge = new Dictionary<Entity, List<Entity>>();
            if (!m_LaneQuery.IsEmptyIgnoreFilter)
            {
                using (NativeArray<Entity> lanes = m_LaneQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity lane in lanes)
                    {
                        Entity owner = EntityManager.GetComponentData<Owner>(lane).m_Owner;
                        if (EntityManager.HasBuffer<LaneChangeBan>(owner))
                        {
                            if (!byEdge.TryGetValue(owner, out List<Entity> onEdge))
                                byEdge[owner] = onEdge = new List<Entity>();
                            onEdge.Add(lane);
                            continue;
                        }
                        if (!EntityManager.HasBuffer<TurnRule>(owner) && !EntityManager.HasBuffer<PriorityRule>(owner)
                            && !EntityManager.HasBuffer<LaneConnectionRule>(owner))
                            continue;
                        if (!byNode.TryGetValue(owner, out List<Entity> list))
                            byNode[owner] = list = new List<Entity>();
                        list.Add(lane);
                    }
                }
            }
            foreach (KeyValuePair<Entity, List<Entity>> entry in byNode)
                ApplyToLanes(entry.Key, entry.Value);
            foreach (KeyValuePair<Entity, List<Entity>> entry in byEdge)
                SolidLines.ApplyToLanes(EntityManager, entry.Key, entry.Value);

            // A junction with solid lines that was rebuilt, e.g. with a road
            // replaced: the new road pieces get the bans.
            if (!m_SolidQuery.IsEmptyIgnoreFilter)
            {
                using (NativeArray<Entity> nodes = m_SolidQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity node in nodes)
                        SolidLines.Sync(EntityManager, node);
                }
            }

            if (m_NodeQuery.IsEmptyIgnoreFilter)
                return;
            using (NativeArray<Entity> nodes = m_NodeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in nodes)
                {
                    byNode.TryGetValue(node, out List<Entity> lanes);
                    AddConnections(node, lanes ?? new List<Entity>());
                }
            }
        }

        /// <summary>Flags, and takes away, the junction's new lanes by its rules.</summary>
        private void ApplyToLanes(Entity node, List<Entity> lanes)
        {
            bool turns = EntityManager.HasBuffer<TurnRule>(node);
            // Signs mean nothing where signals decide.
            bool signs = EntityManager.HasBuffer<PriorityRule>(node) && !EntityManager.HasComponent<TrafficLights>(node);
            bool links = EntityManager.HasBuffer<LaneConnectionRule>(node);
            var removed = new List<Entity>();
            foreach (Entity lane in lanes)
            {
                // Tram tracks follow their lines, not the route search; a
                // lane shared with a tram is left as it is.
                if (EntityManager.HasComponent<TrackLane>(lane))
                    continue;
                Lane path = EntityManager.GetComponentData<Lane>(lane);
                if (links && !EntityManager.HasComponent<MasterLane>(lane) && Removes(EntityManager.GetBuffer<LaneConnectionRule>(node, true), path))
                {
                    removed.Add(lane);
                    continue;
                }
                CarLane car = EntityManager.GetComponentData<CarLane>(lane);
                CarLaneFlags flags = Flagged(node, path, car.m_Flags, turns, signs);
                if (flags != car.m_Flags)
                {
                    car.m_Flags = flags;
                    EntityManager.SetComponentData(lane, car);
                }
            }
            if (removed.Count == 0)
                return;
            // The route search sees a group of parallel lanes only through
            // its master lane. Once all lanes of a group are gone, so goes
            // the master, or routes would lead where no lane is.
            foreach (Entity lane in lanes)
            {
                if (!EntityManager.HasComponent<MasterLane>(lane))
                    continue;
                uint group = EntityManager.GetComponentData<MasterLane>(lane).m_Group;
                bool left = false;
                foreach (Entity other in lanes)
                {
                    if (!removed.Contains(other) && EntityManager.HasComponent<SlaveLane>(other)
                        && EntityManager.GetComponentData<SlaveLane>(other).m_Group == group)
                        left = true;
                }
                if (!left)
                    removed.Add(lane);
            }
            foreach (Entity lane in removed)
                EntityManager.AddComponent<Deleted>(lane);
        }

        /// <summary>A lane's flags with the junction's forbidden turns and signs.</summary>
        private CarLaneFlags Flagged(Entity node, Lane path, CarLaneFlags flags, bool turns, bool signs)
        {
            if (turns && Forbids(EntityManager.GetBuffer<TurnRule>(node, true), path))
                flags |= ForbiddenFlags;
            if (signs)
                flags = WithSign(flags, SignFor(EntityManager.GetBuffer<PriorityRule>(node, true), path));
            return flags;
        }

        /// <summary>
        /// Builds the connections the player added. The game has just built
        /// the junction's lanes without them, and taken away any built before.
        /// </summary>
        private void AddConnections(Entity node, List<Entity> built)
        {
            List<Entity> edges = NetGeometry.ConnectedEdges(EntityManager, node);
            // A road replaced or removed takes its lanes with it; so go
            // the rules for them.
            DynamicBuffer<LaneConnectionRule> stored = EntityManager.GetBuffer<LaneConnectionRule>(node);
            for (int i = stored.Length - 1; i >= 0; i--)
            {
                if (!edges.Contains(stored[i].FromEdge) || !edges.Contains(stored[i].ToEdge))
                    stored.RemoveAt(i);
            }
            DynamicBuffer<LaneConnectionRule> rules = EntityManager.GetBuffer<LaneConnectionRule>(node, true);
            var wanted = new List<LaneConnectionRule>();
            for (int i = 0; i < rules.Length; i++)
            {
                if (rules[i].Change == LaneConnectionChange.Added)
                    wanted.Add(rules[i]);
            }
            if (wanted.Count == 0)
                return;
            List<LaneEnd> ends = LaneEnds.Collect(EntityManager, node, edges);
            bool signals = EntityManager.HasComponent<TrafficLights>(node);
            bool turns = EntityManager.HasBuffer<TurnRule>(node);
            bool signs = EntityManager.HasBuffer<PriorityRule>(node) && !signals;
            for (int i = 0; i < wanted.Count; i++)
            {
                int from = LaneEnds.Find(ends, wanted[i].FromEdge, wanted[i].FromLane, incoming: true);
                int to = LaneEnds.Find(ends, wanted[i].ToEdge, wanted[i].ToLane, incoming: false);
                // A road rebuilt since, with other lanes: the rule waits
                // until the player looks at the junction again.
                if (from < 0 || to < 0 || Connects(built, ends[from].Node, ends[to].Node))
                    continue;
                Entity lane = Create(node, ends[from], ends[to], (ushort)(kAddedLaneIndex + i), built, signals);
                if (lane == Entity.Null)
                    continue;
                CarLane car = EntityManager.GetComponentData<CarLane>(lane);
                car.m_Flags = Flagged(node, EntityManager.GetComponentData<Lane>(lane), car.m_Flags, turns, signs);
                EntityManager.SetComponentData(lane, car);
            }
        }

        private bool Connects(List<Entity> lanes, PathNode start, PathNode end)
        {
            foreach (Entity lane in lanes)
            {
                Lane path = EntityManager.GetComponentData<Lane>(lane);
                if (path.m_StartNode.Equals(start) && path.m_EndNode.Equals(end))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// A junction lane from one road's lane into another's, made the way
        /// the game makes its own (LaneSystem.CreateNodeLane): the lane type
        /// of the lane it comes from, a curve along both lanes' directions,
        /// and the flags for its turn. It stands alone, in no group of
        /// parallel lanes, so the route search sees it directly.
        /// </summary>
        private Entity Create(Entity node, LaneEnd from, LaneEnd to, ushort middle, List<Entity> built, bool signals)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(from.Lane).m_Prefab;
            if (!EntityManager.HasComponent<NetLaneArchetypeData>(prefab) || !EntityManager.HasComponent<NetLaneData>(prefab))
                return Entity.Null;
            var curve = new Curve { m_Bezier = NetUtils.FitCurve(from.Position, from.Direction, to.Direction, to.Position) };
            curve.m_Length = MathUtils.Length(curve.m_Bezier);
            CarLane fromCar = EntityManager.GetComponentData<CarLane>(from.Lane);
            CarLane toCar = EntityManager.GetComponentData<CarLane>(to.Lane);
            var car = new CarLane
            {
                m_DefaultSpeedLimit = math.lerp(fromCar.m_DefaultSpeedLimit, toCar.m_DefaultSpeedLimit, 0.5f),
                m_SpeedLimit = math.lerp(fromCar.m_SpeedLimit, toCar.m_SpeedLimit, 0.5f),
                m_Curviness = NetUtils.CalculateCurviness(curve, EntityManager.GetComponentData<NetLaneData>(prefab).m_Width),
                m_Flags = CarLaneFlags.ForbidPassing | Turn(from, to, m_CityConfiguration.leftHandTraffic),
            };
            // Who gives way is decided per road leading in; the new lane
            // takes it from the game's lanes from the same road.
            foreach (Entity lane in built)
            {
                if (EntityManager.GetComponentData<Lane>(lane).m_StartNode.OwnerEquals(new PathNode(from.Edge, 0)))
                {
                    car.m_Flags |= EntityManager.GetComponentData<CarLane>(lane).m_Flags & PriorityFlags;
                    break;
                }
            }
            if (signals)
                car.m_Flags |= CarLaneFlags.TrafficLights;

            Entity entity = EntityManager.CreateEntity(EntityManager.GetComponentData<NetLaneArchetypeData>(prefab).m_NodeLaneArchetype);
            EntityManager.SetComponentData(entity, new PrefabRef { m_Prefab = prefab });
            EntityManager.SetComponentData(entity, new Lane { m_StartNode = from.Node, m_MiddleNode = new PathNode(node, middle), m_EndNode = to.Node });
            EntityManager.SetComponentData(entity, curve);
            EntityManager.SetComponentData(entity, car);
            EntityManager.AddComponentData(entity, new Owner { m_Owner = node });
            if (signals)
                EntityManager.AddComponent<LaneSignal>(entity);
            return entity;
        }

        /// <summary>The turn flags of a lane between two lane ends, by the angle between their directions.</summary>
        internal static CarLaneFlags Turn(LaneEnd from, LaneEnd to, bool leftHandTraffic)
        {
            float2 a = math.normalizesafe(from.Direction.xz);
            float2 b = math.normalizesafe(to.Direction.xz);
            // Positive turns to the left, seen from above.
            float angle = math.degrees(math.atan2(a.x * b.y - a.y * b.x, math.dot(a, b)));
            if (from.Edge == to.Edge || math.abs(angle) > 150f)
            {
                bool left = math.abs(angle) > 150f ? !leftHandTraffic : angle > 0f;
                return left ? CarLaneFlags.UTurnLeft : CarLaneFlags.UTurnRight;
            }
            if (math.abs(angle) < 20f)
                return CarLaneFlags.Forward;
            if (math.abs(angle) < 60f)
                return angle > 0f ? CarLaneFlags.GentleTurnLeft : CarLaneFlags.GentleTurnRight;
            return angle > 0f ? CarLaneFlags.TurnLeft : CarLaneFlags.TurnRight;
        }

        /// <summary>Whether a rule of the player's takes away the connection a lane of the junction makes.</summary>
        public static bool Removes(DynamicBuffer<LaneConnectionRule> rules, Lane lane)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                LaneConnectionRule rule = rules[i];
                if (rule.Change == LaneConnectionChange.Removed && Is(lane.m_StartNode, rule.FromEdge, rule.FromLane)
                    && Is(lane.m_EndNode, rule.ToEdge, rule.ToLane))
                    return true;
            }
            return false;
        }

        private static bool Is(PathNode node, Entity edge, byte index)
        {
            return node.OwnerEquals(new PathNode(edge, 0)) && (node.GetLaneIndex() & 0xFF) == index;
        }

        /// <summary>Whether a rule forbids the turn a lane of the junction makes.</summary>
        public static bool Forbids(DynamicBuffer<TurnRule> rules, Lane lane)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                TurnRule rule = rules[i];
                if (rule.Forbidden && lane.m_StartNode.OwnerEquals(new PathNode(rule.From, 0))
                    && lane.m_EndNode.OwnerEquals(new PathNode(rule.To, 0)))
                    return true;
            }
            return false;
        }

        /// <summary>The sign on the approach a lane of the junction comes from.</summary>
        public static PrioritySign SignFor(DynamicBuffer<PriorityRule> rules, Lane lane)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                if (lane.m_StartNode.OwnerEquals(new PathNode(rules[i].Edge, 0)))
                    return rules[i].Sign;
            }
            return PrioritySign.Game;
        }

        /// <summary>
        /// A lane's flags with a sign. The game compares the flags of two
        /// lanes that meet by what only one of them has: a lane with Stop or
        /// Yield gives way to one without, one with RightOfWay goes before
        /// one without. Lanes of the priority road all have RightOfWay, so
        /// among themselves the game's usual rules apply: a left turn waits
        /// for the oncoming traffic.
        /// </summary>
        public static CarLaneFlags WithSign(CarLaneFlags flags, PrioritySign sign)
        {
            switch (sign)
            {
                case PrioritySign.Priority:
                    return (flags & ~PriorityFlags) | CarLaneFlags.RightOfWay;
                case PrioritySign.Yield:
                    return (flags & ~PriorityFlags) | CarLaneFlags.Yield;
                case PrioritySign.Stop:
                    return (flags & ~PriorityFlags) | CarLaneFlags.Stop;
                default:
                    return flags;
            }
        }
    }
}
