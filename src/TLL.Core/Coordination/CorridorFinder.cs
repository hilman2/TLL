using System.Collections.Generic;

namespace TLL.Core.Coordination
{
    /// <summary>
    /// A road connection between two signalled junctions, possibly through
    /// junctions without signals. Each end names the approach of the
    /// junction the road arrives at.
    /// </summary>
    public struct SignalLink
    {
        public int A;
        public int ApproachA;
        public int B;
        public int ApproachB;

        /// <summary>Metres along the road.</summary>
        public float Length;

        /// <summary>Speed limit in m/s.</summary>
        public float Speed;

        /// <summary>Importance, e.g. lane count or traffic. Heavier links are coordinated first.</summary>
        public float Weight;

        /// <summary>Car lanes arriving at junction A from the road; likewise <see cref="LanesToB"/>.</summary>
        public int LanesToA;

        public int LanesToB;

        /// <summary>The road is too short for the queue of a red (<see cref="Clusters.Join"/>): its junctions run as a cluster.</summary>
        public bool Tight;
    }

    /// <summary>The signalled junctions of a city and the roads between them.</summary>
    public sealed class SignalNetwork
    {
        /// <summary>For each junction, the approach straight across from each approach, or -1.</summary>
        public readonly List<int[]> OppositeOf = new List<int[]>();

        public readonly List<SignalLink> Links = new List<SignalLink>();

        public int JunctionCount => OppositeOf.Count;
    }

    /// <summary>
    /// One corridor: junctions in order along a road, with the approaches
    /// the road uses at each of them and the links in between.
    /// </summary>
    public sealed class CorridorPath
    {
        public readonly List<int> Junctions = new List<int>();

        /// <summary>Approach towards the previous junction, -1 at the first.</summary>
        public readonly List<int> ApproachBack = new List<int>();

        /// <summary>Approach towards the next junction, -1 at the last.</summary>
        public readonly List<int> ApproachAhead = new List<int>();

        /// <summary>Link i joins junction i and i + 1.</summary>
        public readonly List<SignalLink> Links = new List<SignalLink>();
    }

    /// <summary>
    /// Finds corridors worth coordinating: chains of signalled junctions
    /// along one road, where traffic goes straight through each junction.
    ///
    /// Greedy by weight, roads too short for a red (<see cref="SignalLink.Tight"/>)
    /// before all others: the heaviest free link starts a corridor, which then
    /// grows at both ends as long as the road continues straight across the
    /// end junction to another free junction within reach. A junction ends
    /// up in at most one corridor; where two main roads cross, the heavier
    /// one gets the junction.
    /// </summary>
    public static class CorridorFinder
    {
        public static List<CorridorPath> Find(SignalNetwork network, float maxSpacing)
        {
            var links = new List<SignalLink>();
            foreach (SignalLink l in network.Links)
            {
                if (l.Length <= maxSpacing && l.A != l.B)
                    links.Add(l);
            }
            // Roads too short for a red come first: where a cluster and a
            // green wave across it want the same junction, the cluster gets
            // it. Without it the short road backs up into its junctions; the
            // wave only loses its band there.
            links.Sort((x, y) => x.Tight != y.Tight ? y.Tight.CompareTo(x.Tight) : y.Weight.CompareTo(x.Weight));

            var used = new bool[network.JunctionCount];
            var result = new List<CorridorPath>();
            foreach (SignalLink seed in links)
            {
                if (used[seed.A] || used[seed.B])
                    continue;
                var path = new CorridorPath();
                path.Junctions.Add(seed.A);
                path.Junctions.Add(seed.B);
                path.ApproachBack.Add(-1);
                path.ApproachBack.Add(seed.ApproachB);
                path.ApproachAhead.Add(seed.ApproachA);
                path.ApproachAhead.Add(-1);
                path.Links.Add(seed);
                used[seed.A] = true;
                used[seed.B] = true;

                while (ExtendAhead(network, links, used, path))
                {
                }
                Reverse(path);
                while (ExtendAhead(network, links, used, path))
                {
                }
                result.Add(path);
            }
            return result;
        }

        /// <summary>Adds one junction beyond the last one, if the road goes straight on to a free junction.</summary>
        private static bool ExtendAhead(SignalNetwork network, List<SignalLink> links, bool[] used, CorridorPath path)
        {
            int last = path.Junctions.Count - 1;
            int end = path.Junctions[last];
            int arrivedBy = path.ApproachBack[last];
            int[] opposite = network.OppositeOf[end];
            int straightOn = arrivedBy >= 0 && arrivedBy < opposite.Length ? opposite[arrivedBy] : -1;
            if (straightOn < 0)
                return false;

            foreach (SignalLink l in links)
            {
                SignalLink link = l;
                if (link.B == end && link.ApproachB == straightOn)
                    link = Flip(link);
                if (link.A != end || link.ApproachA != straightOn || used[link.B])
                    continue;
                path.ApproachAhead[last] = straightOn;
                path.Junctions.Add(link.B);
                path.ApproachBack.Add(link.ApproachB);
                path.ApproachAhead.Add(-1);
                path.Links.Add(link);
                used[link.B] = true;
                return true;
            }
            return false;
        }

        private static void Reverse(CorridorPath path)
        {
            path.Junctions.Reverse();
            path.ApproachBack.Reverse();
            path.ApproachAhead.Reverse();
            // Back and ahead swap roles when the direction turns around.
            for (int i = 0; i < path.Junctions.Count; i++)
            {
                int back = path.ApproachBack[i];
                path.ApproachBack[i] = path.ApproachAhead[i];
                path.ApproachAhead[i] = back;
            }
            path.Links.Reverse();
            for (int i = 0; i < path.Links.Count; i++)
                path.Links[i] = Flip(path.Links[i]);
        }

        private static SignalLink Flip(SignalLink l)
        {
            return new SignalLink
            {
                A = l.B,
                ApproachA = l.ApproachB,
                B = l.A,
                ApproachB = l.ApproachA,
                Length = l.Length,
                Speed = l.Speed,
                Weight = l.Weight,
                LanesToA = l.LanesToB,
                LanesToB = l.LanesToA,
                Tight = l.Tight,
            };
        }
    }
}
