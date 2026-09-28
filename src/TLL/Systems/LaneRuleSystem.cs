using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Tools;
using TLL.Components;
using Unity.Collections;
using Unity.Entities;

namespace TLL.Systems
{
    /// <summary>
    /// Applies the rules TLL keeps for a junction to its lanes, every time
    /// the game has built them anew: forbidden turns (<see cref="TurnRule"/>)
    /// and signs (<see cref="PriorityRule"/>).
    ///
    /// The game builds a node's lanes in Modification4 whenever the node is
    /// Updated, and writes each lane's flags afresh, so a rule set once would
    /// be lost with the next change to the junction. The new lanes exist from
    /// the end of Modification4. Everything that reads their flags runs later:
    /// the lane index of the node (LaneReferencesSystem), who gives way to
    /// whom and the flags of the lanes leading in (LaneOverlapSystem), the
    /// signs (SecondaryObjectSystem), TLL's junction set-up, and at the end
    /// of the frame the route search (LanesModifiedSystem). This system runs
    /// first in Modification4B, so all of them see the rules. To change the
    /// rules of a junction, the node is rebuilt (RebuildRequest): going back
    /// to the game's own rule is then just not changing its flags.
    /// </summary>
    public partial class LaneRuleSystem : TllSystemBase
    {
        /// <summary>The flags the game gives the turns its own road upgrades forbid.</summary>
        public const CarLaneFlags ForbiddenFlags = CarLaneFlags.Unsafe | CarLaneFlags.Forbidden;

        /// <summary>The flags that decide who gives way at a junction without signals (LaneOverlapSystem).</summary>
        public const CarLaneFlags PriorityFlags = CarLaneFlags.Yield | CarLaneFlags.Stop | CarLaneFlags.RightOfWay;

        private EntityQuery m_Query;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(new EntityQueryDesc
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
            RequireForUpdate(m_Query);
        }

        protected override void OnSafeUpdate()
        {
            using (NativeArray<Entity> lanes = m_Query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity lane in lanes)
                {
                    Entity node = EntityManager.GetComponentData<Owner>(lane).m_Owner;
                    bool turns = EntityManager.HasBuffer<TurnRule>(node);
                    // Signs mean nothing where signals decide.
                    bool signs = EntityManager.HasBuffer<PriorityRule>(node) && !EntityManager.HasComponent<TrafficLights>(node);
                    // Tram tracks follow their lines, not the route search;
                    // a lane shared with a tram is left as it is.
                    if ((!turns && !signs) || EntityManager.HasComponent<TrackLane>(lane))
                        continue;
                    Lane path = EntityManager.GetComponentData<Lane>(lane);
                    CarLane car = EntityManager.GetComponentData<CarLane>(lane);
                    CarLaneFlags before = car.m_Flags;
                    if (turns && Forbids(EntityManager.GetBuffer<TurnRule>(node, true), path))
                        car.m_Flags |= ForbiddenFlags;
                    if (signs)
                        car.m_Flags = WithSign(car.m_Flags, SignFor(EntityManager.GetBuffer<PriorityRule>(node, true), path));
                    if (car.m_Flags != before)
                        EntityManager.SetComponentData(lane, car);
                }
            }
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
