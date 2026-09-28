namespace TLL.Core.Control
{
    /// <summary>
    /// How often turning vehicles had to wait for pedestrians in the last
    /// vehicle greens, for the panel and the metrics log. A green counts as
    /// a conflict when a turning vehicle was held at the line with people on
    /// its crosswalk. Whether pedestrians get a phase of their own is not
    /// decided here: the autopilot weighs the delay with a scramble and
    /// without (JunctionAdvisor).
    /// </summary>
    public struct PedestrianConflicts
    {
        /// <summary>Greens remembered.</summary>
        public const int Window = 8;

        /// <summary>One bit per recorded green, newest in bit 0; set for a conflict.</summary>
        public byte History;

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

        /// <summary>Records the green that just ended.</summary>
        public void Record(bool conflict)
        {
            History = (byte)((History << 1) | (conflict ? 1 : 0));
        }
    }
}
