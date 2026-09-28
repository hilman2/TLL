using TLL.Core.Advisor;
using TLL.Core.Planning;
using TLL.Core.Tests.Planning;
using Xunit;

namespace TLL.Core.Tests.Advisor
{
    public class TurnAdvisorTests
    {
        private static readonly DelayParameters P = DelayParameters.Default;

        private const ulong UTurns = (1UL << 1) | (1UL << 6) | (1UL << 12);

        /// <summary>
        /// A straight road through a junction with one U-turn from approach 0,
        /// which crosses the oncoming straight traffic (hard, from the side of
        /// the junction the other traffic uses) and nothing else. Without any
        /// other movement nearby it costs only its own green.
        /// </summary>
        private static (JunctionModel model, float[] volumes) StraightRoad(float uturn, bool hard)
        {
            var m = new JunctionModel { ApproachCount = 2, OppositeOf = new[] { 1, 0 } };
            m.Movements.Add(new Movement(0, 1, MovementKind.Straight, 2));
            m.Movements.Add(new Movement(1, 0, MovementKind.Straight, 2));
            m.Movements.Add(new Movement(0, 0, MovementKind.UTurn));
            m.Conflicts = new ConflictMatrix(3);
            m.Conflicts.Set(2, 1, hard ? Relation.Hard : Relation.Yields);
            return (m, new[] { 900f, 900f, uturn });
        }

        [Fact]
        public void AUTurnSharingALaneKeepsTheBusiestFlowsApart()
        {
            var (m, volumes, down, up) = TestJunctions.BentMainRoadSharedLanes();

            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.Permissive, volumes);

            Assert.DoesNotContain(plan.Phases, p => p.Has(down) && p.Has(up));
        }

        [Fact]
        public void AUTurnThatKeepsTheBusiestFlowsApartIsForbidden()
        {
            var (m, volumes, down, up) = TestJunctions.BentMainRoadSharedLanes();

            ulong forbidden = TurnAdvisor.Choose(m, volumes, volumes, P, UTurns, 0UL);

            Assert.NotEqual(0UL, forbidden & (1UL << 6));
            JunctionModel reduced = m.Without(forbidden, out int[] kept);
            PhasePlan plan = PhasePlanner.Build(reduced, PlanStrategy.Permissive, JunctionModel.Select(volumes, kept));
            int newDown = System.Array.IndexOf(kept, down);
            int newUp = System.Array.IndexOf(kept, up);
            Assert.Contains(plan.Phases, p => p.Has(newDown) && p.Has(newUp));
        }

        [Fact]
        public void OnlyTurnsAreForbidden()
        {
            var (m, volumes, _, _) = TestJunctions.BentMainRoadSharedLanes();
            ulong everything = (1UL << m.Movements.Count) - 1;

            ulong forbidden = TurnAdvisor.Choose(m, volumes, volumes, P, everything, 0UL);

            for (int i = 0; i < m.Movements.Count; i++)
            {
                if ((forbidden & (1UL << i)) != 0)
                    Assert.True(m.Movements[i].IsTurn, $"{m.Movements[i]} was forbidden");
            }
        }

        [Fact]
        public void AUTurnThatChangesNothingDoesNotRideAlong()
        {
            // The U-turn from approach 0 costs a phase, the one from 1 only
            // gives way. Forbidding the first pays; the second stays allowed.
            var (m, volumes) = StraightRoad(0f, hard: true);
            m.Movements.Add(new Movement(1, 1, MovementKind.UTurn));
            var conflicts = new ConflictMatrix(4);
            conflicts.Set(2, 1, Relation.Hard);
            conflicts.Set(3, 0, Relation.Yields);
            m.Conflicts = conflicts;
            volumes = new[] { volumes[0], volumes[1], 0f, 0f };

            Assert.Equal(1UL << 2, TurnAdvisor.Choose(m, volumes, volumes, P, (1UL << 2) | (1UL << 3), 0UL));
        }

        [Fact]
        public void AnEmptyUTurnThatCostsAPhaseIsForbidden()
        {
            var (m, volumes) = StraightRoad(0f, hard: true);

            Assert.Equal(1UL << 2, TurnAdvisor.Choose(m, volumes, volumes, P, 1UL << 2, 0UL));
        }

        [Fact]
        public void AUTurnWhoseDetourCostsMoreThanItsPhaseIsKept()
        {
            // With enough vehicles turning, sending them round the block
            // costs more than the phase they need.
            var (m, volumes) = StraightRoad(400f, hard: true);

            Assert.Equal(0UL, TurnAdvisor.Choose(m, volumes, volumes, P, 1UL << 2, 0UL));
        }

        [Fact]
        public void TheTurnsThePlayerForbadeAreNotTheAdvisors()
        {
            // The advisor had forbidden the U-turn, then the player forbade it
            // too: from now on it is the player's, and the advisor lets go.
            var (m, volumes, _, _) = TestJunctions.BentMainRoadSharedLanes();

            ulong forbidden = TurnAdvisor.Choose(m, volumes, volumes, P, UTurns, 1UL << 6, fixedOut: 1UL << 6);

            Assert.Equal(0UL, forbidden & (1UL << 6));
        }

        [Fact]
        public void ACurrentChoiceStaysWithoutAClearSaving()
        {
            var (m, volumes, _, _) = TestJunctions.BentMainRoadSharedLanes();
            ulong best = TurnAdvisor.Choose(m, volumes, volumes, P, UTurns, 0UL);
            // Every other choice that is not clearly worse than the best stays.
            float bestCost = TurnAdvisor.Cost(m, volumes, volumes, P, best, 0UL, out float people);
            for (ulong current = 0; current <= UTurns; current++)
            {
                if ((current & ~UTurns) != 0 || current == best)
                    continue;
                float cost = TurnAdvisor.Cost(m, volumes, volumes, P, current, 0UL, out float peopleNow);
                ulong chosen = TurnAdvisor.Choose(m, volumes, volumes, P, UTurns, current);
                if (cost - bestCost < TurnAdvisor.MinSaving * peopleNow)
                    Assert.Equal(current, chosen);
                else
                    Assert.Equal(best, chosen);
            }
        }

        [Fact]
        public void WithoutDropsTheMovementAndItsLaneTies()
        {
            var (m, _, _, _) = TestJunctions.BentMainRoadSharedLanes();

            JunctionModel reduced = m.Without(1UL << 6, out int[] kept);

            Assert.Equal(m.Movements.Count - 1, reduced.Movements.Count);
            Assert.DoesNotContain(6, kept);
            for (int a = 0; a < kept.Length; a++)
            {
                Assert.Equal(m.Movements[kept[a]].ToString(), reduced.Movements[a].ToString());
                for (int b = 0; b < kept.Length; b++)
                    Assert.Equal(m.Conflicts.Get(kept[a], kept[b]), reduced.Conflicts.Get(a, b));
            }
            // Straight and left of approach 1 shared the lane with the U-turn
            // and with each other; that tie stays, the U-turn's goes.
            int straight = System.Array.IndexOf(kept, 5);
            int left = System.Array.IndexOf(kept, 7);
            Assert.Equal(1UL << left, reduced.SharesLaneWith(straight));
        }
    }
}
