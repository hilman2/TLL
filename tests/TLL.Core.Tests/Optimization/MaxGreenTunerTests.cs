using TLL.Core.Control;
using TLL.Core.Optimization;
using Xunit;

namespace TLL.Core.Tests.Optimization
{
    public class MaxGreenTunerTests
    {
        private static PhaseData Phase(float maxSeconds, uint greens, uint maxOuts, uint gapOuts)
        {
            return new PhaseData
            {
                MinGreen = (ushort)SimTime.ToSteps(5f),
                MaxGreen = (ushort)SimTime.ToSteps(maxSeconds),
                Stats = new PhaseStatistics { Greens = greens, MaxOuts = maxOuts, GapOuts = gapOuts },
            };
        }

        [Fact]
        public void APhaseStuckAtItsMaximumGrowsOutOfIt()
        {
            // The trap the old rule set: 7 s of maximum, every green ending
            // there with its queue still standing.
            PhaseData p = Phase(7f, 4, 4, 0);
            int rounds = 0;
            while (p.MaxGreen < SimTime.ToSteps(60f) && rounds < 20)
            {
                p.MaxGreen = (ushort)MaxGreenTuner.Next(p);
                rounds++;
            }
            Assert.True(rounds <= 6, $"{rounds} rounds to reach 60 s, at {SimTime.ToSeconds(p.MaxGreen):0} s");
        }

        [Fact]
        public void NoMaximumBelowTheFloor()
        {
            Assert.Equal(MaxGreenTuner.Floor, MaxGreenTuner.Next(Phase(7f, 4, 0, 4)));
        }

        [Fact]
        public void NoMaximumAboveTheCap()
        {
            Assert.Equal(MaxGreenTuner.Cap, MaxGreenTuner.Next(Phase(88f, 4, 4, 0)));
        }

        [Fact]
        public void GreensThatAllRunEmptyShrinkItSlowly()
        {
            PhaseData p = Phase(60f, 5, 0, 5);
            int next = MaxGreenTuner.Next(p);
            Assert.True(next < p.MaxGreen, "did not shrink");
            Assert.True(next >= p.MaxGreen * 0.85f, $"shrank from {p.MaxGreen} to {next} steps at once");
        }

        [Fact]
        public void AnOccasionalMaxOutLeavesItAsItIs()
        {
            PhaseData p = Phase(40f, 10, 1, 6);
            Assert.Equal(p.MaxGreen, MaxGreenTuner.Next(p));
        }

        [Fact]
        public void WithoutGreensThereIsNothingToLearn()
        {
            PhaseData p = Phase(40f, 0, 0, 0);
            Assert.Equal(p.MaxGreen, MaxGreenTuner.Next(p));
        }

        [Fact]
        public void TheWalkAlwaysFits()
        {
            PhaseData p = Phase(30f, 4, 0, 4);
            p.Flags = PhaseFlags.Pedestrian;
            p.WalkGreen = (ushort)SimTime.ToSteps(28f);
            Assert.True(MaxGreenTuner.Next(p) > p.WalkGreen);
        }
    }
}
