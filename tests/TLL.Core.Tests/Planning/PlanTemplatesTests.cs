using System;
using System.Collections.Generic;
using System.Linq;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class PlanTemplatesTests
    {
        private static readonly float[] Cross = { 0f, 90f, 180f, 270f };

        public static IEnumerable<object[]> Junctions()
        {
            var random = new Random(1301);
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
                yield return new object[] { angles, random.Next(2) == 0, random.Next(n), random.Next(2) == 0 };
            }
        }

        [Theory]
        [MemberData(nameof(Junctions))]
        public void EveryTemplatePassesTheCheckAndNoneRepeatsAnother(float[] angles, bool lht, int main, bool scramble)
        {
            JunctionModel m = ChordModel.Build(angles, lht);

            List<TemplatePlan> templates = PlanTemplates.All(m, main, null, scramble);

            Assert.NotEmpty(templates);
            Assert.Equal(TemplateKind.TurnsGiveWay, templates[0].Kind);
            foreach (TemplatePlan t in templates)
            {
                List<Finding> errors = PlanCheck.Run(m, t.Phases).Where(f => f.Severity == Severity.Error).ToList();
                Assert.True(errors.Count == 0, $"{t.Kind}: {string.Join(", ", errors.Select(f => f.Kind))}");
            }
            for (int i = 0; i < templates.Count; i++)
            {
                for (int j = 0; j < i; j++)
                    Assert.False(templates[i].Phases.SequenceEqual(templates[j].Phases), $"{templates[i].Kind} repeats {templates[j].Kind}");
            }
        }

        [Fact]
        public void TurnsLastRunsTheSamePhasesAsTurnsFirstInAnotherOrder()
        {
            JunctionModel m = ChordModel.Build(Cross, false);

            TemplatePlan first = PlanTemplates.Build(m, TemplateKind.ProtectedFirst, 0, null, false);
            TemplatePlan last = PlanTemplates.Build(m, TemplateKind.ProtectedLast, 0, null, false);

            Assert.NotNull(last);
            Assert.Equal(first.Phases.OrderBy(x => x), last.Phases.OrderBy(x => x));
            Assert.False(first.Phases.SequenceEqual(last.Phases));
            int left = m.IndexOf(0, 3, MovementKind.Left);
            int straight = m.IndexOf(0, 2, MovementKind.Straight);
            int turnPhase = last.Phases.FindIndex(p => (p & (1UL << left)) != 0UL && (p & (1UL << straight)) == 0UL);
            int straightPhase = last.Phases.FindIndex(p => (p & (1UL << straight)) != 0UL);
            Assert.True(turnPhase > straightPhase, $"turns in phase {turnPhase}, straight traffic in {straightPhase}; first: {Describe(m, first.Phases)}; last: {Describe(m, last.Phases)}");
        }

        private static string Describe(JunctionModel m, List<ulong> phases) =>
            string.Join(" | ", phases.Select(p => string.Join(",", Enumerable.Range(0, m.Movements.Count).Where(i => (p & (1UL << i)) != 0UL).Select(i => m.Movements[i].ToString()))));

        [Fact]
        public void OnlyTheMainRoadsTurnsAreProtected()
        {
            // Main road through arms 1 and 3; the side road through 0 and 2.
            JunctionModel m = ChordModel.Build(Cross, false);
            int mainLeft = m.IndexOf(1, 0, MovementKind.Left);
            int mainOncoming = m.IndexOf(3, 1, MovementKind.Straight);
            int sideLeft = m.IndexOf(0, 3, MovementKind.Left);
            int sideOncoming = m.IndexOf(2, 0, MovementKind.Straight);
            Assert.True(mainLeft >= 0 && mainOncoming >= 0 && sideLeft >= 0 && sideOncoming >= 0);

            TemplatePlan plan = PlanTemplates.Build(m, TemplateKind.ProtectedMainRoad, 1, null, false);

            Assert.DoesNotContain(plan.Phases, p => (p & (1UL << mainLeft)) != 0UL && (p & (1UL << mainOncoming)) != 0UL);
            Assert.Contains(plan.Phases, p => (p & (1UL << sideLeft)) != 0UL && (p & (1UL << sideOncoming)) != 0UL);
        }

        [Fact]
        public void APedestrianCrossingHasNoRoadsToSplit()
        {
            JunctionModel m = ChordModel.Build(new[] { 0f, 180f }, false);

            List<TemplatePlan> templates = PlanTemplates.All(m, 0, null, false);

            Assert.DoesNotContain(templates, t => t.Kind == TemplateKind.EachRoadAlone);
        }
    }
}
