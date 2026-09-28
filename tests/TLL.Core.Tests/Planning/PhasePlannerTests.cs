using System;
using System.Collections.Generic;
using System.Linq;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class PhasePlannerTests
    {
        private static readonly float[] Cross = { 0f, 90f, 180f, 270f };

        public static IEnumerable<object[]> Junctions()
        {
            var random = new Random(4711);
            var strategies = (PlanStrategy[])Enum.GetValues(typeof(PlanStrategy));
            for (int i = 0; i < 60; i++)
            {
                int n = 3 + random.Next(4);
                var angles = new float[n];
                float a = (float)random.NextDouble() * 360f;
                for (int k = 0; k < n; k++)
                {
                    // Spread the arms so no two are closer than 25 degrees.
                    a += 25f + (float)random.NextDouble() * (360f - 25f * n) / n;
                    angles[k] = a % 360f;
                }
                yield return new object[] { angles, random.Next(2) == 0, random.Next(2) == 0, strategies[i % strategies.Length] };
            }
        }

        [Theory]
        [MemberData(nameof(Junctions))]
        public void NoPhaseHoldsAHardConflict(float[] angles, bool lht, bool uTurns, PlanStrategy strategy)
        {
            // A pair the geometry forbids never shares a phase. A pair only the
            // strategy forbids may, if the two do not meet at all.
            JunctionModel m = ChordModel.Build(angles, lht, uTurns);
            PhasePlan plan = PhasePlanner.Build(m, strategy);
            ConflictMatrix rules = PhasePlanner.Adjust(m, strategy);
            foreach (Phase p in plan.Phases)
            {
                for (int x = 0; x < m.Movements.Count; x++)
                {
                    for (int y = 0; y < m.Movements.Count; y++)
                    {
                        if (!p.Has(x) || !p.Has(y))
                            continue;
                        Assert.True(m.Conflicts.Get(x, y) != Relation.Hard, $"{m.Movements[x]} and {m.Movements[y]} share a phase");
                        if (rules.Get(x, y) == Relation.Hard)
                            Assert.True(m.Conflicts.Get(x, y) == Relation.Compatible, $"{m.Movements[x]} and {m.Movements[y]} share a phase against the strategy but meet ({m.Conflicts.Get(x, y)})");
                    }
                }
            }
        }

        [Fact]
        public void ProtectedTurnFromASharedLaneRunsWithItsStraight()
        {
            // Approach 0 has one lane for straight on and left: a left turn
            // waiting for its own arrow would block the straight traffic.
            JunctionModel m = ChordModel.Build(Cross, false);
            int left = m.Movements.FindIndex(x => x.Source == 0 && x.Kind == MovementKind.Left);
            int straight = m.Movements.FindIndex(x => x.Source == 0 && x.Kind == MovementKind.Straight);
            Assert.True(left >= 0 && straight >= 0, "model has no left or straight from approach 0");
            m.ShareLane(left, straight);

            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.ProtectedTurns);

            Assert.Contains(plan.Phases, p => p.Has(left) && p.Has(straight));
        }

        [Fact]
        public void HardConflictsTravelAlongAChainOfSharedLanes()
        {
            // Right shares a lane with straight, straight one with left, but
            // right and left share none: the right turn still waits for
            // whatever the left turn cannot run with.
            JunctionModel m = ChordModel.Build(Cross, false);
            int right = m.Movements.FindIndex(x => x.Source == 0 && x.Kind == MovementKind.Right);
            int straight = m.Movements.FindIndex(x => x.Source == 0 && x.Kind == MovementKind.Straight);
            int left = m.Movements.FindIndex(x => x.Source == 0 && x.Kind == MovementKind.Left);
            m.ShareLane(right, straight);
            m.ShareLane(straight, left);
            ConflictMatrix rules = PhasePlanner.Adjust(m, PlanStrategy.Permissive);
            for (int x = 0; x < m.Movements.Count; x++)
            {
                if (x != right && x != straight && x != left && rules.Get(left, x) == Relation.Hard)
                    Assert.True(rules.Get(right, x) == Relation.Hard, $"{m.Movements[right]} misses the conflict of {m.Movements[left]} with {m.Movements[x]}");
            }
        }

        [Theory]
        [MemberData(nameof(Junctions))]
        public void MovementsSharingALaneGetGreenTogether(float[] angles, bool lht, bool uTurns, PlanStrategy strategy)
        {
            // Every approach has one lane for all its vehicle movements, the
            // hardest case: whatever is at the front must be able to go.
            JunctionModel m = ChordModel.Build(angles, lht, uTurns);
            for (int a = 0; a < m.Movements.Count; a++)
            {
                for (int b = a + 1; b < m.Movements.Count; b++)
                {
                    if (!m.Movements[a].IsPedestrian && !m.Movements[b].IsPedestrian && m.Movements[a].Source == m.Movements[b].Source)
                        m.ShareLane(a, b);
                }
            }
            PhasePlan plan = PhasePlanner.Build(m, strategy);
            for (int a = 0; a < m.Movements.Count; a++)
            {
                for (int b = a + 1; b < m.Movements.Count; b++)
                {
                    if ((m.SharesLaneWith(a) & (1UL << b)) == 0 || !m.Conflicts.CanShare(a, b))
                        continue;
                    Assert.True(plan.Phases.Any(p => p.Has(a) && p.Has(b)), $"{m.Movements[a]} and {m.Movements[b]} share a lane but never have green together ({strategy})");
                }
            }
        }

        [Theory]
        [MemberData(nameof(Junctions))]
        public void EveryMovementGetsGreen(float[] angles, bool lht, bool uTurns, PlanStrategy strategy)
        {
            JunctionModel m = ChordModel.Build(angles, lht, uTurns);
            PhasePlan plan = PhasePlanner.Build(m, strategy);
            Assert.Equal(0UL, plan.Uncovered(m.Movements.Count));
        }

        [Theory]
        [MemberData(nameof(Junctions))]
        public void PermittedIsExactlyWhoMustYield(float[] angles, bool lht, bool uTurns, PlanStrategy strategy)
        {
            JunctionModel m = ChordModel.Build(angles, lht, uTurns);
            PhasePlan plan = PhasePlanner.Build(m, strategy);
            foreach (Phase p in plan.Phases)
            {
                for (int x = 0; x < m.Movements.Count; x++)
                {
                    bool mustYield = false;
                    for (int y = 0; y < m.Movements.Count; y++)
                        mustYield |= p.Has(x) && p.Has(y) && m.Conflicts.Get(x, y) == Relation.Yields;
                    Assert.Equal(mustYield, (p.Permitted & (1UL << x)) != 0);
                }
            }
        }

        [Fact]
        public void PermissiveCrossNeedsTwoPhases()
        {
            PhasePlan plan = PhasePlanner.Build(ChordModel.Build(Cross, false), PlanStrategy.Permissive);
            Assert.Equal(2, plan.Phases.Count);
        }

        [Fact]
        public void ProtectedCrossNeedsFourPhasesAndNeverRunsLeftAgainstOncoming()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.ProtectedTurns);
            Assert.Equal(4, plan.Phases.Count);
            int left = m.IndexOf(1, 0, MovementKind.Left);
            int oncoming = m.IndexOf(3, 1, MovementKind.Straight);
            foreach (Phase p in plan.Phases)
                Assert.False(p.Has(left) && p.Has(oncoming));
        }

        [Fact]
        public void ProtectedPlanRunsTurnsBeforeStraightsOfTheSameAxis()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.ProtectedTurns);
            int firstLeft = IndexOfPhaseWith(plan, m.IndexOf(1, 0, MovementKind.Left));
            int firstStraight = IndexOfPhaseWith(plan, m.IndexOf(1, 3, MovementKind.Straight));
            Assert.True(firstLeft < firstStraight, $"left in phase {firstLeft}, straight in phase {firstStraight}");
        }

        [Fact]
        public void SplitPlanGivesEachApproachItsOwnPhase()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.Split);
            Assert.Equal(4, plan.Phases.Count);
            var sources = new HashSet<int>();
            foreach (Phase p in plan.Phases)
            {
                // The approach a split phase is for: the one with most movements in it.
                int main = Enumerable.Range(0, m.Movements.Count).Where(i => p.Has(i) && !m.Movements[i].IsPedestrian)
                    .GroupBy(i => m.Movements[i].Source).OrderByDescending(g => g.Count()).First().Key;
                sources.Add(main);
                for (int i = 0; i < m.Movements.Count; i++)
                {
                    if (!p.Has(i) || m.Movements[i].IsPedestrian || m.Movements[i].Source == main)
                        continue;
                    for (int j = 0; j < m.Movements.Count; j++)
                    {
                        if (p.Has(j) && j != i)
                            Assert.Equal(Relation.Compatible, m.Conflicts.Get(i, j));
                    }
                }
            }
            Assert.Equal(4, sources.Count);
        }

        [Fact]
        public void TurnIntoItsOwnLaneRunsAlongsideTheCrossStreetEvenWhenSplit()
        {
            // T junction: west (0) and east (1) are the main road, south (2)
            // the stem. On a road with several lanes the right turn out of the
            // stem gets a lane of its own; the lanes say it meets the
            // west-to-east traffic nowhere, which the model reproduces here.
            JunctionModel m = ChordModel.Build(new[] { 180f, 0f, 270f }, false, crosswalks: false);
            int stemRight = m.IndexOf(2, 1, MovementKind.Right);
            int westEast = m.IndexOf(0, 1, MovementKind.Straight);
            m.Conflicts.Set(stemRight, westEast, Relation.Compatible);

            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.Split);

            Phase west = plan.Phases.First(p => p.Has(westEast));
            Assert.True(west.Has(stemRight), "the stem's right turn is red while west-to-east has green");
        }

        [Fact]
        public void ExclusivePedestrianPhaseHoldsAllCrosswalksAndNoVehicle()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.ExclusivePedestrian);
            Phase last = plan.Phases[plan.Phases.Count - 1];
            for (int i = 0; i < m.Movements.Count; i++)
                Assert.Equal(m.Movements[i].IsPedestrian, last.Has(i));
        }

        [Fact]
        public void MovementKeepsGreenAcrossPhasesWhereItFits()
        {
            // In a T junction the right turn out of the stem conflicts with
            // nothing on the main road that cannot yield, so it should not be
            // confined to a single phase.
            JunctionModel m = ChordModel.Build(new[] { 0f, 180f, 270f }, false, crosswalks: false);
            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.Permissive);
            int stemRight = m.IndexOf(2, 0, MovementKind.Right);
            int phases = 0;
            foreach (Phase p in plan.Phases)
                phases += p.Has(stemRight) ? 1 : 0;
            Assert.True(phases >= 2, $"stem right turn is green in {phases} phase(s)");
        }

        [Theory]
        [MemberData(nameof(Junctions))]
        public void TurnOnRedNeverMeetsAnythingItCannotYieldTo(float[] angles, bool lht, bool uTurns, PlanStrategy strategy)
        {
            JunctionModel m = ChordModel.Build(angles, lht, uTurns);
            ConflictMatrix rules = m.Conflicts;
            PhasePlan plan = PhasePlanner.Build(m, strategy);
            MovementKind shortTurn = lht ? MovementKind.Left : MovementKind.Right;
            foreach (Phase p in plan.Phases)
            {
                ulong onRed = PhasePlanner.TurnOnRed(m, p.Green);
                for (int x = 0; x < m.Movements.Count; x++)
                {
                    if ((onRed & (1UL << x)) == 0)
                        continue;
                    Assert.Equal(shortTurn, m.Movements[x].Kind);
                    Assert.False(p.Has(x), $"{m.Movements[x]} already has green");
                    for (int y = 0; y < m.Movements.Count; y++)
                    {
                        Relation r = rules.Get(x, y);
                        if (p.Has(y))
                            Assert.True(r == Relation.Compatible || r == Relation.Yields, $"{m.Movements[x]} turns on red against {m.Movements[y]} ({r})");
                    }
                }
            }
        }

        [Fact]
        public void RightTurnMayGoOnRedWhileOnlyTheApproachItMergesWithHasGreen()
        {
            // Split phasing gives the east approach a phase of its own. The
            // right turn from the north has red then, but it only merges into
            // the westbound traffic from the east, which it can yield to.
            JunctionModel m = ChordModel.Build(Cross, false, crosswalks: false);
            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.Split);
            int northRight = m.IndexOf(1, 2, MovementKind.Right);
            Phase east = plan.Phases.First(p => p.Has(m.IndexOf(0, 2, MovementKind.Straight)));
            Assert.False(east.Has(northRight));

            ulong onRed = PhasePlanner.TurnOnRed(m, east.Green);

            Assert.NotEqual(0UL, onRed & (1UL << northRight));
        }

        [Fact]
        public void NoTurnOnRedIntoAPedestrianScramble()
        {
            // Turning vehicles only give way to the crosswalks, so by the
            // relations alone they could turn on red while everyone walks.
            // That would bring back the very clash the scramble is there for.
            JunctionModel m = ChordModel.Build(Cross, false);
            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.ExclusivePedestrian);
            Phase walk = plan.Phases[plan.Phases.Count - 1];

            Assert.Equal(0UL, PhasePlanner.TurnOnRed(m, walk.Green));
        }

        private static int Between(JunctionModel m, int source, int target)
        {
            for (int i = 0; i < m.Movements.Count; i++)
            {
                if (m.Movements[i].Source == source && m.Movements[i].Target == target && !m.Movements[i].IsPedestrian)
                    return i;
            }
            return -1;
        }

        [Theory]
        [InlineData(PlanStrategy.Permissive)]
        [InlineData(PlanStrategy.ProtectedTurns)]
        [InlineData(PlanStrategy.Split)]
        public void WithAScramblePedestriansWalkOnlyInTheirOwnPhase(PlanStrategy strategy)
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            ulong crosswalks = PhasePlanner.Crosswalks(m);

            PhasePlan scramble = PhasePlanner.Build(m, strategy, scramble: true);

            Assert.Equal(crosswalks, scramble.Phases[scramble.Phases.Count - 1].Green);
            for (int p = 0; p < scramble.Phases.Count - 1; p++)
                Assert.Equal(0UL, scramble.Phases[p].Green & crosswalks);
            Assert.Equal(0UL, scramble.Uncovered(m.Movements.Count));
        }

        [Fact]
        public void AScrambleFreesTheVehiclePhasesFromTheCrosswalks()
        {
            // Straight traffic crosses the crosswalks of the road it comes
            // from; with the crosswalks in the vehicle phases, oncoming
            // straight traffic may not run with the other road's crosswalks.
            // Without them, the vehicle phases are those of the junction
            // without crosswalks.
            JunctionModel m = ChordModel.Build(Cross, false);
            JunctionModel bare = ChordModel.Build(Cross, false, crosswalks: false);

            PhasePlan scramble = PhasePlanner.Build(m, PlanStrategy.Permissive, scramble: true);

            Assert.Equal(PhasePlanner.Build(bare, PlanStrategy.Permissive).Phases.Count + 1, scramble.Phases.Count);
        }

        [Fact]
        public void AJunctionWithoutCrosswalksGetsNoScramble()
        {
            JunctionModel bare = ChordModel.Build(Cross, false, crosswalks: false);

            Assert.Equal(PhasePlanner.Build(bare, PlanStrategy.Split).Phases.Count, PhasePlanner.Build(bare, PlanStrategy.Split, scramble: true).Phases.Count);
        }

        [Fact]
        public void ThePedestrianScrambleLayoutIsPermissiveWithAScramble()
        {
            JunctionModel m = ChordModel.Build(Cross, false);

            PhasePlan old = PhasePlanner.Build(m, PlanStrategy.ExclusivePedestrian);
            PhasePlan now = PhasePlanner.Build(m, PlanStrategy.Permissive, scramble: true);

            Assert.Equal(now.Phases.Select(p => p.Green), old.Phases.Select(p => p.Green));
        }

        [Fact]
        public void TheBusiestFlowsShareAPhase()
        {
            var (m, volumes, down, up) = TestJunctions.BentMainRoad();
            Assert.NotEqual(Relation.Hard, m.Conflicts.Get(down, up));

            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.Permissive, volumes);

            Assert.Contains(plan.Phases, p => p.Has(down) && p.Has(up));
        }

        [Fact]
        public void TrafficDoesNotCostAPhase()
        {
            var (m, volumes, _, _) = TestJunctions.BentMainRoad();
            int fewest = PhasePlanner.Build(m, PlanStrategy.Permissive).Phases.Count;
            Assert.True(PhasePlanner.Build(m, PlanStrategy.Permissive, volumes).Phases.Count <= fewest);
        }

        private static int IndexOfPhaseWith(PhasePlan plan, int movement)
        {
            for (int i = 0; i < plan.Phases.Count; i++)
            {
                if (plan.Phases[i].Has(movement))
                    return i;
            }
            return -1;
        }
    }
}
