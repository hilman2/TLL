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
            Assert.Equal(expected, Coupling.BandWorthIt(bandA, bandB, cycle));
        }
    }
}
