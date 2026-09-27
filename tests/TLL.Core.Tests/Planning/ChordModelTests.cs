using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class ChordModelTests
    {
        // Approaches of a plain cross: 0 = east, 1 = north, 2 = west, 3 = south.
        private static readonly float[] Cross = { 0f, 90f, 180f, 270f };

        [Theory]
        [InlineData(1, 0, MovementKind.Left)]      // southbound, leaves east: left
        [InlineData(1, 2, MovementKind.Right)]     // southbound, leaves west: right
        [InlineData(1, 3, MovementKind.Straight)]
        [InlineData(0, 3, MovementKind.Left)]      // westbound, leaves south: left
        [InlineData(0, 1, MovementKind.Right)]     // westbound, leaves north: right
        public void TurnKindFollowsHeading(int source, int target, MovementKind expected)
        {
            Assert.Equal(expected, ChordModel.KindOf(Cross[source], Cross[target], false));
        }

        [Fact]
        public void OppositeApproachesArePaired()
        {
            JunctionModel m = ChordModel.Build(Cross, leftHandTraffic: false);
            Assert.Equal(new[] { 2, 3, 0, 1 }, m.OppositeOf);
        }

        [Fact]
        public void TJunctionStemHasNoOpposite()
        {
            JunctionModel m = ChordModel.Build(new[] { 0f, 180f, 270f }, leftHandTraffic: false);
            Assert.Equal(new[] { 1, 0, -1 }, m.OppositeOf);
        }

        [Fact]
        public void PerpendicularStraightsConflictHard()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int ns = m.IndexOf(1, 3, MovementKind.Straight);
            int ew = m.IndexOf(0, 2, MovementKind.Straight);
            Assert.Equal(Relation.Hard, m.Conflicts.Get(ns, ew));
        }

        [Fact]
        public void LeftTurnYieldsToOncomingStraight()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int left = m.IndexOf(1, 0, MovementKind.Left);
            int oncoming = m.IndexOf(3, 1, MovementKind.Straight);
            Assert.Equal(Relation.Yields, m.Conflicts.Get(left, oncoming));
            Assert.Equal(Relation.HasPriority, m.Conflicts.Get(oncoming, left));
        }

        [Fact]
        public void LeftTurnYieldsToOncomingRightTurnIntoSameRoad()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int left = m.IndexOf(1, 0, MovementKind.Left);
            int right = m.IndexOf(3, 0, MovementKind.Right);
            Assert.Equal(Relation.Yields, m.Conflicts.Get(left, right));
        }

        [Fact]
        public void InLeftHandTrafficTheRightTurnIsTheLongOne()
        {
            JunctionModel m = ChordModel.Build(Cross, leftHandTraffic: true);
            int right = m.IndexOf(1, 2, MovementKind.Right);
            int oncoming = m.IndexOf(3, 1, MovementKind.Straight);
            Assert.Equal(Relation.Yields, m.Conflicts.Get(right, oncoming));
        }

        [Fact]
        public void StraightTrafficNeverSharesACrosswalkItCrosses()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int straight = m.IndexOf(1, 3, MovementKind.Straight);
            Assert.Equal(Relation.Hard, m.Conflicts.Get(straight, m.IndexOfCrosswalk(1)));
            Assert.Equal(Relation.Hard, m.Conflicts.Get(straight, m.IndexOfCrosswalk(3)));
            Assert.Equal(Relation.Compatible, m.Conflicts.Get(straight, m.IndexOfCrosswalk(0)));
        }

        [Fact]
        public void TurningTrafficYieldsToCrosswalk()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int right = m.IndexOf(1, 2, MovementKind.Right);
            Assert.Equal(Relation.Yields, m.Conflicts.Get(right, m.IndexOfCrosswalk(2)));
        }
    }
}
