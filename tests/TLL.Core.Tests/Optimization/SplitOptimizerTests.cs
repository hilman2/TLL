using System;
using System.Linq;
using TLL.Core.Control;
using TLL.Core.Optimization;
using Xunit;

namespace TLL.Core.Tests.Optimization
{
    public class SplitOptimizerTests
    {
        private const int Intergreen = 16;

        private static PhaseData[] Phases(params (int min, int busy)[] spec)
        {
            return spec.Select(p => new PhaseData
            {
                MinGreen = (ushort)p.min,
                Green = 40,
                Stats = new PhaseStatistics { BusySteps = (uint)p.busy, GreenSteps = 1000, Greens = 20 },
            }).ToArray();
        }

        [Fact]
        public void GreensAndIntergreensAddUpToTheCycle()
        {
            var random = new Random(5);
            for (int round = 0; round < 500; round++)
            {
                int n = 2 + random.Next(5);
                var phases = Phases(Enumerable.Range(0, n).Select(_ => (random.Next(5, 30), random.Next(0, 800))).ToArray());
                int cycle = random.Next(100, 500);
                float[] ratios = SplitOptimizer.FlowRatios(phases, 3000);

                SplitResult r = SplitOptimizer.Splits(phases, ratios, cycle, Intergreen);

                Assert.Equal(r.Cycle, r.Green.Sum(g => g) + n * Intergreen);
                for (int i = 0; i < n; i++)
                    Assert.True(r.Green[i] >= phases[i].MinGreen, $"round {round}: phase {i} got {r.Green[i]}, minimum {phases[i].MinGreen}");
                if (phases.Sum(p => p.MinGreen) + n * Intergreen <= cycle)
                    Assert.Equal(cycle, r.Cycle);
            }
        }

        [Fact]
        public void GreenFollowsUsedGreen()
        {
            var phases = Phases((5, 300), (5, 100));
            float[] ratios = SplitOptimizer.FlowRatios(phases, 3000);
            SplitResult r = SplitOptimizer.Splits(phases, ratios, 200, Intergreen);
            double proportion = r.Green[0] / (double)r.Green[1];
            Assert.InRange(proportion, 2.9, 3.1);
        }

        [Fact]
        public void PhaseThatKeepsMaxingOutGetsMoreThanItShowed()
        {
            var phases = Phases((5, 200), (5, 200));
            phases[0].Stats.MaxOuts = 20;
            float[] ratios = SplitOptimizer.FlowRatios(phases, 3000);
            SplitResult r = SplitOptimizer.Splits(phases, ratios, 200, Intergreen);
            Assert.True(r.Green[0] > r.Green[1], $"{r.Green[0]} vs {r.Green[1]}");
        }

        [Fact]
        public void MoreTrafficMeansLongerCycle()
        {
            var limits = OptimizerLimits.Default;
            int previous = 0;
            for (int busy = 0; busy <= 2400; busy += 200)
            {
                var phases = Phases((5, busy), (5, busy / 2));
                int cycle = SplitOptimizer.OptimalCycle(SplitOptimizer.FlowRatios(phases, 3000), Intergreen, limits);
                Assert.True(cycle >= previous, $"busy {busy}: cycle {cycle} after {previous}");
                Assert.InRange(cycle, limits.MinCycle, limits.MaxCycle);
                previous = cycle;
            }
            Assert.Equal(limits.MaxCycle, previous);
        }

        [Fact]
        public void MinimumsThatDoNotFitGrowTheCycle()
        {
            var phases = Phases((60, 10), (60, 10));
            SplitResult r = SplitOptimizer.Splits(phases, SplitOptimizer.FlowRatios(phases, 3000), 100, Intergreen);
            Assert.Equal(60 + 60 + 2 * Intergreen, r.Cycle);
        }

        [Fact]
        public void RepeatedRoundsConvergeInsteadOfOscillating()
        {
            var phases = Phases((8, 600), (8, 150), (8, 300));
            var limits = OptimizerLimits.Default;
            int[] last = null;
            for (int round = 0; round < 40; round++)
            {
                SplitResult r = SplitOptimizer.Optimize(phases, 3000, Intergreen, limits);
                for (int i = 0; i < phases.Length; i++)
                    phases[i].Green = r.Green[i];
                last = r.Green.Select(g => (int)g).ToArray();
            }
            SplitResult final = SplitOptimizer.Optimize(phases, 3000, Intergreen, limits);
            for (int i = 0; i < phases.Length; i++)
                Assert.InRange(final.Green[i], last[i] - 1, last[i] + 1);
        }
    }
}
