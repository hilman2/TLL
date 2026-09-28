using TLL.Core.Advisor;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Advisor
{
    public class LayoutScheduleTests
    {
        private const PlanStrategy A = PlanStrategy.Permissive;
        private const PlanStrategy B = PlanStrategy.Split;

        [Fact]
        public void TwoReviewsMustAgree()
        {
            var s = LayoutSchedule.Start;
            Assert.False(s.Review(A, B, false));
            Assert.True(s.Review(A, B, false));
        }

        [Fact]
        public void AJamChangesAtTheFirstReview()
        {
            var s = LayoutSchedule.Start;
            Assert.True(s.Review(A, B, true));
        }

        [Fact]
        public void ANewLayoutRunsItsDwellEvenWhenItJams()
        {
            // A junction that jams in every layout: the new layout is not
            // dropped at the next review, only once it has been measured.
            var s = LayoutSchedule.Start;
            Assert.True(s.Review(A, B, true));
            for (int review = 1; review < LayoutSchedule.DwellReviews; review++)
                Assert.False(s.Review(B, A, true), $"changed back after {review} reviews");
            Assert.True(s.Review(B, A, true));
        }

        [Fact]
        public void AnotherRecommendationStartsTheCountOver()
        {
            var s = LayoutSchedule.Start;
            Assert.False(s.Review(A, B, false));
            Assert.False(s.Review(A, PlanStrategy.ExclusivePedestrian, false));
            Assert.True(s.Review(A, PlanStrategy.ExclusivePedestrian, false));
        }

        [Fact]
        public void KeepingTheLayoutClearsThePending()
        {
            var s = LayoutSchedule.Start;
            Assert.False(s.Review(A, B, false));
            Assert.False(s.Review(A, A, false));
            Assert.False(s.Review(A, B, false));
        }
    }
}
