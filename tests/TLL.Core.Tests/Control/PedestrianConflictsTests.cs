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
        public void QuietGreensAloneDoNotEndIt()
        {
            Assert.True(After("........", After("xxxxx")).Divert);
        }

        [Fact]
        public void TheReviewEndsItOnceConflictsAlmostStopped()
        {
            PedestrianConflicts diverted = After("xxxxx");
            PedestrianConflicts two = After("......", diverted);
            two.Review();
            Assert.True(two.Divert, "6 quiet greens leave 2 conflicts in the window");
            PedestrianConflicts one = After(".......", diverted);
            one.Review();
            Assert.False(one.Divert, "7 quiet greens leave 1");
        }

        [Fact]
        public void TheReviewDoesNotStartIt()
        {
            PedestrianConflicts four = After("x.x.x.x.");
            four.Review();
            Assert.False(four.Divert);
        }

        [Fact]
        public void FourConflictsNeitherStartNorEndIt()
        {
            Assert.False(After("x.x.x.x.").Divert);
            PedestrianConflicts diverted = After("x.x.x.x.", After("xxxxx"));
            diverted.Review();
            Assert.True(diverted.Divert);
        }

        [Fact]
        public void OnlyTheLastEightGreensCount()
        {
            // Five old conflicts, pushed out by eight quiet greens.
            Assert.Equal(0, After("xxxxx........").Count);
        }
    }
}
