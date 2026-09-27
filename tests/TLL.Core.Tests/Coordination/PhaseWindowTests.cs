using System;
using System.Linq;
using TLL.Core.Coordination;
using Xunit;

namespace TLL.Core.Tests.Coordination
{
    public class PhaseWindowTests
    {
        private static readonly ushort[] Greens = { 20, 10, 15 };
        private const int Intergreen = 5;

        [Theory]
        [InlineData(new[] { true, false, false }, 0, 20)]
        [InlineData(new[] { false, true, true }, 25, 30)]
        [InlineData(new[] { true, false, true }, 40, 40)]
        [InlineData(new[] { true, true, true }, 0, 60)]
        [InlineData(new[] { false, false, false }, 0, 0)]
        public void KnownLayouts(bool[] has, int start, int length)
        {
            PhaseWindow w = PhaseWindow.Of(Greens, Intergreen, has);
            Assert.Equal(start, w.Start);
            Assert.Equal(length, w.Length);
        }

        /// <summary>
        /// The window the slow way: walk the cycle step by step and mark
        /// where the movement has green, then take the longest circular run.
        /// </summary>
        private static int LongestGreen(ushort[] greens, int intergreen, bool[] has, out int start)
        {
            int n = greens.Length;
            int cycle = greens.Sum(g => g) + n * intergreen;
            var green = new bool[cycle];
            int t = 0;
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < greens[i]; k++)
                    green[t++] = has[i];
                bool keeps = has[i] && has[(i + 1) % n];
                for (int k = 0; k < intergreen; k++)
                    green[t++] = keeps;
            }
            start = 0;
            if (green.All(g => g))
                return cycle;
            int best = 0;
            for (int s = 0; s < cycle; s++)
            {
                if (green[s] && green[(s - 1 + cycle) % cycle])
                    continue;
                int run = 0;
                while (run < cycle && green[(s + run) % cycle])
                    run++;
                if (run > best)
                {
                    best = run;
                    start = s;
                }
            }
            return best;
        }

        [Fact]
        public void MatchesStepByStepCount()
        {
            var random = new Random(12);
            for (int round = 0; round < 500; round++)
            {
                int n = 2 + random.Next(6);
                ushort[] greens = Enumerable.Range(0, n).Select(_ => (ushort)(5 + random.Next(60))).ToArray();
                bool[] has = Enumerable.Range(0, n).Select(_ => random.Next(2) == 0).ToArray();
                int intergreen = random.Next(12);

                int expected = LongestGreen(greens, intergreen, has, out int expectedStart);
                PhaseWindow w = PhaseWindow.Of(greens, intergreen, has);

                Assert.Equal(expected, w.Length);
                if (expected > 0 && expected < greens.Sum(g => g) + n * intergreen)
                    Assert.True(expectedStart == w.Start || LongestGreenStartsAt(greens, intergreen, has, w.Start, expected),
                        $"round {round}: window starts at {w.Start}, a longest run starts at {expectedStart}");
            }
        }

        /// <summary>True if a run of green of the given length starts at the given step (there may be several equal runs).</summary>
        private static bool LongestGreenStartsAt(ushort[] greens, int intergreen, bool[] has, int start, int length)
        {
            int n = greens.Length;
            int cycle = greens.Sum(g => g) + n * intergreen;
            int t = 0;
            var green = new bool[cycle];
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < greens[i]; k++)
                    green[t++] = has[i];
                bool keeps = has[i] && has[(i + 1) % n];
                for (int k = 0; k < intergreen; k++)
                    green[t++] = keeps;
            }
            for (int k = 0; k < length; k++)
            {
                if (!green[(start + k) % cycle])
                    return false;
            }
            return true;
        }
    }
}
