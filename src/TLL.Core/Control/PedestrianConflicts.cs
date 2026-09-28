namespace TLL.Core.Control
{
    /// <summary>
    /// Decides when pedestrians get their own phase (a scramble) instead of
    /// crossing alongside the vehicles, from what happened in the last
    /// vehicle greens.
    ///
    /// A green counts as a conflict when a turning vehicle was held at the
    /// line with people on its crosswalk. Once the conflicts pile up,
    /// pedestrians are diverted into the scramble for good: from then on the
    /// two no longer meet, so the conflicts cannot tell whether the scramble
    /// is still needed, and a scramble on demand costs nothing while nobody
    /// presses the button.
    /// </summary>
    public struct PedestrianConflicts
    {
        /// <summary>Greens remembered.</summary>
        public const int Window = 8;

        /// <summary>Conflicts within the window from which pedestrians are diverted.</summary>
        public const int DivertAt = 5;

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

        /// <summary>Records the green that just ended; diverts pedestrians once the conflicts reach <see cref="DivertAt"/>.</summary>
        public void Record(bool conflict)
        {
            History = (byte)((History << 1) | (conflict ? 1 : 0));
            if (Count >= DivertAt)
                Divert = true;
        }
    }
}
