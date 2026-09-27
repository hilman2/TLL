using System;
using System.Linq;
using TLL.Core.Coordination;
using Xunit;

namespace TLL.Core.Tests.Coordination
{
    public class GreenWaveTests
    {
        private static Corridor Uniform(int count, int cycle, int window, int travel, float weightB = 1f)
        {
            var c = new Corridor { Cycle = cycle, WeightB = weightB };
            for (int i = 0; i < count; i++)
            {
                c.Junctions.Add(new CorridorJunction { WindowStartA = 0, WindowLengthA = window, WindowStartB = 0, WindowLengthB = window });
                if (i > 0)
                {
                    c.TravelA.Add(travel);
                    c.TravelB.Add(travel);
                }
            }
            return c;
        }

        private static Corridor RandomCorridor(Random r)
        {
            int n = 2 + r.Next(7);
            int cycle = 150 + r.Next(300);
            var c = new Corridor { Cycle = cycle, WeightA = 0.2f + (float)r.NextDouble(), WeightB = 0.2f + (float)r.NextDouble() };
            for (int i = 0; i < n; i++)
            {
                int lenA = cycle / 4 + r.Next(cycle / 2);
                int lenB = r.Next(2) == 0 ? lenA : cycle / 4 + r.Next(cycle / 2);
                c.Junctions.Add(new CorridorJunction
                {
                    WindowStartA = r.Next(cycle),
                    WindowLengthA = lenA,
                    WindowStartB = r.Next(cycle),
                    WindowLengthB = lenB,
                });
                if (i > 0)
                {
                    c.TravelA.Add(20 + r.Next(300));
                    c.TravelB.Add(20 + r.Next(300));
                }
            }
            return c;
        }

        /// <summary>Bandwidth computed the slow way: follow one vehicle per departure time.</summary>
        private static int BruteForceBandwidth(Corridor c, int[] offsets, bool forward)
        {
            int n = c.Count;
            var open = new bool[c.Cycle];
            for (int t = 0; t < c.Cycle; t++)
            {
                bool green = true;
                long time = t;
                for (int k = 0; k < n; k++)
                {
                    int i = forward ? k : n - 1 - k;
                    if (k > 0)
                        time += forward ? c.TravelA[i - 1] : c.TravelB[i];
                    CorridorJunction j = c.Junctions[i];
                    int start = forward ? j.WindowStartA : j.WindowStartB;
                    int length = forward ? j.WindowLengthA : j.WindowLengthB;
                    long local = ((time - offsets[i] - start) % c.Cycle + c.Cycle) % c.Cycle;
                    green &= local < length;
                }
                open[t] = green;
            }
            if (open.All(o => o))
                return c.Cycle;
            int best = 0;
            for (int s = 0; s < c.Cycle; s++)
            {
                int run = 0;
                while (run < c.Cycle && open[(s + run) % c.Cycle])
                    run++;
                best = Math.Max(best, run);
            }
            return best;
        }

        [Fact]
        public void BandwidthMatchesFollowingSingleVehicles()
        {
            var random = new Random(21);
            for (int round = 0; round < 300; round++)
            {
                Corridor c = RandomCorridor(random);
                int[] offsets = Enumerable.Range(0, c.Count).Select(_ => random.Next(c.Cycle)).ToArray();
                Assert.Equal(BruteForceBandwidth(c, offsets, true), GreenWave.BandwidthA(c, offsets));
                Assert.Equal(BruteForceBandwidth(c, offsets, false), GreenWave.BandwidthB(c, offsets));
            }
        }

        [Fact]
        public void OneWayProgressionPassesTheWholeWindow()
        {
            Corridor c = Uniform(6, 300, 120, 137, weightB: 0f);
            Assert.Equal(120, GreenWave.BandwidthA(c, GreenWave.ProgressionA(c)));
            Assert.Equal(120, GreenWave.BandwidthA(c, GreenWave.Optimize(c)));
        }

        [Fact]
        public void HalfCycleSpacingAllowsFullWavesBothWays()
        {
            // Travel time of half a cycle between neighbours is the textbook
            // case in which alternating offsets serve both directions fully.
            Corridor c = Uniform(5, 200, 100, 100);
            int[] offsets = GreenWave.Optimize(c);
            Assert.Equal(100, GreenWave.BandwidthA(c, offsets));
            Assert.Equal(100, GreenWave.BandwidthB(c, offsets));
        }

        [Fact]
        public void OptimizerIsNeverWorseThanEitherOneWaySolution()
        {
            var random = new Random(8);
            for (int round = 0; round < 150; round++)
            {
                Corridor c = RandomCorridor(random);
                float optimized = GreenWave.Score(c, GreenWave.Optimize(c));
                Assert.True(optimized >= GreenWave.Score(c, GreenWave.ProgressionA(c)), $"round {round}");
                Assert.True(optimized >= GreenWave.Score(c, GreenWave.ProgressionB(c)), $"round {round}");
            }
        }

        [Fact]
        public void LockedJunctionKeepsItsOffset()
        {
            Corridor c = Uniform(4, 240, 90, 77);
            CorridorJunction locked = c.Junctions[2];
            locked.Locked = true;
            locked.LockedOffset = 55;
            c.Junctions[2] = locked;

            int[] offsets = GreenWave.Optimize(c);

            Assert.Equal(55, offsets[2]);
            Assert.Equal(90, GreenWave.BandwidthA(c, GreenWave.ProgressionA(c)));
        }

        [Fact]
        public void HeavierDirectionWins()
        {
            // Spacing that cannot serve both directions fully: the optimiser
            // must favour the direction with the larger weight.
            Corridor c = Uniform(4, 200, 60, 70);
            c.WeightA = 5f;
            c.WeightB = 1f;
            int[] offsets = GreenWave.Optimize(c);
            Assert.Equal(60, GreenWave.BandwidthA(c, offsets));
        }
    }
}
