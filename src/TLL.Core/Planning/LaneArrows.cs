using System;
using System.Collections.Generic;

namespace TLL.Core.Planning
{
    /// <summary>
    /// The movements one lane of an approach serves: a range of consecutive
    /// movements in the order <see cref="LaneArrows"/> uses, from the turn
    /// on the kerb side to the one across the oncoming traffic.
    /// </summary>
    public struct LaneUse : IEquatable<LaneUse>
    {
        public int First;
        public int Last;

        public LaneUse(int first, int last)
        {
            First = first;
            Last = last;
        }

        public bool Serves(int movement) => movement >= First && movement <= Last;

        public bool Shared => Last > First;

        public bool Equals(LaneUse other) => First == other.First && Last == other.Last;

        public override bool Equals(object obj) => obj is LaneUse other && Equals(other);

        public override int GetHashCode() => (First * 31) ^ Last;

        public override string ToString() => First == Last ? $"{First}" : $"{First}-{Last}";
    }

    /// <summary>
    /// Chooses which lane of an approach goes where: the lane arrows. The
    /// movements of the approach are ordered from the kerb side outwards
    /// (in right-hand traffic: right, straight, left), and so are its lanes.
    /// Each lane serves a range of consecutive movements, and the ranges move
    /// outwards from lane to lane, so no two paths through the junction
    /// cross. Every movement keeps at least one lane and gets no more than
    /// the road it leads into has lanes to take them.
    ///
    /// The best choice has the lowest load on its busiest lane, with the
    /// traffic of a shared lane split as evenly as the lanes allow. A shared
    /// lane carries less than a lane of its own: the first vehicle, waiting
    /// to turn, holds up all behind it (see <see cref="Factor"/>).
    /// </summary>
    public static class LaneArrows
    {
        /// <summary>A new choice must lower the busiest lane's load by at least this share.</summary>
        public const float MinGain = 0.2f;

        /// <summary>Vehicles per hour on the busiest lane below which the lanes stay as they are; no lane is full.</summary>
        public const float MinLoad = 250f;

        /// <summary>Most lanes and movements of one approach the search covers.</summary>
        public const int MaxLanes = 8;
        public const int MaxMovements = 6;

        /// <summary>
        /// The share of a lane of its own that a lane serving
        /// <paramref name="use"/> carries, or 0 where such a lane is not
        /// allowed. A turn on the kerb side slows the straight traffic
        /// behind it a little. A turn across the oncoming traffic waits for
        /// a gap and holds up everything behind it. At signals a lane may not
        /// mix straight traffic and such a turn at all, unless the approach
        /// has only that one lane: the two would have to share every green,
        /// which ties the whole approach to the turn's conflicts.
        /// </summary>
        /// <param name="far">Per movement, whether it turns across the oncoming traffic (left in right-hand traffic) or U-turns.</param>
        /// <param name="straight">Per movement, whether it goes straight on.</param>
        public static float Factor(LaneUse use, bool[] far, bool[] straight, bool signalled, int lanes)
        {
            if (!use.Shared)
                return 1f;
            bool hasFar = false;
            bool hasStraight = false;
            bool hasKerb = false;
            for (int m = use.First; m <= use.Last; m++)
            {
                hasFar |= far[m];
                hasStraight |= straight[m];
                hasKerb |= !far[m] && !straight[m];
            }
            if (hasFar && hasStraight)
            {
                if (signalled)
                    return lanes == 1 ? 0.5f : 0f;
                return hasKerb ? 0.75f : 0.8f;
            }
            if (hasFar)
                return 0.8f;
            return 0.9f;
        }

        /// <summary>
        /// The load on the busiest lane, in vehicles per hour of a lane of its
        /// own, with the traffic of each movement split over its lanes as
        /// evenly as the lanes allow. <see cref="float.MaxValue"/> where a
        /// lane is not allowed or a movement has no lane.
        /// </summary>
        public static float Load(LaneUse[] lanes, float[] volumes, bool[] far, bool[] straight, bool signalled)
        {
            var capacity = new float[lanes.Length];
            for (int j = 0; j < lanes.Length; j++)
            {
                capacity[j] = Factor(lanes[j], far, straight, signalled, lanes.Length);
                if (capacity[j] <= 0f)
                    return float.MaxValue;
            }
            float total = 0f;
            foreach (float v in volumes)
                total += Math.Max(0f, v);
            if (!Covers(lanes, volumes.Length))
                return float.MaxValue;
            if (total <= 0f)
                return 0f;
            // The lowest load L at which every movement fits into its lanes,
            // a lane taking L times its factor. Found by halving the interval.
            float low = 0f;
            float high = total / MinFactor(capacity);
            for (int step = 0; step < 40; step++)
            {
                float mid = 0.5f * (low + high);
                if (Fits(lanes, volumes, capacity, mid))
                    high = mid;
                else
                    low = mid;
            }
            return high;
        }

        /// <summary>
        /// Whether all traffic fits with each lane taking at most
        /// <paramref name="load"/> times its factor. The lanes and the
        /// movements they serve both run outwards, so filling the lanes in
        /// order with the movements in order is as good as any split.
        /// </summary>
        private static bool Fits(LaneUse[] lanes, float[] volumes, float[] capacity, float load)
        {
            int m = 0;
            float left = Math.Max(0f, volumes[0]);
            for (int j = 0; j < lanes.Length; j++)
            {
                float room = load * capacity[j];
                while (m < volumes.Length)
                {
                    if (left <= 0f)
                    {
                        m++;
                        if (m < volumes.Length)
                            left = Math.Max(0f, volumes[m]);
                        continue;
                    }
                    if (!lanes[j].Serves(m))
                    {
                        // Traffic of a movement this lane does not serve is
                        // left over; the lanes further out serve it even less.
                        if (m < lanes[j].First)
                            return false;
                        break;
                    }
                    float take = Math.Min(room, left);
                    room -= take;
                    left -= take;
                    if (room <= 0f)
                        break;
                }
            }
            while (m < volumes.Length && left <= 0f)
            {
                m++;
                if (m < volumes.Length)
                    left = Math.Max(0f, volumes[m]);
            }
            return m >= volumes.Length;
        }

        private static float MinFactor(float[] capacity)
        {
            float min = 1f;
            foreach (float c in capacity)
                min = Math.Min(min, c);
            return min;
        }

        private static bool Covers(LaneUse[] lanes, int movements)
        {
            for (int m = 0; m < movements; m++)
            {
                bool served = false;
                foreach (LaneUse lane in lanes)
                    served |= lane.Serves(m);
                if (!served)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Every way to give <paramref name="lanes"/> lanes to the movements
        /// without crossing paths: each lane a range of movements, the ranges
        /// moving outwards lane by lane, every movement served, and no
        /// movement on more lanes than <paramref name="receiving"/> allows.
        /// </summary>
        public static List<LaneUse[]> Assignments(int lanes, int[] receiving)
        {
            var result = new List<LaneUse[]>();
            int movements = receiving.Length;
            if (lanes < 1 || movements < 1 || lanes > MaxLanes || movements > MaxMovements)
                return result;
            Extend(new LaneUse[lanes], 0, receiving, result);
            return result;
        }

        private static void Extend(LaneUse[] current, int lane, int[] receiving, List<LaneUse[]> result)
        {
            int movements = receiving.Length;
            if (lane == current.Length)
            {
                if (current[lane - 1].Last == movements - 1 && WithinReceiving(current, receiving))
                    result.Add((LaneUse[])current.Clone());
                return;
            }
            int minFirst = lane == 0 ? 0 : current[lane - 1].First;
            int maxFirst = lane == 0 ? 0 : current[lane - 1].Last + 1;
            int minLast = lane == 0 ? 0 : current[lane - 1].Last;
            for (int first = minFirst; first <= maxFirst && first < movements; first++)
            {
                for (int last = Math.Max(first, minLast); last < movements; last++)
                {
                    current[lane] = new LaneUse(first, last);
                    Extend(current, lane + 1, receiving, result);
                }
            }
        }

        private static bool WithinReceiving(LaneUse[] lanes, int[] receiving)
        {
            for (int m = 0; m < receiving.Length; m++)
            {
                int count = 0;
                foreach (LaneUse lane in lanes)
                    count += lane.Serves(m) ? 1 : 0;
                if (count > Math.Max(1, receiving[m]))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The lanes the approach should have, or <paramref name="current"/>
        /// where no choice is clearly better: where it lowers the busiest
        /// lane's load by less than <see cref="MinGain"/>, or no lane is full
        /// (<see cref="MinLoad"/>). Among equally good choices, the one with
        /// fewer shared lanes, then the one closest to the current lanes.
        /// </summary>
        /// <param name="volumes">Vehicles per hour per movement, in the kerb-outwards order.</param>
        /// <param name="receiving">Per movement, the lanes of the road it leads into.</param>
        /// <param name="allowed">Called as <c>allowed(lanes)</c>; false leaves those lanes out. Null allows all.</param>
        public static LaneUse[] Choose(LaneUse[] current, float[] volumes, bool[] far, bool[] straight, int[] receiving, bool signalled,
            out float currentLoad, out float bestLoad, Predicate<LaneUse[]> allowed = null)
        {
            currentLoad = Load(current, volumes, far, straight, signalled);
            LaneUse[] best = current;
            bestLoad = currentLoad;
            foreach (LaneUse[] candidate in Assignments(current.Length, receiving))
            {
                float load = Load(candidate, volumes, far, straight, signalled);
                if (load == float.MaxValue || (allowed != null && !allowed(candidate)))
                    continue;
                if (best == current && bestLoad == float.MaxValue || load < bestLoad * 0.999f
                    || (load <= bestLoad * 1.001f && Rank(candidate, current) < Rank(best, current)))
                {
                    best = candidate;
                    bestLoad = load;
                }
            }
            // Lanes the current choice cannot serve at all (a movement
            // without a lane, a lane that may not exist) always change.
            if (currentLoad == float.MaxValue)
                return best;
            if (currentLoad < MinLoad || bestLoad > currentLoad * (1f - MinGain))
            {
                bestLoad = currentLoad;
                return current;
            }
            return best;
        }

        /// <summary>
        /// The lanes with the lowest <paramref name="cost"/>, among those
        /// allowed and within the lanes each road takes, or
        /// <paramref name="current"/> unless the best saves at least
        /// <paramref name="minSaving"/>. For signals, where the cost is the
        /// junction's delay under its best phase layout: a shared lane ties
        /// its movements to one green, which the lane load does not show.
        /// </summary>
        /// <param name="cost">Called as <c>cost(lanes)</c>. Returns what the junction loses with those lanes, e.g. seconds of delay per hour.</param>
        /// <param name="allowed">Called as <c>allowed(lanes)</c>; false leaves those lanes out. Null allows all.</param>
        public static LaneUse[] ChooseBy(LaneUse[] current, float[] volumes, bool[] far, bool[] straight, int[] receiving, bool signalled,
            Func<LaneUse[], float> cost, float minSaving, out float currentCost, out float bestCost, Predicate<LaneUse[]> allowed = null)
        {
            currentCost = cost(current);
            LaneUse[] best = current;
            bestCost = currentCost;
            foreach (LaneUse[] candidate in Assignments(current.Length, receiving))
            {
                if (Load(candidate, volumes, far, straight, signalled) == float.MaxValue || (allowed != null && !allowed(candidate)))
                    continue;
                float c = cost(candidate);
                if (c < bestCost * 0.999f || (c <= bestCost * 1.001f && Rank(candidate, current) < Rank(best, current)))
                {
                    best = candidate;
                    bestCost = c;
                }
            }
            if (currentCost - bestCost < minSaving)
            {
                bestCost = currentCost;
                return current;
            }
            return best;
        }

        /// <summary>Whether every lane of <paramref name="lanes"/> serves only movements it serves in <paramref name="current"/>: lanes that only lose directions.</summary>
        public static bool OnlyRemoves(LaneUse[] lanes, LaneUse[] current)
        {
            for (int j = 0; j < lanes.Length; j++)
            {
                if (lanes[j].First < current[j].First || lanes[j].Last > current[j].Last)
                    return false;
            }
            return true;
        }

        /// <summary>Order among equally loaded choices: fewer shared lanes first, then fewer lanes changed.</summary>
        private static int Rank(LaneUse[] lanes, LaneUse[] current)
        {
            int shared = 0;
            int changed = 0;
            for (int j = 0; j < lanes.Length; j++)
            {
                shared += lanes[j].Shared ? 1 : 0;
                changed += j < current.Length && lanes[j].Equals(current[j]) ? 0 : 1;
            }
            return shared * 100 + changed;
        }
    }
}
