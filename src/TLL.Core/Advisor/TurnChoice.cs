namespace TLL.Core.Advisor
{
    /// <summary>Who decides whether a turn is allowed, and what they decided.</summary>
    public enum TurnChoice
    {
        /// <summary>The autopilot decides; the turn is allowed unless it forbids it.</summary>
        Autopilot,

        /// <summary>The player forbids the turn.</summary>
        Forbidden,

        /// <summary>The player allows the turn.</summary>
        Allowed,
    }

    public static class TurnChoices
    {
        /// <summary>
        /// The choice after the player clicks a turn's rule in the panel.
        /// Every click changes whether the turn is allowed, except the one
        /// that hands a turn the player allowed back to the autopilot, and
        /// from any choice the clicks lead back to the autopilot.
        /// </summary>
        /// <param name="current">The choice now.</param>
        /// <param name="autopilotForbids">Whether the autopilot forbids the turn; only read for <see cref="TurnChoice.Autopilot"/>.</param>
        public static TurnChoice Next(TurnChoice current, bool autopilotForbids)
        {
            switch (current)
            {
                case TurnChoice.Autopilot:
                    return autopilotForbids ? TurnChoice.Allowed : TurnChoice.Forbidden;
                case TurnChoice.Forbidden:
                    return TurnChoice.Allowed;
                default:
                    return TurnChoice.Autopilot;
            }
        }
    }
}
