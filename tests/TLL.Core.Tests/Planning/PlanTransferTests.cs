using System.Collections.Generic;
using System.Linq;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class PlanTransferTests
    {
        private static readonly float[] Cross = { 0f, 90f, 180f, 270f };

        private static List<ulong> Greens(PhasePlan plan) => plan.Phases.Select(p => p.Green).ToList();

        [Fact]
        public void ACrossFitsACrossInFourTurnsTheStraightestFirst()
        {
            List<int[]> maps = PlanTransfer.Rotations(Cross, new[] { 10f, 100f, 190f, 280f });

            Assert.Equal(4, maps.Count);
            Assert.Equal(new[] { 0, 1, 2, 3 }, maps[0]);
        }

        [Fact]
        public void TheArmOrderOfTheNodeDoesNotMatterOnlyTheDirections()
        {
            // The same cross with its roads listed in another order: arm 0
            // at 0 degrees is arm 2 there.
            float[] shuffled = { 90f, 270f, 0f, 180f };

            List<int[]> maps = PlanTransfer.Rotations(Cross, shuffled);

            Assert.Equal(new[] { 2, 0, 3, 1 }, maps[0]);
        }

        [Fact]
        public void ATFitsATTheOtherWayRoundOnlyTurned()
        {
            // Stem at 90 degrees, and at 270: the same T, turned half round.
            List<int[]> maps = PlanTransfer.Rotations(new[] { 0f, 90f, 180f }, new[] { 0f, 180f, 270f });

            int[] map = Assert.Single(maps);
            Assert.Equal(2, map[1]);
        }

        [Fact]
        public void DifferentShapesDoNotFit()
        {
            Assert.Empty(PlanTransfer.Rotations(Cross, new[] { 0f, 90f, 180f }));
            Assert.Empty(PlanTransfer.Rotations(Cross, new[] { 0f, 45f, 180f, 270f }));
        }

        [Fact]
        public void KnownArmsKeepTheirMatchAndAReplacedRoadIsFoundByDirection()
        {
            // Arm 1 was rebuilt: new entity, same direction. Arm 3 is gone.
            float[] before = { 0f, 90f, 180f, 270f };
            float[] after = { 181f, 1f, 88f };
            int[] known = { 1, -1, 0, -1 };

            Assert.Equal(new[] { 1, 2, 0, -1 }, PlanTransfer.MatchArms(before, after, known));
        }

        [Fact]
        public void AJunctionTurnedAsAWholeMatchesByTheTurnOfItsKnownArms()
        {
            // Every arm turned by 40 degrees, beyond the tolerance for a lone
            // arm; the known arm shows the turn.
            float[] before = { 0f, 90f, 180f, 270f };
            float[] after = { 40f, 130f, 220f, 310f };

            Assert.Equal(new[] { 0, 1, 2, 3 }, PlanTransfer.MatchArms(before, after, new[] { 0, -1, -1, -1 }));
            Assert.Equal(new[] { -1, -1, -1, -1 }, PlanTransfer.MatchArms(before, after, null));
        }

        [Fact]
        public void APlanComesOverUnchangedOntoTheSameJunction()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            List<ulong> phases = Greens(PhasePlanner.Build(m, PlanStrategy.ProtectedTurns));

            TransferResult r = PlanTransfer.Transfer(m.Movements, phases, new[] { 0, 1, 2, 3 }, m);

            Assert.True(r.Unchanged);
            Assert.Equal(phases, r.Phases);
            Assert.Equal(Enumerable.Range(0, phases.Count), r.Origin);
        }

        [Fact]
        public void ATurnedPresetPutsItsTurnsOnTheTurnsOfTheNewJunction()
        {
            // The plan protects the left turns of the road through arms 0
            // and 2 only. On a junction whose roads are listed in another
            // order and point elsewhere, it must protect the left turns of
            // the road the map turns that road into.
            // Arms count counter-clockwise, so in right-hand traffic the
            // left turn from 0 leads into 3, the one from 2 into 1.
            JunctionModel a = ChordModel.Build(Cross, false, crosswalks: false);
            int left0 = a.IndexOf(0, 3, MovementKind.Left);
            int left2 = a.IndexOf(2, 1, MovementKind.Left);
            Assert.True(left0 >= 0 && left2 >= 0);
            List<ulong> phases = Greens(PhasePlanner.Build(a, PlanStrategy.Permissive));
            for (int p = 0; p < phases.Count; p++)
                phases[p] &= ~((1UL << left0) | (1UL << left2));
            phases.Insert(0, (1UL << left0) | (1UL << left2));

            float[] shuffled = { 100f, 280f, 10f, 190f };
            JunctionModel b = ChordModel.Build(shuffled, false, crosswalks: false);
            int[] map = PlanTransfer.Rotations(Cross, shuffled)[0];

            TransferResult r = PlanTransfer.Transfer(a.Movements, phases, map, b);

            int b0 = b.IndexOf(map[0], map[3], MovementKind.Left);
            int b2 = b.IndexOf(map[2], map[1], MovementKind.Left);
            Assert.True(b0 >= 0 && b2 >= 0);
            Assert.Equal((1UL << b0) | (1UL << b2), r.Phases[0]);
            Assert.True(r.Unchanged);
        }

        [Fact]
        public void AMovementTheOldPlanDidNotKnowIsPlacedWithItsRoad()
        {
            JunctionModel before = ChordModel.Build(Cross, false, uTurns: false, crosswalks: false);
            List<ulong> phases = Greens(PhasePlanner.Build(before, PlanStrategy.Split));
            JunctionModel after = ChordModel.Build(Cross, false, uTurns: true, crosswalks: false);
            int uturn = after.IndexOf(1, 1, MovementKind.UTurn);

            TransferResult r = PlanTransfer.Transfer(before.Movements, phases, new[] { 0, 1, 2, 3 }, after);

            Assert.True((r.Added & (1UL << uturn)) != 0UL);
            Assert.Empty(r.Dropped);
            int straight = after.IndexOf(1, 3, MovementKind.Straight);
            Assert.Contains(r.Phases, p => (p & (1UL << uturn)) != 0UL && (p & (1UL << straight)) != 0UL);
            Assert.False(PlanCheck.HasErrors(PlanCheck.Run(after, r.Phases)));
        }

        [Fact]
        public void AnArmThatIsGoneDropsItsMovementsAndAPhaseLeftEmpty()
        {
            JunctionModel before = ChordModel.Build(Cross, false, crosswalks: false);
            List<ulong> phases = Greens(PhasePlanner.Build(before, PlanStrategy.Split));
            JunctionModel after = ChordModel.Build(new[] { 0f, 90f, 180f }, false, crosswalks: false);

            TransferResult r = PlanTransfer.Transfer(before.Movements, phases, new[] { 0, 1, 2, -1 }, after);

            Assert.Equal(before.Movements.Count(m => m.Source == 3 || m.Target == 3), r.Dropped.Count);
            Assert.True(r.PhasesDropped);
            Assert.Equal(phases.Count - 1, r.Phases.Count);
            Assert.DoesNotContain(3, r.Origin);
            Assert.False(PlanCheck.HasErrors(PlanCheck.Run(after, r.Phases)));
        }

        [Fact]
        public void MovementsWhosePathsNowCrossArePartedAndLanePartnersJoined()
        {
            JunctionModel before = ChordModel.Build(Cross, false, crosswalks: false);
            int north = before.IndexOf(0, 2, MovementKind.Straight);
            int left = before.IndexOf(0, 3, MovementKind.Left);
            List<ulong> phases = Greens(PhasePlanner.Build(before, PlanStrategy.Permissive));

            // The same junction after a rebuild: the straight and left turn
            // from 0 now share a lane, and a new hard conflict keeps the
            // oncoming straight traffic apart from straight on from 0.
            JunctionModel after = ChordModel.Build(Cross, false, crosswalks: false);
            int south = after.IndexOf(2, 0, MovementKind.Straight);
            after.Conflicts.Set(north, south, Relation.Hard);
            after.ShareLane(north, left);

            TransferResult r = PlanTransfer.Transfer(before.Movements, phases, new[] { 0, 1, 2, 3 }, after);

            Assert.NotEqual(0UL, r.Moved);
            Assert.False(r.Unchanged);
            List<Finding> errors = PlanCheck.Run(after, r.Phases).Where(f => f.Severity == Severity.Error).ToList();
            Assert.Empty(errors);
        }

        [Fact]
        public void APresetFromALeftHandCityProtectsTheSameTurnsOnTheRight()
        {
            // In left-hand traffic the turn across oncoming traffic is the
            // right turn. Mirrored for a right-hand city, the plan must
            // protect left turns and nothing else.
            JunctionModel lht = ChordModel.Build(Cross, true, crosswalks: false);
            List<ulong> phases = Greens(PhasePlanner.Build(lht, PlanStrategy.ProtectedTurns));
            var movements = new List<Movement>(lht.Movements);
            float[] mirrored = PlanTransfer.Mirror(Cross, movements);

            JunctionModel rht = ChordModel.Build(Cross, false, crosswalks: false);
            int[] map = PlanTransfer.Rotations(mirrored, Cross)[0];
            TransferResult r = PlanTransfer.Transfer(movements, phases, map, rht);

            Assert.True(r.Unchanged, $"added {r.Added:x}, moved {r.Moved:x}, dropped {r.Dropped.Count}");
            List<ulong> expected = Greens(PhasePlanner.Build(rht, PlanStrategy.ProtectedTurns));
            Assert.Equal(expected.OrderBy(x => x), r.Phases.OrderBy(x => x));
        }
    }
}
