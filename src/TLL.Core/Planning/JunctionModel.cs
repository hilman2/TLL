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
    }
}
