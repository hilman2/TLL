namespace TLL.Core.Coordination
{
    /// <summary>
    /// Green window of one movement within a timed cycle, relative to the
    /// cycle start: the movement has green from <see cref="Start"/> for
    /// <see cref="Length"/> steps.
    /// </summary>
    public struct PhaseWindow
    {
        public int Start;
        public int Length;

        /// <summary>
        /// The window of a movement that has green in the phases marked in
        /// <paramref name="hasMovement"/>. Phases follow each other in order,
        /// each green followed by <paramref name="intergreen"/> steps.
        ///
        /// If the movement keeps green over consecutive phases, the window
        /// spans them including the intergreens between them, since the
        /// movement does not stop there. If it has green in separate runs,
        /// the longest run counts. Runs may wrap from the last phase to the
        /// first.
        /// </summary>
        public static PhaseWindow Of(ushort[] greens, int intergreen, bool[] hasMovement)
        {
            int n = greens.Length;
            var starts = new int[n];
            int cycle = 0;
            for (int i = 0; i < n; i++)
            {
                starts[i] = cycle;
                cycle += greens[i] + intergreen;
            }

            bool any = false;
            bool all = true;
            for (int i = 0; i < n; i++)
            {
                any |= hasMovement[i];
                all &= hasMovement[i];
            }
            if (!any)
                return new PhaseWindow();
            if (all)
                return new PhaseWindow { Start = 0, Length = cycle };

            // Start every run right after a phase without the movement, so a
            // run that wraps around the end is found in one piece.
            int first = 0;
            while (hasMovement[first])
                first++;
            var best = new PhaseWindow();
            int k = 1;
            while (k <= n)
            {
                int i = (first + k) % n;
                if (!hasMovement[i])
                {
                    k++;
                    continue;
                }
                int runStart = starts[i];
                int length = 0;
                int j = i;
                int count = 0;
                while (hasMovement[j] && count < n)
                {
                    length += greens[j];
                    count++;
                    int next = (j + 1) % n;
                    if (hasMovement[next])
                        length += intergreen;
                    j = next;
                }
                if (length > best.Length)
                    best = new PhaseWindow { Start = runStart, Length = length };
                k += count;
            }
            return best;
        }
    }
}
