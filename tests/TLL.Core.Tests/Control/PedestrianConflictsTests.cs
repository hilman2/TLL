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
        public void CountsTheConflictsOfTheLastEightGreens()
        {
            Assert.Equal(4, After("xx.x..x.").Count);
        }

        [Fact]
        public void OnlyTheLastEightGreensCount()
        {
            // Five old conflicts, pushed out by eight quiet greens.
            Assert.Equal(0, After("xxxxx........").Count);
        }
    }
}
