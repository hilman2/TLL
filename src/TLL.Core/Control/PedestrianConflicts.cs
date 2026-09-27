namespace TLL.Core.Control
{
    /// <summary>
    /// Decides when pedestrians get their own phase (a scramble) instead of
    /// crossing alongside the vehicles, from what happened in the last
    /// vehicle greens.
    ///
    /// A green counts as a conflict when turning vehicles and pedestrians
    /// wanted the same crosswalk in it. While pedestrians cross with the
    /// vehicles, that is a turning vehicle held at the line with people on
    /// its crosswalk. While they are diverted into the scramble, the two no
    /// longer meet, so it is people waiting to cross while turning vehicles
    /// use their crosswalk: without that, the scramble would switch itself
    /// off as soon as it worked.
    /// </summary>
    public struct PedestrianConflicts
    {
        /// <summary>Greens remembered.</summary>
        public const int Window = 8;

        /// <summary>Conflicts within the window from which pedestrians are diverted.</summary>
        public const int DivertAt = 5;

        /// <summary>Conflicts within the window at or below which they cross with the vehicles again.</summary>
        public const int ReturnAt = 1;

        /// <summary>One bit per recorded green, newest in bit 0; set for a conflict.</summary>
        public byte History;

        public bool Divert;

        public int Count
        {
            get
            {
                int n = 0;
                for (int b = History; b != 0; b >>= 1)
                    n += b & 1;
                return n;
            }
        }

        /// <summary>Records the green that just ended and updates <see cref="Divert"/>.</summary>
        public void Record(bool conflict)
        {
            History = (byte)((History << 1) | (conflict ? 1 : 0));
            int n = Count;
            if (!Divert && n >= DivertAt)
                Divert = true;
            else if (Divert && n <= ReturnAt)
                Divert = false;
        }
    }
}
