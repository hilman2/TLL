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
        public void OnceDivertedItStays()
        {
            // With pedestrians in the scramble, turning vehicles no longer
            // meet them: the quiet greens that follow say nothing about
            // whether the scramble is still needed.
            PedestrianConflicts diverted = After(".....................", After("xxxxx"));
            Assert.Equal(0, diverted.Count);
            Assert.True(diverted.Divert);
        }

        [Fact]
        public void FourConflictsDoNotStartIt()
        {
            Assert.False(After("x.x.x.x.").Divert);
        }

        [Fact]
        public void OnlyTheLastEightGreensCount()
        {
            // Five old conflicts, pushed out by eight quiet greens.
            Assert.Equal(0, After("xxxxx........").Count);
        }
    }
}
