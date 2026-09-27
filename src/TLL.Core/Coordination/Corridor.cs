using System.Collections.Generic;

namespace TLL.Core.Coordination
{
    /// <summary>
    /// A signalled junction on a corridor, seen from the corridor.
    ///
    /// Each direction has a green window, given relative to the junction's
    /// own cycle start: the corridor's through traffic in that direction has
    /// green from <c>Start</c> for <c>Length</c> steps. If the through
    /// movement is green in several consecutive phases, the window spans
    /// them including the intergreens between them.
    /// </summary>
    public struct CorridorJunction
    {
        public int WindowStartA;
        public int WindowLengthA;
        public int WindowStartB;
        public int WindowLengthB;

        /// <summary>The offset of this junction is fixed by another corridor and must not change.</summary>
        public bool Locked;

        /// <summary>Offset to keep if <see cref="Locked"/> is set.</summary>
        public int LockedOffset;
    }

    /// <summary>
    /// A chain of signalled junctions along one road, all on the same cycle.
    /// Direction A runs from the first junction to the last, B back.
    /// </summary>
    public sealed class Corridor
    {
        public int Cycle;

        public readonly List<CorridorJunction> Junctions = new List<CorridorJunction>();

        /// <summary>
        /// Travel time in steps from junction i to i+1 (direction A), and from
        /// i+1 to i (direction B). One entry fewer than there are junctions.
        /// </summary>
        public readonly List<int> TravelA = new List<int>();

        public readonly List<int> TravelB = new List<int>();

        /// <summary>Relative importance of the directions, e.g. their traffic volumes.</summary>
        public float WeightA = 1f;

        public float WeightB = 1f;

        public int Count => Junctions.Count;
    }
}
