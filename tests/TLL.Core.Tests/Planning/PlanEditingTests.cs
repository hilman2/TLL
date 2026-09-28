using System;
using System.Collections.Generic;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class PlanEditingTests
    {
        private static readonly float[] Cross = { 0f, 90f, 180f, 270f };

        private static int Find(JunctionModel m, int source, MovementKind kind)
        {
            int i = m.Movements.FindIndex(x => x.Source == source && x.Kind == kind);
            Assert.True(i >= 0, $"no {kind} from {source}");
            return i;
        }

        [Fact]
        public void AMovementThatFitsGoesIn()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int north = Find(m, 0, MovementKind.Straight);
            int south = Find(m, 2, MovementKind.Straight);
            var phases = new List<ulong> { 1UL << north };

            ToggleResult r = PlanEditing.Toggle(m, phases, 0, south);

            Assert.True(r.Changed && r.Added);
            Assert.Equal((1UL << north) | (1UL << south), phases[0]);
        }

        [Fact]
        public void ACrossingMovementIsRefusedAndNamesWhatItCrosses()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int north = Find(m, 0, MovementKind.Straight);
            int east = Find(m, 1, MovementKind.Straight);
            var phases = new List<ulong> { 1UL << north };

            ToggleResult r = PlanEditing.Toggle(m, phases, 0, east);

            Assert.False(r.Changed);
            Assert.Equal(1UL << north, r.Blocking);
            Assert.Equal(1UL << north, phases[0]);
        }

        [Fact]
        public void APartnerOfTheSameLaneComesAlongAndLeavesAlong()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int left = Find(m, 0, MovementKind.Left);
            int straight = Find(m, 0, MovementKind.Straight);
            m.ShareLane(left, straight);
            var phases = new List<ulong> { 0UL };

            ToggleResult added = PlanEditing.Toggle(m, phases, 0, left);
            Assert.Equal((1UL << left) | (1UL << straight), phases[0]);
            Assert.Equal(1UL << straight, added.Partners);

            ToggleResult removed = PlanEditing.Toggle(m, phases, 0, straight);
            Assert.False(removed.Added);
            Assert.Equal(0UL, phases[0]);
            Assert.Equal(1UL << left, removed.Partners);
        }

        [Fact]
        public void APartnerThatCrossesTheGreenBlocksTheClick()
        {
            // The right turn from 0 alone would fit beside the straight
            // traffic from 1, but it shares its lane with straight on from
            // 0, which crosses that traffic.
            JunctionModel m = ChordModel.Build(Cross, false);
            int turn = Find(m, 0, MovementKind.Right);
            int straight = Find(m, 0, MovementKind.Straight);
            int crossing = Find(m, 1, MovementKind.Straight);
            Assert.True(m.Conflicts.CanShare(turn, crossing), "test needs a turn that fits beside the cross traffic");
            Assert.False(m.Conflicts.CanShare(straight, crossing));
            m.ShareLane(turn, straight);
            var phases = new List<ulong> { 1UL << crossing };

            ToggleResult r = PlanEditing.Toggle(m, phases, 0, turn);

            Assert.False(r.Changed);
            Assert.Equal(1UL << crossing, r.Blocking);
        }

        [Fact]
        public void PartnersFollowAChainOfLanes()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int right = Find(m, 0, MovementKind.Right);
            int straight = Find(m, 0, MovementKind.Straight);
            int left = Find(m, 0, MovementKind.Left);
            m.ShareLane(right, straight);
            m.ShareLane(straight, left);

            Assert.Equal((1UL << straight) | (1UL << left), PlanEditing.Partners(m, right));
        }

        [Fact]
        public void FitsInLeavesOutPhasesWithACrossingAndThoseItIsInAlready()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int north = Find(m, 0, MovementKind.Straight);
            int south = Find(m, 2, MovementKind.Straight);
            int east = Find(m, 1, MovementKind.Straight);
            var phases = new List<ulong> { 1UL << north, 1UL << east, 1UL << south, 0UL };

            // Phase 0 has the oncoming straight traffic, which it runs with;
            // phase 1 the cross traffic; phase 2 has it already.
            Assert.Equal(0b1001U, PlanEditing.FitsIn(m, phases, south));
            Assert.Equal(0b1101U, PlanEditing.FitsIn(m, new List<ulong> { 1UL << north, 1UL << east, 0UL, 0UL }, south));
        }

        public static IEnumerable<object[]> Junctions()
        {
            var random = new Random(815);
            for (int i = 0; i < 40; i++)
            {
                int n = 3 + random.Next(3);
                var angles = new float[n];
                float a = (float)random.NextDouble() * 360f;
                for (int k = 0; k < n; k++)
                {
                    a += 30f + (float)random.NextDouble() * (360f - 30f * n) / n;
                    angles[k] = a % 360f;
                }
                yield return new object[] { angles, random.Next(2) == 0, random.Next(1000) };
            }
        }

        [Theory]
        [MemberData(nameof(Junctions))]
        public void FillUpNeverAddsACrossingAndLeavesNothingThatFits(float[] angles, bool lht, int seed)
        {
            JunctionModel m = ChordModel.Build(angles, lht);
            var random = new Random(seed);
            int start = random.Next(m.Movements.Count);
            ulong phase = PlanEditing.FillUp(m, 1UL << start);

            Assert.True((phase & (1UL << start)) != 0UL);
            for (int a = 0; a < m.Movements.Count; a++)
            {
                for (int b = 0; b < m.Movements.Count; b++)
                {
                    if ((phase & (1UL << a)) != 0UL && (phase & (1UL << b)) != 0UL)
                        Assert.True(m.Conflicts.CanShare(a, b), $"{m.Movements[a]} and {m.Movements[b]}");
                }
                if ((phase & (1UL << a)) == 0UL)
                    Assert.NotEqual(0UL, PlanEditing.Blocking(m, phase, 1UL << a));
            }
        }

        [Fact]
        public void APlacedTurnGoesWhereItsRoadHasGreen()
        {
            JunctionModel m = ChordModel.Build(Cross, false, crosswalks: false);
            int north = Find(m, 0, MovementKind.Straight);
            int south = Find(m, 2, MovementKind.Straight);
            int east = Find(m, 1, MovementKind.Straight);
            int west = Find(m, 3, MovementKind.Straight);
            int rightFromEast = Find(m, 1, MovementKind.Right);
            var phases = new List<ulong> { (1UL << north) | (1UL << south), (1UL << east) | (1UL << west) };

            uint into = PlanEditing.Place(m, phases, rightFromEast);

            Assert.Equal(0b10U, into);
            Assert.True((phases[1] & (1UL << rightFromEast)) != 0UL);
            Assert.Equal(2, phases.Count);
        }

        [Fact]
        public void AMovementThatFitsNowhereGetsANewPhase()
        {
            JunctionModel m = ChordModel.Build(Cross, false, crosswalks: false);
            int north = Find(m, 0, MovementKind.Straight);
            int east = Find(m, 1, MovementKind.Straight);
            int west = Find(m, 3, MovementKind.Straight);
            var phases = new List<ulong> { (1UL << east) | (1UL << west) };

            uint into = PlanEditing.Place(m, phases, north);

            Assert.Equal(0b10U, into);
            Assert.Equal(1UL << north, phases[1]);
        }

        [Fact]
        public void AFullPlanTakesNoNewPhase()
        {
            JunctionModel m = ChordModel.Build(Cross, false, crosswalks: false);
            int north = Find(m, 0, MovementKind.Straight);
            int east = Find(m, 1, MovementKind.Straight);
            var phases = new List<ulong>();
            for (int p = 0; p < PhasePlanner.MaxPhases; p++)
                phases.Add(1UL << east);

            Assert.Equal(0U, PlanEditing.Place(m, phases, north));
            Assert.Equal(PhasePlanner.MaxPhases, phases.Count);
        }
    }
}
