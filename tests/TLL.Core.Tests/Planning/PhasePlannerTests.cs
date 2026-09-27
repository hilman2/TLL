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
