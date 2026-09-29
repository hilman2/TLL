using TLL.Core.Coordination;
using Xunit;

namespace TLL.Core.Tests.Coordination
{
    public class ClustersTests
    {
        [Fact]
        public void AShortRoadIsTightWhateverItsTraffic()
        {
            Assert.True(Clusters.IsTight(80f, 2, 20f));
        }

        [Fact]
        public void ALongRoadIsNeverTight()
        {
            Assert.False(Clusters.IsTight(260f, 1, 5000f));
        }

        [Fact]
        public void BetweenTheLimitsTheRedQueueDecides()
        {
            // 180 m hold 25.7 cars per lane. 1200/h bring 13.3 in a red of
            // 40 s: more than half of one lane, less than half of two.
            Assert.True(Clusters.IsTight(180f, 1, 1200f));
            Assert.False(Clusters.IsTight(180f, 2, 1200f));
        }

        [Fact]
        public void EitherDirectionMakesTheRoadTight()
        {
            Assert.True(Clusters.Join(180f, 2, 100f, 1, 1200f));
            Assert.True(Clusters.Join(180f, 1, 1200f, 2, 100f));
            Assert.False(Clusters.Join(180f, 2, 100f, 2, 1200f));
        }

        [Fact]
        public void AQuietRoadJoinsNoCluster()
        {
            Assert.False(Clusters.Join(60f, 2, 70f, 2, 70f));
            Assert.True(Clusters.Join(60f, 2, 80f, 2, 80f));
        }

        [Fact]
        public void TheLeadGrowsWithTheRoadUpToTheFrontOfItsQueue()
        {
            Assert.Equal(SimTime.ToSteps(Clusters.StartLoss), Clusters.Lead(0f));
            Assert.Equal(SimTime.ToSteps(Clusters.StartLoss + Clusters.Headway), Clusters.Lead(Clusters.VehicleSpacing));
            int longest = SimTime.ToSteps(Clusters.StartLoss + Clusters.Headway * Clusters.LeadVehicles);
            Assert.Equal(longest, Clusters.Lead(60f));
            Assert.Equal(longest, Clusters.Lead(240f));
        }
    }
}
