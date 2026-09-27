using TLL.Core.Advisor;
using Xunit;

namespace TLL.Core.Tests.Advisor
{
    public class FlashScheduleTests
    {
        // Night traffic that flashes, and a rush hour that does not.
        private const float QuietMajor = 200f, QuietMinor = 30f, QuietMinorTotal = 60f;
        private const float BusyMajor = 900f, BusyMinor = 100f, BusyMinorTotal = 200f;

        [Fact]
        public void QueueUnderFlashingEndsItAtOnce()
        {
            // Flashing has just started, and the traffic figures still look
            // quiet: they lag behind. The queue ends it without the hold.
            var s = new FlashSchedule();
            Assert.False(s.Round(true, true, QuietMajor, QuietMinor, QuietMinorTotal, FlashSchedule.BacklogQueue));
        }

        [Fact]
        public void ShortQueueKeepsFlashing()
        {
            var s = FlashSchedule.Start;
            Assert.True(s.Round(true, true, QuietMajor, QuietMinor, QuietMinorTotal, FlashSchedule.BacklogQueue - 1f));
        }

        [Fact]
        public void BusyTrafficWaitsForTheHold()
        {
            var s = new FlashSchedule();
            Assert.True(s.Round(true, true, BusyMajor, BusyMinor, BusyMinorTotal, 0f));
            Assert.False(s.Round(true, true, BusyMajor, BusyMinor, BusyMinorTotal, 0f));
        }

        [Fact]
        public void NoFlashingForTheRestOfTheRushHourAfterABacklog()
        {
            var s = FlashSchedule.Start;
            Assert.False(s.Round(true, true, QuietMajor, QuietMinor, QuietMinorTotal, 6f));
            // Quiet figures and no queue while the lights run: flashing only
            // comes back after the longer hold.
            for (int round = 1; round < FlashSchedule.BacklogHoldRounds; round++)
                Assert.False(s.Round(false, true, QuietMajor, QuietMinor, QuietMinorTotal, 0f), $"flashes again after {round} rounds");
            Assert.True(s.Round(false, true, QuietMajor, QuietMinor, QuietMinorTotal, 0f));
        }

        [Fact]
        public void OrdinaryEndKeepsTheShortHold()
        {
            var s = FlashSchedule.Start;
            Assert.False(s.Round(true, true, BusyMajor, BusyMinor, BusyMinorTotal, 0f));
            Assert.False(s.Round(false, true, QuietMajor, QuietMinor, QuietMinorTotal, 0f));
            Assert.True(s.Round(false, true, QuietMajor, QuietMinor, QuietMinorTotal, 0f));
        }

        [Fact]
        public void QueueAtTheLightsDoesNotMatter()
        {
            // Vehicles wait at red; that says nothing about priority rules.
            var s = FlashSchedule.Start;
            Assert.True(s.Round(false, true, QuietMajor, QuietMinor, QuietMinorTotal, 10f));
        }

        [Fact]
        public void SwitchedOffEndsFlashing()
        {
            var s = FlashSchedule.Start;
            Assert.False(s.Round(true, false, QuietMajor, QuietMinor, QuietMinorTotal, 0f));
        }
    }
}
