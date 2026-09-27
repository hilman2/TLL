using TLL.Core.Control;
using Xunit;

namespace TLL.Core.Tests.Control
{
    public class PedestrianConflictsTests
    {
        private static PedestrianConflicts After(string greens, PedestrianConflicts start = default)
        {
            foreach (char g in greens)
                start.Record(g == 'x');
            return start;
        }

        [Fact]
        public void DivertsAfterFiveConflictsInTheLastEightGreens()
        {
            Assert.False(After("xx.x..x.").Divert);
            Assert.True(After("xx.x..xx").Divert);
        }

        [Fact]
        public void StaysDivertedUntilConflictsAlmostStop()
        {
            PedestrianConflicts diverted = After("xxxxx");
            Assert.True(After("......", diverted).Divert, "6 quiet greens leave 2 conflicts in the window");
            Assert.False(After(".......", diverted).Divert, "7 quiet greens leave 1");
        }

        [Fact]
        public void FourConflictsNeitherStartNorEndIt()
        {
            Assert.False(After("x.x.x.x.").Divert);
            Assert.True(After("x.x.x.x.", After("xxxxx")).Divert);
        }

        [Fact]
        public void OnlyTheLastEightGreensCount()
        {
            // Five old conflicts, pushed out by eight quiet greens.
            Assert.Equal(0, After("xxxxx........").Count);
        }
    }
}
