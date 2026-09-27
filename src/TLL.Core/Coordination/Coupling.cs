namespace TLL.Core.Coordination
{
    /// <summary>
    /// Whether two neighbouring signals should run as one green wave, and
    /// whether a planned wave is worth running.
    ///
    /// Coordination binds junctions to a common cycle. That pays off only
    /// where platoons from one signal reach the next: heavy traffic between
    /// signals close together. The measure is Whitson's coupling index, the
    /// two-way volume on the link divided by its length; coordination is
    /// usually recommended above 0.5 vehicles per hour per foot. As SCATS
    /// does with its "marriage" and "divorce" of subsystems, a pair is
    /// joined above one threshold and parted only below a lower one, so it
    /// does not switch back and forth around the line.
    /// </summary>
    public static class Coupling
    {
        /// <summary>Coupling index, vehicles per hour and metre, from which a link is coordinated: 0.5 per foot.</summary>
        public const float JoinIndex = 1.6f;

        /// <summary>A coordinated link stays coordinated down to this index.</summary>
        public const float LeaveIndex = 1.1f;

        /// <summary>
        /// Two-way volume below which a link is never coordinated, however
        /// short: a few cars do not form platoons worth a fixed cycle.
        /// </summary>
        public const float MinimumVolume = 300f;

        /// <summary>
        /// Share of the cycle the green band must cover, averaged over both
        /// directions, for a planned wave to run. A narrower band stops most
        /// of the platoon anyway, and the fixed cycle only costs the side
        /// roads.
        /// </summary>
        public const float MinimumBandShare = 0.2f;

        /// <summary>A wave that runs already stays down to this band share.</summary>
        public const float KeepBandShare = 0.15f;

        /// <param name="volume">Two-way volume on the link, vehicles per hour.</param>
        /// <param name="length">Length of the link between the stop lines, metres.</param>
        /// <param name="coordinated">Whether the two junctions run in one wave now.</param>
        public static bool Couple(float volume, float length, bool coordinated)
        {
            if (volume < MinimumVolume || length <= 0f)
                return false;
            float index = volume / length;
            return index >= (coordinated ? LeaveIndex : JoinIndex);
        }

        /// <param name="bandA">Green band in direction A, steps.</param>
        /// <param name="bandB">Green band in direction B, steps.</param>
        /// <param name="cycle">Common cycle, steps.</param>
        /// <param name="hasA">Whether any member has through traffic in direction A.</param>
        /// <param name="hasB">Same for direction B; a one-way corridor has only one.</param>
        /// <param name="running">Whether the wave runs already.</param>
        /// <remarks>
        /// The planner gives a direction without through traffic a band of a
        /// full cycle. Averaged in, it would pass any one-way corridor, so
        /// only directions with traffic count.
        /// </remarks>
        public static bool BandWorthIt(int bandA, int bandB, int cycle, bool hasA, bool hasB, bool running)
        {
            if (cycle <= 0 || (!hasA && !hasB))
                return false;
            float share = hasA && hasB ? (bandA + bandB) / (2f * cycle) : (hasA ? bandA : bandB) / (float)cycle;
            return share >= (running ? KeepBandShare : MinimumBandShare);
        }
    }
}
