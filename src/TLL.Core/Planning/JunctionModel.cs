using System;
using System.Collections.Generic;

namespace TLL.Core.Planning
{
    /// <summary>
    /// Everything the phase planner needs to know about one junction.
    /// The mod fills it from the game's lanes; tests fill it from
    /// <see cref="ChordModel"/>.
    /// </summary>
    public sealed class JunctionModel
    {
        public int ApproachCount;

        /// <summary>For each approach, the approach straight across, or -1.</summary>
        public int[] OppositeOf;

        public bool LeftHandTraffic;

        public readonly List<Movement> Movements = new List<Movement>();

        public ConflictMatrix Conflicts;

        /// <summary>
        /// For each movement, the movements whose vehicles queue in the same
        /// approach lane, as a bit mask; null when no lane is shared. The
        /// first vehicle of a shared lane holds up all behind it, whichever
        /// way they go, so such movements need their green together.
        /// </summary>
        public ulong[] SharedLane;

        public ulong SharesLaneWith(int movement)
        {
            return SharedLane != null && movement < SharedLane.Length ? SharedLane[movement] : 0UL;
        }

        /// <summary>Records that two movements are fed by the same approach lane.</summary>
        public void ShareLane(int a, int b)
        {
            if (a == b)
                return;
            if (SharedLane == null || SharedLane.Length < Movements.Count)
            {
                var grown = new ulong[Movements.Count];
                if (SharedLane != null)
                    Array.Copy(SharedLane, grown, SharedLane.Length);
                SharedLane = grown;
            }
            SharedLane[a] |= 1UL << b;
            SharedLane[b] |= 1UL << a;
        }

        public int IndexOf(int source, int target, MovementKind kind)
        {
            for (int i = 0; i < Movements.Count; i++)
            {
                Movement m = Movements[i];
                if (m.Source == source && m.Target == target && m.Kind == kind)
                    return i;
            }
            return -1;
        }

        public int IndexOfCrosswalk(int approach)
        {
            return IndexOf(approach, -1, MovementKind.Pedestrian);
        }

        /// <summary>
        /// The junction as it is once the movements in <paramref name="removed"/>
        /// are forbidden: without them, their conflicts and their share in
        /// the approach lanes. Lanes they shared with other movements no
        /// longer tie those together through them.
        /// </summary>
        /// <param name="kept">For each movement of the result, its index here.</param>
        public JunctionModel Without(ulong removed, out int[] kept)
        {
            var indices = new List<int>();
            for (int i = 0; i < Movements.Count; i++)
            {
                if ((removed & (1UL << i)) == 0)
                    indices.Add(i);
            }
            kept = indices.ToArray();
            var result = new JunctionModel
            {
                ApproachCount = ApproachCount,
                OppositeOf = OppositeOf,
                LeftHandTraffic = LeftHandTraffic,
                Conflicts = new ConflictMatrix(kept.Length),
            };
            foreach (int i in kept)
                result.Movements.Add(Movements[i]);
            for (int a = 0; a < kept.Length; a++)
            {
                for (int b = a + 1; b < kept.Length; b++)
                {
                    if (Conflicts != null)
                        result.Conflicts.Set(a, b, Conflicts.Get(kept[a], kept[b]));
                    if ((SharesLaneWith(kept[a]) & (1UL << kept[b])) != 0)
                        result.ShareLane(a, b);
                }
            }
            return result;
        }

        /// <summary>
        /// The junction with other lanes on one approach: each of
        /// <paramref name="movements"/> (model indices, in the order of
        /// <paramref name="lanes"/>) runs on the lanes that serve it, and two
        /// of them share a lane where one lane serves both. Ties to other
        /// movements of the approach, such as a tram on a car lane, stay.
        /// </summary>
        public JunctionModel WithLanes(int[] movements, LaneUse[] lanes)
        {
            var result = new JunctionModel
            {
                ApproachCount = ApproachCount,
                OppositeOf = OppositeOf,
                LeftHandTraffic = LeftHandTraffic,
                Conflicts = Conflicts,
            };
            for (int i = 0; i < Movements.Count; i++)
                result.Movements.Add(Movements[i]);
            if (SharedLane != null)
                result.SharedLane = (ulong[])SharedLane.Clone();
            for (int a = 0; a < movements.Length; a++)
            {
                int count = 0;
                foreach (LaneUse lane in lanes)
                    count += lane.Serves(a) ? 1 : 0;
                Movement m = result.Movements[movements[a]];
                m.LaneCount = Math.Max(1, count);
                result.Movements[movements[a]] = m;
                for (int b = 0; b < movements.Length; b++)
                {
                    if (a == b)
                        continue;
                    bool shared = false;
                    foreach (LaneUse lane in lanes)
                        shared |= lane.Serves(a) && lane.Serves(b);
                    if (result.SharedLane != null)
                        result.SharedLane[movements[a]] &= ~(1UL << movements[b]);
                    if (shared)
                        result.ShareLane(movements[a], movements[b]);
                }
            }
            return result;
        }

        /// <summary>The values of the movements a <see cref="Without"/> kept, in its order.</summary>
        public static float[] Select(float[] values, int[] kept)
        {
            if (values == null)
                return null;
            var result = new float[kept.Length];
            for (int i = 0; i < kept.Length; i++)
                result[i] = kept[i] < values.Length ? values[kept[i]] : 0f;
            return result;
        }
    }
}
