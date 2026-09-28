using System;
using System.Collections.Generic;
using System.Linq;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class PlanCheckTests
    {
        private static readonly float[] Cross = { 0f, 90f, 180f, 270f };

        private static int Find(JunctionModel m, int source, MovementKind kind)
        {
            int i = m.Movements.FindIndex(x => x.Source == source && x.Kind == kind);
            Assert.True(i >= 0, $"no {kind} from {source}");
            return i;
        }

        private static List<ulong> Greens(PhasePlan plan) => plan.Phases.Select(p => p.Green).ToList();

        [Fact]
        public void AMovementWithoutGreenIsAnErrorWithTheCandidatePhases()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            List<ulong> phases = Greens(PhasePlanner.Build(m, PlanStrategy.Permissive));
            int north = Find(m, 0, MovementKind.Straight);
            for (int p = 0; p < phases.Count; p++)
                phases[p] &= ~(1UL << north);

            List<Finding> findings = PlanCheck.Run(m, phases);

            Finding f = Assert.Single(findings, x => x.Kind == FindingKind.NoGreen);
            Assert.Equal(north, f.Movement);
            Assert.Equal(Severity.Error, f.Severity);
            Assert.Equal(PlanEditing.FitsIn(m, phases, north), f.FitsIn);
            Assert.NotEqual(0U, f.FitsIn);
            Assert.True(PlanCheck.HasErrors(findings));
        }

        [Fact]
        public void TwoCrossingMovementsInOnePhaseAreAnError()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int north = Find(m, 0, MovementKind.Straight);
            int east = Find(m, 1, MovementKind.Straight);
            List<ulong> phases = Greens(PhasePlanner.Build(m, PlanStrategy.Permissive));
            phases[0] |= (1UL << north) | (1UL << east);

            Finding f = Assert.Single(PlanCheck.Run(m, phases), x => x.Kind == FindingKind.Crossing && x.Movement == Math.Min(north, east));
            Assert.Equal(Math.Max(north, east), f.Other);
            Assert.Equal(0, f.Phase);
        }

        [Fact]
        public void PartnersOfOneLaneInDifferentPhasesAreAnError()
        {
            JunctionModel m = ChordModel.Build(Cross, false, crosswalks: false);
            int left = Find(m, 0, MovementKind.Left);
            int straight = Find(m, 0, MovementKind.Straight);
            m.ShareLane(left, straight);
            var phases = new List<ulong> { 1UL << straight, 1UL << left };
            for (int i = 0; i < m.Movements.Count; i++)
            {
                if (i != left && i != straight)
                    phases.Add(1UL << i);
            }

            List<Finding> split = PlanCheck.Run(m, phases).Where(x => x.Kind == FindingKind.SplitLane).ToList();
            Assert.Single(split);

            phases[0] |= 1UL << left;
            phases[1] |= 1UL << straight;
            Assert.DoesNotContain(PlanCheck.Run(m, phases), x => x.Kind == FindingKind.SplitLane);
        }

        [Fact]
        public void MoreThanSixteenPhasesAreAnError()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            List<ulong> phases = Greens(PhasePlanner.Build(m, PlanStrategy.Split));
            while (phases.Count <= PhasePlanner.MaxPhases)
                phases.Add(phases[0]);

            Assert.Contains(PlanCheck.Run(m, phases), x => x.Kind == FindingKind.TooManyPhases && x.Severity == Severity.Error);
        }

        [Fact]
        public void ATurnGivingWayToHeavyTrafficIsAWarningUnlessItHasAPhaseOfItsOwn()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            int left = Find(m, 0, MovementKind.Left);
            int oncoming = Find(m, 2, MovementKind.Straight);
            Assert.Equal(Relation.Yields, m.Conflicts.Get(left, oncoming));
            var volumes = new float[m.Movements.Count];
            volumes[left] = 200f;
            volumes[oncoming] = 800f;
            List<ulong> permissive = Greens(PhasePlanner.Build(m, PlanStrategy.Permissive));

            Finding f = Assert.Single(PlanCheck.Run(m, permissive, volumes), x => x.Kind == FindingKind.HeavyYield);
            Assert.Equal(left, f.Movement);
            Assert.Equal(800f, f.Value);

            List<ulong> guarded = Greens(PhasePlanner.Build(m, PlanStrategy.ProtectedTurns));
            Assert.DoesNotContain(PlanCheck.Run(m, guarded, volumes), x => x.Kind == FindingKind.HeavyYield);

            // Giving way to people does not count: they cross in short groups.
            volumes[oncoming] = 0f;
            for (int i = 0; i < m.Movements.Count; i++)
            {
                if (m.Movements[i].IsPedestrian)
                    volumes[i] = 5000f;
            }
            Assert.DoesNotContain(PlanCheck.Run(m, permissive, volumes), x => x.Kind == FindingKind.HeavyYield);
        }

        [Fact]
        public void ALongCycleIsAWarning()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            List<ulong> phases = Greens(PhasePlanner.Build(m, PlanStrategy.Permissive));

            Assert.Contains(PlanCheck.Run(m, phases, null, 150f), x => x.Kind == FindingKind.LongCycle && x.Value == 150f);
            Assert.DoesNotContain(PlanCheck.Run(m, phases, null, 90f), x => x.Kind == FindingKind.LongCycle);
        }

        [Fact]
        public void AnEmptyOrRepeatedPhaseIsOnlyAnInfo()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            List<ulong> phases = Greens(PhasePlanner.Build(m, PlanStrategy.Permissive));
            phases.Add(0UL);
            phases.Add(phases[0]);

            List<Finding> findings = PlanCheck.Run(m, phases);

            Assert.Contains(findings, x => x.Kind == FindingKind.EmptyPhase && x.Phase == phases.Count - 2);
            Assert.Contains(findings, x => x.Kind == FindingKind.DuplicatePhase && x.Phase == phases.Count - 1 && x.Other == 0);
            Assert.False(PlanCheck.HasErrors(findings));
        }

        public static IEnumerable<object[]> Junctions()
        {
            var random = new Random(2026);
            var strategies = new[] { PlanStrategy.Permissive, PlanStrategy.ProtectedTurns, PlanStrategy.Split };
            for (int i = 0; i < 60; i++)
            {
                int n = 3 + random.Next(3);
                var angles = new float[n];
                float a = (float)random.NextDouble() * 360f;
                for (int k = 0; k < n; k++)
                {
                    a += 30f + (float)random.NextDouble() * (360f - 30f * n) / n;
                    angles[k] = a % 360f;
                }
                yield return new object[] { angles, random.Next(2) == 0, strategies[i % strategies.Length], random.Next(3) == 0, random.Next(1 << 20) };
            }
        }

        [Theory]
        [MemberData(nameof(Junctions))]
        public void EveryGeneratedPlanPassesTheCheck(float[] angles, bool lht, PlanStrategy strategy, bool scramble, int laneSeed)
        {
            // The planner shows generated plans and templates in the editor.
            // A generated plan the check calls broken would show an error the
            // player never made, so the two must agree, shared lanes included.
            JunctionModel m = ChordModel.Build(angles, lht);
            var random = new Random(laneSeed);
            for (int s = 0; s < m.ApproachCount; s++)
            {
                List<int> from = Enumerable.Range(0, m.Movements.Count).Where(i => m.Movements[i].Source == s && !m.Movements[i].IsPedestrian).ToList();
                if (from.Count >= 2 && random.Next(2) == 0)
                    m.ShareLane(from[random.Next(from.Count)], from[random.Next(from.Count)]);
            }

            List<ulong> phases = Greens(PhasePlanner.Build(m, strategy, null, scramble));
            List<Finding> errors = PlanCheck.Run(m, phases).Where(f => f.Severity == Severity.Error).ToList();

            Assert.True(errors.Count == 0, string.Join("; ", errors.Select(f => $"{f.Kind} {Describe(m, f.Movement)} / {Describe(m, f.Other)} in phase {f.Phase}")));
        }

        private static string Describe(JunctionModel m, int movement) => movement >= 0 ? m.Movements[movement].ToString() : "-";
    }
}
