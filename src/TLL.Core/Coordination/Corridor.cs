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
    /// Traffic one junction of a cluster lets into the road towards its
    /// neighbour (<see cref="Clusters"/>): a movement with its green window,
    /// the drive to the neighbour's stop line, and how long the neighbour's
    /// green must have run by then.
    /// </summary>
    public struct Feed
    {
        /// <summary>Junction the traffic leaves, as an index into the corridor.</summary>
        public int From;

        /// <summary>Junction it drives to: From + 1 in direction A, From − 1 in direction B.</summary>
        public int To;

        /// <summary>Green window of the movement at <see cref="From"/>, relative to that junction's cycle start.</summary>
        public int WindowStart;

        public int WindowLength;

        /// <summary>Steps from the stop line at <see cref="From"/> to the one at <see cref="To"/>.</summary>
        public int Travel;

        /// <summary>
        /// Steps the through green at <see cref="To"/> must have run when the
        /// traffic arrives (<see cref="Clusters.Lead"/>). 0 for the main
        /// road's through traffic.
        /// </summary>
        public int Lead;

        /// <summary>
        /// Weight of a step of this traffic arriving at red: the movement's
        /// share of the side traffic entering the road, or
        /// <see cref="Clusters.ThroughFeedWeight"/> for the through traffic.
        /// </summary>
        public float Weight;
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

        /// <summary>
        /// Traffic let into the roads of a cluster. The offsets are chosen so
        /// that it reaches green at the next junction; each step it would
        /// arrive at red costs <see cref="FeedWeight"/> steps of green band.
        /// Empty for a corridor without roads too short for a red: its
        /// offsets then only widen the band.
        /// </summary>
        public readonly List<Feed> Feeds = new List<Feed>();

        /// <summary>
        /// Worth of a step of fed traffic arriving at green, in steps of
        /// band. In a cluster a vehicle stopped halfway fills a road that has
        /// no room to spare, which costs more than a narrower band.
        /// </summary>
        public float FeedWeight = 2f;

        public int Count => Junctions.Count;
    }
}
