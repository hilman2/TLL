using TLL.Core.Advisor;
using Xunit;

namespace TLL.Core.Tests.Advisor
{
    public class PriorityAdvisorTests
    {
        private static float[,] Flow(int approaches, params (int from, int to, float perHour)[] movements)
        {
            var flow = new float[approaches, approaches];
            foreach (var (from, to, perHour) in movements)
                flow[from, to] = perHour;
            return flow;
        }

        private static ulong Pair(int a, int b) => (1UL << a) | (1UL << b);

        [Fact]
        public void ABentMainRoadGetsThePriorityRoad()
        {
            // Most traffic turns between the stem (2) and approach 1.
            float[,] flow = Flow(3, (2, 1, 500f), (1, 2, 400f), (0, 1, 80f), (1, 0, 70f), (0, 2, 30f), (2, 0, 20f));

            Assert.Equal(Pair(1, 2), PriorityAdvisor.Choose(flow, 0UL));
        }

        [Fact]
        public void TwoEqualRoadsKeepTheGamesRule()
        {
            float[,] flow = Flow(4, (0, 2, 300f), (2, 0, 300f), (1, 3, 280f), (3, 1, 280f));

            Assert.Equal(0UL, PriorityAdvisor.Choose(flow, 0UL));
        }

        [Fact]
        public void AQuietJunctionKeepsTheGamesRule()
        {
            float[,] flow = Flow(3, (0, 1, 50f), (1, 0, 50f), (2, 0, 5f));

            Assert.Equal(0UL, PriorityAdvisor.Choose(flow, 0UL));
        }

        [Fact]
        public void AMainRoadWithMostOfTheTrafficButLittleLeadIsNotChosen()
        {
            // The pair carries 60 %, but the cross road carries almost as much.
            float[,] flow = Flow(4, (0, 2, 300f), (2, 0, 300f), (1, 3, 200f), (3, 1, 200f));

            Assert.Equal(0UL, PriorityAdvisor.Choose(flow, 0UL));
        }

        [Fact]
        public void AMainRoadWithLessThanHalfTheTrafficIsNotChosen()
        {
            // The pair leads every other by far, but most vehicles turn off
            // it or cross it; as a priority road it would hold them all up.
            float[,] flow = Flow(4, (0, 2, 200f), (2, 0, 200f), (0, 1, 150f), (1, 2, 150f), (2, 3, 150f), (3, 0, 150f), (1, 3, 150f));

            Assert.Equal(0UL, PriorityAdvisor.Choose(flow, 0UL));
        }

        [Fact]
        public void APriorityRoadStaysThroughASmallShift()
        {
            // Share 0.45 and a lead of 1.8: too little to start, enough to keep.
            float[,] flow = Flow(4, (0, 2, 225f), (2, 0, 225f), (1, 3, 125f), (3, 1, 125f),
                (0, 1, 50f), (1, 0, 50f), (1, 2, 50f), (2, 1, 50f), (2, 3, 50f), (3, 0, 50f));

            Assert.Equal(0UL, PriorityAdvisor.Choose(flow, 0UL));
            Assert.Equal(Pair(0, 2), PriorityAdvisor.Choose(flow, Pair(0, 2)));
        }

        [Fact]
        public void APriorityRoadMovesWithTheTraffic()
        {
            float[,] flow = Flow(4, (1, 3, 400f), (3, 1, 400f), (0, 2, 60f), (2, 0, 60f));

            Assert.Equal(Pair(1, 3), PriorityAdvisor.Choose(flow, Pair(0, 2)));
        }

        [Fact]
        public void APriorityRoadGoesWhenTheTrafficEvensOut()
        {
            float[,] flow = Flow(4, (0, 2, 300f), (2, 0, 300f), (1, 3, 280f), (3, 1, 280f));

            Assert.Equal(0UL, PriorityAdvisor.Choose(flow, Pair(0, 2)));
        }
    }
}
