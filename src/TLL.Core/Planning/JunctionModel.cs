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
