namespace TLL.Core
{
    /// <summary>
    /// Time base of the signal controller.
    ///
    /// The game simulates 60 frames per simulated second, independent of the
    /// speed setting. Vehicles re-evaluate their path every 16 frames, so a
    /// signal change between two of their updates is not seen any earlier.
    /// TLL therefore steps every controller once per 16 frames and counts all
    /// durations in these steps.
    /// </summary>
    public static class SimTime
    {
        public const int FramesPerSecond = 60;

        public const int FramesPerStep = 16;

        public const float SecondsPerStep = FramesPerStep / (float)FramesPerSecond;

        /// <summary>Converts seconds to whole steps, rounding to the nearest step.</summary>
        public static int ToSteps(float seconds)
        {
            return (int)(seconds / SecondsPerStep + 0.5f);
        }

        public static float ToSeconds(int steps)
        {
            return steps * SecondsPerStep;
        }

        /// <summary>
        /// Returns the controller step that contains a simulation frame.
        /// All controllers derive their cycle position from this, which keeps
        /// coordinated junctions in step without talking to each other.
        /// </summary>
        public static long StepOfFrame(uint frameIndex)
        {
            return frameIndex / FramesPerStep;
        }

        /// <summary>Mathematical modulo: the result is in [0, m) also for negative a.</summary>
        public static int Mod(long a, int m)
        {
            long r = a % m;
            return (int)(r < 0 ? r + m : r);
        }
    }
}
