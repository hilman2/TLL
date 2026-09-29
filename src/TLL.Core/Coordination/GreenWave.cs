using System;

namespace TLL.Core.Coordination
{
    /// <summary>
    /// Offsets for a corridor so that traffic released at one end meets green
    /// at every following junction, in both directions as far as possible.
    ///
    /// The quality measure is the bandwidth of a direction: the length of the
    /// longest span of departure times at the first junction for which a
    /// vehicle travelling at the corridor speed passes every junction on
    /// green. The optimiser maximises the weighted sum of both bandwidths.
    ///
    /// In a cluster (<see cref="Corridor.Feeds"/>) it also counts, for all
    /// traffic let into the roads between the junctions, the steps it would
    /// reach the next junction at red, and takes them off the band
    /// (<see cref="Corridor.FeedWeight"/>). That is what keeps a side road
    /// from turning into a road whose far end is red.
    /// </summary>
    public static class GreenWave
    {
        /// <summary>Upper bound on full passes over the junctions.</summary>
        private const int MaxSweeps = 12;

        /// <summary>
        /// Working memory of the bandwidth count for one corridor. The descent
        /// counts bandwidths tens of thousands of times per corridor; new
        /// arrays for each count ran to hundreds of megabytes of garbage.
        /// </summary>
        private sealed class Scratch
        {
            public readonly int[] ArrivalA;
            public readonly int[] ArrivalB;
            public readonly int[] Delta;
            public readonly bool[] Open;

            /// <summary>
            /// Per feed, the red steps at its far junction counted from that
            /// junction's cycle start over two cycles: element k holds the
            /// red steps before k. Two cycles let a window that wraps around
            /// the end be counted as one difference.
            /// </summary>
            public readonly int[][] FeedRed;

            public Scratch(Corridor corridor)
            {
                int n = corridor.Count;
                ArrivalA = new int[n];
                for (int i = 1; i < n; i++)
                    ArrivalA[i] = ArrivalA[i - 1] + corridor.TravelA[i - 1];
                ArrivalB = new int[n];
                for (int i = n - 2; i >= 0; i--)
                    ArrivalB[i] = ArrivalB[i + 1] + corridor.TravelB[i];
                Delta = new int[Math.Max(0, corridor.Cycle) + 1];
                Open = new bool[Math.Max(0, corridor.Cycle)];
                FeedRed = new int[corridor.Feeds.Count][];
                for (int f = 0; f < FeedRed.Length; f++)
                    FeedRed[f] = RedCount(corridor, corridor.Feeds[f]);
            }

            private static int[] RedCount(Corridor corridor, Feed feed)
            {
                int c = Math.Max(1, corridor.Cycle);
                CorridorJunction to = corridor.Junctions[feed.To];
                bool forward = feed.To > feed.From;
                int start = forward ? to.WindowStartA : to.WindowStartB;
                int length = forward ? to.WindowLengthA : to.WindowLengthB;
                // The far junction's through green counts from Lead steps
                // after it began, when the front of its queue has moved off.
                // One that never ends has no queue to wait for.
                int from = length >= c ? 0 : start + feed.Lead;
                int open = length >= c ? c : Math.Max(0, length - feed.Lead);
                var count = new int[2 * c + 1];
                for (int k = 0; k < 2 * c; k++)
                {
                    int t = k % c;
                    bool green = SimTime.Mod(t - from, c) < open;
                    count[k + 1] = count[k] + (green ? 0 : 1);
                }
                return count;
            }
        }

        /// <summary>Bandwidth of direction A for the given offsets, in steps.</summary>
        public static int BandwidthA(Corridor corridor, int[] offsets)
        {
            var scratch = new Scratch(corridor);
            return Bandwidth(corridor, offsets, scratch.ArrivalA, forward: true, scratch);
        }

        /// <summary>Bandwidth of direction B (last junction to first) for the given offsets, in steps.</summary>
        public static int BandwidthB(Corridor corridor, int[] offsets)
        {
            var scratch = new Scratch(corridor);
            return Bandwidth(corridor, offsets, scratch.ArrivalB, forward: false, scratch);
        }

        public static float Score(Corridor corridor, int[] offsets)
        {
            return Score(corridor, offsets, new Scratch(corridor));
        }

        private static float Score(Corridor corridor, int[] offsets, Scratch scratch)
        {
            float score = corridor.WeightA * Bandwidth(corridor, offsets, scratch.ArrivalA, forward: true, scratch)
                + corridor.WeightB * Bandwidth(corridor, offsets, scratch.ArrivalB, forward: false, scratch);
            if (corridor.Feeds.Count > 0)
                score -= corridor.FeedWeight * FedIntoRed(corridor, offsets, scratch);
            return score;
        }

        /// <summary>
        /// Steps of green that let traffic into a road of the cluster which
        /// then reaches the next junction at red, or before that junction's
        /// queue has moved off, weighted by each feed's share of its road
        /// and summed over the roads.
        /// </summary>
        public static float FedIntoRed(Corridor corridor, int[] offsets)
        {
            return FedIntoRed(corridor, offsets, new Scratch(corridor));
        }

        private static float FedIntoRed(Corridor corridor, int[] offsets, Scratch scratch)
        {
            int c = corridor.Cycle;
            if (c <= 0)
                return 0f;
            float sum = 0f;
            for (int f = 0; f < corridor.Feeds.Count; f++)
            {
                Feed feed = corridor.Feeds[f];
                int length = Math.Min(feed.WindowLength, c);
                if (length <= 0)
                    continue;
                // The step of the far junction's cycle at which the first
                // vehicle let in at the start of the window arrives there.
                int arrival = SimTime.Mod(offsets[feed.From] + feed.WindowStart + feed.Travel - offsets[feed.To], c);
                int[] red = scratch.FeedRed[f];
                sum += feed.Weight * (red[arrival + length] - red[arrival]);
            }
            return sum;
        }

        /// <summary>
        /// Offsets (cycle start of each junction, in steps within the cycle)
        /// that maximise the weighted bandwidth. Locked junctions keep their
        /// offset. Without any lock, the first junction stays at 0: only
        /// differences between offsets matter.
        /// </summary>
        public static int[] Optimize(Corridor corridor)
        {
            int n = corridor.Count;
            int[] best = null;
            float bestScore = float.MinValue;
            var scratch = new Scratch(corridor);
            foreach (int[] start in StartingPoints(corridor))
            {
                int[] candidate = Descend(corridor, start, scratch);
                float score = Score(corridor, candidate, scratch);
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            return best ?? new int[n];
        }

        /// <summary>
        /// Offsets for a perfect wave in direction A only: every junction
        /// opens its A window when the platoon that left the first junction
        /// at the start of its window arrives.
        /// </summary>
        public static int[] ProgressionA(Corridor corridor)
        {
            int n = corridor.Count;
            int c = corridor.Cycle;
            var offsets = new int[n];
            int reference = Reference(corridor, out int referenceOffset);
            int arrivalAtReference = 0;
            for (int i = 0; i < reference; i++)
                arrivalAtReference += corridor.TravelA[i];
            int departure = referenceOffset + corridor.Junctions[reference].WindowStartA - arrivalAtReference;
            int arrival = 0;
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                    arrival += corridor.TravelA[i - 1];
                offsets[i] = SimTime.Mod(departure + arrival - corridor.Junctions[i].WindowStartA, c);
            }
            ApplyLocks(corridor, offsets);
            return offsets;
        }

        /// <summary>Like <see cref="ProgressionA"/> for direction B.</summary>
        public static int[] ProgressionB(Corridor corridor)
        {
            int n = corridor.Count;
            int c = corridor.Cycle;
            var offsets = new int[n];
            int reference = Reference(corridor, out int referenceOffset);
            var arrival = new int[n];
            for (int i = n - 2; i >= 0; i--)
                arrival[i] = arrival[i + 1] + corridor.TravelB[i];
            int departure = referenceOffset + corridor.Junctions[reference].WindowStartB - arrival[reference];
            for (int i = 0; i < n; i++)
                offsets[i] = SimTime.Mod(departure + arrival[i] - corridor.Junctions[i].WindowStartB, c);
            ApplyLocks(corridor, offsets);
            return offsets;
        }

        private static int[][] StartingPoints(Corridor corridor)
        {
            int[] a = ProgressionA(corridor);
            int[] b = ProgressionB(corridor);
            // Half-way between the two one-way solutions is often close to the
            // best two-way compromise and gives the descent a third basin.
            var middle = new int[corridor.Count];
            for (int i = 0; i < middle.Length; i++)
            {
                int d = SimTime.Mod(b[i] - a[i], corridor.Cycle);
                if (d > corridor.Cycle / 2)
                    d -= corridor.Cycle;
                middle[i] = SimTime.Mod(a[i] + d / 2, corridor.Cycle);
            }
            ApplyLocks(corridor, middle);
            return new[] { a, b, middle };
        }

        /// <summary>
        /// Coordinate descent: move one junction's offset to its best value
        /// while the others stay put, and repeat over all junctions until a
        /// full pass improves nothing.
        /// </summary>
        private static int[] Descend(Corridor corridor, int[] start, Scratch scratch)
        {
            int n = corridor.Count;
            int c = corridor.Cycle;
            var offsets = (int[])start.Clone();
            bool anyLocked = false;
            for (int i = 0; i < n; i++)
                anyLocked |= corridor.Junctions[i].Locked;

            float current = Score(corridor, offsets, scratch);
            for (int sweep = 0; sweep < MaxSweeps; sweep++)
            {
                bool improved = false;
                for (int i = 0; i < n; i++)
                {
                    if (corridor.Junctions[i].Locked || (!anyLocked && i == 0))
                        continue;
                    int keep = offsets[i];
                    int bestOffset = keep;
                    float bestScore = current;
                    for (int o = 0; o < c; o++)
                    {
                        offsets[i] = o;
                        float score = Score(corridor, offsets, scratch);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestOffset = o;
                        }
                    }
                    offsets[i] = bestOffset;
                    if (bestScore > current)
                    {
                        current = bestScore;
                        improved = true;
                    }
                }
                if (!improved)
                    break;
            }
            return offsets;
        }

        /// <summary>
        /// Counts, for every departure time, at how many junctions the
        /// vehicle meets green, and returns the longest run of departure
        /// times that meet green everywhere.
        /// </summary>
        private static int Bandwidth(Corridor corridor, int[] offsets, int[] arrival, bool forward, Scratch scratch)
        {
            int n = corridor.Count;
            int c = corridor.Cycle;
            if (n == 0 || c <= 0)
                return 0;

            // Junction i is green on arrival for departures in the circular
            // interval [offset + windowStart - arrival, + windowLength).
            // A difference array turns the n intervals into counts in O(n + c).
            int[] delta = scratch.Delta;
            Array.Clear(delta, 0, c + 1);
            for (int i = 0; i < n; i++)
            {
                CorridorJunction j = corridor.Junctions[i];
                int length = forward ? j.WindowLengthA : j.WindowLengthB;
                int windowStart = forward ? j.WindowStartA : j.WindowStartB;
                if (length <= 0)
                    return 0;
                if (length >= c)
                {
                    delta[0]++;
                    delta[c]--;
                    continue;
                }
                int from = SimTime.Mod(offsets[i] + windowStart - arrival[i], c);
                int to = from + length;
                delta[from]++;
                if (to <= c)
                {
                    delta[to]--;
                }
                else
                {
                    delta[c]--;
                    delta[0]++;
                    delta[to - c]--;
                }
            }

            bool[] open = scratch.Open;
            int count = 0;
            bool all = true;
            for (int t = 0; t < c; t++)
            {
                count += delta[t];
                open[t] = count == n;
                all &= open[t];
            }
            if (all)
                return c;

            // Longest circular run: start counting right after a closed step.
            int startAt = 0;
            while (open[startAt])
                startAt++;
            int best = 0;
            int run = 0;
            for (int k = 1; k <= c; k++)
            {
                if (open[(startAt + k) % c])
                {
                    run++;
                    best = Math.Max(best, run);
                }
                else
                {
                    run = 0;
                }
            }
            return best;
        }

        /// <summary>The junction whose offset anchors the others: the first locked one, else the first.</summary>
        private static int Reference(Corridor corridor, out int offset)
        {
            for (int i = 0; i < corridor.Count; i++)
            {
                if (corridor.Junctions[i].Locked)
                {
                    offset = corridor.Junctions[i].LockedOffset;
                    return i;
                }
            }
            offset = 0;
            return 0;
        }

        private static void ApplyLocks(Corridor corridor, int[] offsets)
        {
            for (int i = 0; i < corridor.Count; i++)
            {
                if (corridor.Junctions[i].Locked)
                    offsets[i] = SimTime.Mod(corridor.Junctions[i].LockedOffset, corridor.Cycle);
            }
        }
    }
}
