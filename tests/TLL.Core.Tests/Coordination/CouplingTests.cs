using TLL.Core.Coordination;
using Xunit;

namespace TLL.Core.Tests.Coordination
{
    public class CouplingTests
    {
        [Theory]
        [InlineData(800f, 400f, true)]   // index 2.0
        [InlineData(500f, 400f, false)]  // index 1.25: between the thresholds
        [InlineData(250f, 100f, false)]  // index 2.5, but too few vehicles
        [InlineData(900f, 800f, false)]  // index 1.1: far apart
        public void JoinsOnlyBusyCloseLinks(float volume, float length, bool expected)
        {
            Assert.Equal(expected, Coupling.Couple(volume, length, coordinated: false));
        }

        [Fact]
        public void StaysJoinedBetweenTheThresholds()
        {
            // Index 1.25: not enough to join, enough to stay.
            Assert.False(Coupling.Couple(500f, 400f, coordinated: false));
            Assert.True(Coupling.Couple(500f, 400f, coordinated: true));
            Assert.False(Coupling.Couple(400f, 400f, coordinated: true));
        }

        [Theory]
        [InlineData(11, 0, 82, false)]
        [InlineData(0, 15, 63, false)]
        [InlineData(20, 14, 80, true)]
        public void BandMustCoverAFifthOfTheCycle(int bandA, int bandB, int cycle, bool expected)
        {
            Assert.Equal(expected, Coupling.BandWorthIt(bandA, bandB, cycle, hasA: true, hasB: true, running: false));
        }

        [Fact]
        public void OneWayCorridorIsJudgedByItsOneDirection()
        {
            // No through traffic in B: the planner reports a full-cycle band
            // there, which must not carry a useless band in A.
            Assert.False(Coupling.BandWorthIt(0, 80, 80, hasA: true, hasB: false, running: false));
            Assert.True(Coupling.BandWorthIt(20, 80, 80, hasA: true, hasB: false, running: false));
        }

        [Fact]
        public void RunningWaveStaysAtASlightlyNarrowerBand()
        {
            // 14 of 80 steps both ways: 17.5 %, too little to start, enough to stay.
            Assert.False(Coupling.BandWorthIt(14, 14, 80, hasA: true, hasB: true, running: false));
            Assert.True(Coupling.BandWorthIt(14, 14, 80, hasA: true, hasB: true, running: true));
        }

        [Fact]
        public void AClusterKeepsItsCycleThroughTheSwingsOfItsMembers()
        {
            // The cycles one cluster of six asked for in six rounds of a
            // game, in seconds: it keeps the first. A green wave replans at
            // the big swings, 57, 91 and 97 s.
            int running = SimTime.ToSteps(73f);
            foreach (float wanted in new[] { 70f, 57f, 91f, 77f, 57f, 97f })
            {
                Assert.True(Coupling.KeepsCycle(running, SimTime.ToSteps(wanted), cluster: true), $"{wanted} s");
                Assert.Equal(wanted == 70f || wanted == 77f, Coupling.KeepsCycle(running, SimTime.ToSteps(wanted), cluster: false));
            }
        }

        [Fact]
        public void AClusterReplansWhenItsTrafficReallyChanges()
        {
            int running = SimTime.ToSteps(60f);
            Assert.False(Coupling.KeepsCycle(running, SimTime.ToSteps(90f), cluster: true));
            Assert.False(Coupling.KeepsCycle(running, SimTime.ToSteps(35f), cluster: true));
            Assert.False(Coupling.KeepsCycle(0, SimTime.ToSteps(60f), cluster: true));
        }
    }
}
