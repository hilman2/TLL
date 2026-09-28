namespace TLL.Core.Advisor
{
    /// <summary>
    /// When an automatic junction flashes yellow, decided once per autopilot
    /// round. <see cref="FlashAdvisor"/> judges the traffic; this adds how
    /// long a change holds, and ends flashing at once when a queue builds up
    /// under it.
    /// </summary>
    /// <remarks>
    /// The traffic figures alone notice a backlog late: under flashing only
    /// as many side road vehicles get through as the main road leaves gaps
    /// for, so the counted rate rises no higher than that, and it is
    /// smoothed over several rounds. The queue shows it in the round it
    /// happens.
    /// </remarks>
    public struct FlashSchedule
    {
        /// <summary>Rounds a change holds before the next one, so the junction does not flicker.</summary>
        public const int HoldRounds = 2;

        /// <summary>
        /// Rounds without flashing after a backlog ended it: 3 game hours,
        /// the rest of a rush hour. The traffic figures still lag behind
        /// then and would start flashing again into the same queue.
        /// </summary>
        public const int BacklogHoldRounds = 8;

        /// <summary>
        /// Vehicles waiting per approach lane, averaged over a round, that end
        /// flashing. Under priority rules that is a side road at about 80 % of
        /// the capacity the gaps in the main road give it.
        /// </summary>
        public const float BacklogQueue = 4f;

        public ushort RoundsSinceChange;

        /// <summary>The last flashing ended because of a queue.</summary>
        public bool EndedByBacklog;

        /// <summary>A schedule that may change at the first round.</summary>
        public static FlashSchedule Start => new FlashSchedule { RoundsSinceChange = HoldRounds };

        /// <summary>Called once per round. Returns whether the junction flashes from now on.</summary>
        /// <param name="flashing">Whether it flashes now.</param>
        /// <param name="allowed">Flashing is on in the settings and the junction has a main road.</param>
        /// <param name="major">See <see cref="FlashAdvisor.Decide"/>; likewise minor and totalMinor.</param>
        /// <param name="worstQueue">Mean vehicles waiting per lane over the last round, on the approach where it is highest.</param>
        /// <param name="removeSignals">
        /// The autopilot advises priority rules for the junction even at its
        /// peak (SignalAdvisor): then it flashes whatever the traffic, as far
        /// as no queue builds up. The traffic thresholds of FlashAdvisor are
        /// for quiet hours at junctions that need their signals otherwise.
        /// </param>
        public bool Round(bool flashing, bool allowed, float major, float minor, float totalMinor, float worstQueue, bool removeSignals = false)
        {
            if (RoundsSinceChange < ushort.MaxValue)
                RoundsSinceChange++;
            bool backlog = flashing && worstQueue >= BacklogQueue;
            bool wanted = allowed && !backlog && (removeSignals || FlashAdvisor.Decide(flashing, major, minor, totalMinor));
            if (wanted == flashing)
                return flashing;
            int hold = EndedByBacklog ? BacklogHoldRounds : HoldRounds;
            if (!backlog && RoundsSinceChange < hold)
                return flashing;
            EndedByBacklog = backlog;
            RoundsSinceChange = 0;
            return wanted;
        }
    }
}
