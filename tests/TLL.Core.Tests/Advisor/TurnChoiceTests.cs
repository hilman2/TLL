using TLL.Core.Advisor;
using Xunit;

namespace TLL.Core.Tests.Advisor
{
    public class TurnChoiceTests
    {
        private static bool IsForbidden(TurnChoice choice, bool autopilotForbids) =>
            choice == TurnChoice.Forbidden || (choice == TurnChoice.Autopilot && autopilotForbids);

        /// <summary>
        /// A turn the player forbade must come free again with further
        /// clicks. It once stayed forbidden however often it was clicked.
        /// </summary>
        [Theory]
        [InlineData(TurnChoice.Autopilot, false)]
        [InlineData(TurnChoice.Autopilot, true)]
        [InlineData(TurnChoice.Forbidden, false)]
        [InlineData(TurnChoice.Forbidden, true)]
        [InlineData(TurnChoice.Allowed, false)]
        [InlineData(TurnChoice.Allowed, true)]
        public void ClicksLeadBackToTheAutopilot(TurnChoice start, bool autopilotForbids)
        {
            TurnChoice choice = start;
            for (int click = 0; click < 3 && choice != TurnChoice.Autopilot; click++)
                choice = TurnChoices.Next(choice, autopilotForbids);

            Assert.Equal(TurnChoice.Autopilot, choice);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ARoundOfClicksBothAllowsAndForbids(bool autopilotForbids)
        {
            TurnChoice choice = TurnChoice.Autopilot;
            bool sawAllowed = false, sawForbidden = false;
            for (int click = 0; click < 3 && (click == 0 || choice != TurnChoice.Autopilot); click++)
            {
                choice = TurnChoices.Next(choice, autopilotForbids);
                sawAllowed |= !IsForbidden(choice, autopilotForbids);
                sawForbidden |= IsForbidden(choice, autopilotForbids);
            }

            Assert.Equal(TurnChoice.Autopilot, choice);
            Assert.True(sawAllowed, "never allowed");
            Assert.True(sawForbidden, "never forbidden");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheFirstClickOverrulesTheAutopilot(bool autopilotForbids)
        {
            TurnChoice next = TurnChoices.Next(TurnChoice.Autopilot, autopilotForbids);

            Assert.NotEqual(autopilotForbids, IsForbidden(next, autopilotForbids));
        }
    }
}
