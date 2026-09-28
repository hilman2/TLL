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
    /// Applies the turn rules of a junction (<see cref="TurnRule"/>) to its
    /// lanes, every time the game has built them anew.
    ///
    /// The game builds a node's lanes in Modification4 whenever the node is
    /// Updated, and writes each lane's flags afresh, so a rule set once would
    /// be lost with the next change to the junction. The new lanes exist from
    /// the end of Modification4. Everything that reads their flags runs later:
    /// the lane index of the node (LaneReferencesSystem), who gives way to
    /// whom (LaneOverlapSystem), the signs (SecondaryObjectSystem), TLL's
    /// junction set-up, and at the end of the frame the route search
    /// (LanesModifiedSystem). This system runs first in Modification4B, so
    /// all of them see the rules. To change the rules of a junction, the node
    /// is rebuilt (RebuildRequest): allowing a turn again is then just not
    /// flagging it.
    /// </summary>
    public partial class TurnRuleSystem : TllSystemBase
    {
        /// <summary>The flags the game gives the turns its own road upgrades forbid.</summary>
        public const CarLaneFlags ForbiddenFlags = CarLaneFlags.Unsafe | CarLaneFlags.Forbidden;

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
                    // Tram tracks follow their lines, not the route search;
                    // a lane shared with a tram is left as it is.
                    if (!EntityManager.HasBuffer<TurnRule>(node) || EntityManager.HasComponent<TrackLane>(lane))
                        continue;
                    if (Forbids(EntityManager.GetBuffer<TurnRule>(node, true), EntityManager.GetComponentData<Lane>(lane)))
                    {
                        CarLane car = EntityManager.GetComponentData<CarLane>(lane);
                        car.m_Flags |= ForbiddenFlags;
                        EntityManager.SetComponentData(lane, car);
                    }
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
    }
}
