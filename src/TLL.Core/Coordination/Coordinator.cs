using System;
using System.Collections.Generic;
using TLL.Core.Control;
using TLL.Core.Optimization;

namespace TLL.Core.Coordination
{
    /// <summary>One junction of a corridor, as the coordinator needs it.</summary>
    public sealed class CorridorMember
    {
        public PhaseData[] Phases;

        /// <summary>For each phase, bit i set: movement i has green.</summary>
        public ulong[] PhaseMovements;

        public int Intergreen;

        /// <summary>Movement of the corridor's through traffic in direction A (first to last junction), or -1.</summary>
        public int MovementA = -1;

        /// <summary>Same for direction B.</summary>
        public int MovementB = -1;

        /// <summary>Measured flow ratio per phase (see <see cref="SplitOptimizer.FlowRatios"/>), or null to keep the current proportions.</summary>
        public float[] Ratios;

        /// <summary>The cycle this junction would choose on its own, in steps, or 0 if unknown.</summary>
        public int DesiredCycle;

        /// <summary>
        /// Movements whose traffic drives into the road towards the next
        /// junction of the corridor (direction A), the through traffic among
        /// them; null or empty at the last junction. Only a road too short
        /// for a red (<see cref="SignalLink.Tight"/>) makes feeds of them.
        /// </summary>
        public int[] FeedsAhead;

        /// <summary>Likewise into the road towards the previous junction (direction B).</summary>
        public int[] FeedsBack;

        /// <summary>Traffic per movement, vehicles per hour, for each feed's share of its road; null weighs all feeds of a road alike.</summary>
        public float[] Volumes;
    }

    /// <summary>Timing for all junctions of a corridor.</summary>
    public sealed class CoordinationPlan
    {
        public int Cycle;

        /// <summary>New greens per member and phase.</summary>
        public ushort[][] Greens;

        /// <summary>Cycle start per member, in steps within the cycle.</summary>
        public int[] Offsets;

        /// <summary>Per member and phase: the phase carries the corridor's through traffic.</summary>
        public bool[][] Coordinated;

        public int BandwidthA;
        public int BandwidthB;

        /// <summary>Some road of the corridor is too short for a red: the corridor runs as a cluster, whatever its band.</summary>
        public bool Cluster;

        /// <summary>
        /// For a cluster, the steps per cycle its feeds reach the next
        /// junction at red (<see cref="GreenWave.FedIntoRed"/>), weighted by
        /// share and summed over its roads; 0 otherwise.
        /// </summary>
        public float FedIntoRed;
    }

    /// <summary>
    /// Puts the junctions of a corridor on one cycle and finds offsets for a
    /// green wave in both directions. Where a road of the corridor is too
    /// short for a red, the offsets also let the traffic entering that road
    /// meet green at its far end (<see cref="Clusters"/>).
    /// </summary>
    public static class Coordinator
    {
        /// <summary>Share of the speed limit that traffic actually drives between junctions.</summary>
        public const float SpeedFactor = 0.85f;

        public static CoordinationPlan Plan(CorridorPath path, IList<CorridorMember> members, OptimizerLimits limits)
        {
            int n = members.Count;
            if (n != path.Junctions.Count)
                throw new ArgumentException("One member per corridor junction expected.", nameof(members));

            // The busiest junction sets the cycle; at the others the longer
            // cycle is shared out over all phases by their flow ratios, which
            // widens the windows the green wave can use.
            int cycle = limits.MinCycle;
            foreach (CorridorMember m in members)
                cycle = Math.Max(cycle, m.DesiredCycle > 0 ? m.DesiredCycle : CurrentCycle(m));
            cycle = Math.Min(cycle, limits.MaxCycle);

            var greens = new ushort[n][];
            for (int pass = 0; pass < 2; pass++)
            {
                int needed = cycle;
                for (int i = 0; i < n; i++)
                {
                    SplitResult split = SplitOptimizer.Splits(members[i].Phases, RatiosOf(members[i]), cycle, members[i].Intergreen);
                    greens[i] = split.Green;
                    needed = Math.Max(needed, split.Cycle);
                }
                // A junction whose minimum greens do not fit forces a longer
                // cycle on the whole corridor; one more pass spreads it.
                if (needed == cycle)
                    break;
                cycle = needed;
            }

            var corridor = new Corridor { Cycle = cycle };
            var coordinated = new bool[n][];
            for (int i = 0; i < n; i++)
            {
                CorridorMember m = members[i];
                bool[] hasA = PhasesWith(m, m.MovementA);
                bool[] hasB = PhasesWith(m, m.MovementB);
                PhaseWindow a = m.MovementA >= 0 ? PhaseWindow.Of(greens[i], m.Intergreen, hasA) : new PhaseWindow { Length = cycle };
                PhaseWindow b = m.MovementB >= 0 ? PhaseWindow.Of(greens[i], m.Intergreen, hasB) : new PhaseWindow { Length = cycle };
                corridor.Junctions.Add(new CorridorJunction
                {
                    WindowStartA = a.Start,
                    WindowLengthA = a.Length,
                    WindowStartB = b.Start,
                    WindowLengthB = b.Length,
                });
                coordinated[i] = new bool[m.Phases.Length];
                for (int p = 0; p < m.Phases.Length; p++)
                    coordinated[i][p] = hasA[p] || hasB[p];
            }
            foreach (SignalLink link in path.Links)
            {
                int travel = SimTime.ToSteps(link.Length / Math.Max(1f, link.Speed * SpeedFactor));
                corridor.TravelA.Add(travel);
                corridor.TravelB.Add(travel);
            }
            bool cluster = false;
            for (int k = 0; k < path.Links.Count; k++)
            {
                if (!path.Links[k].Tight)
                    continue;
                cluster = true;
                AddFeeds(corridor, members, greens, k, k + 1, members[k].FeedsAhead, members[k].MovementA, corridor.TravelA[k], path.Links[k].Length);
                AddFeeds(corridor, members, greens, k + 1, k, members[k + 1].FeedsBack, members[k + 1].MovementB, corridor.TravelB[k], path.Links[k].Length);
            }

            int[] offsets = GreenWave.Optimize(corridor);
            return new CoordinationPlan
            {
                Cycle = cycle,
                Greens = greens,
                Offsets = offsets,
                Coordinated = coordinated,
                BandwidthA = GreenWave.BandwidthA(corridor, offsets),
                BandwidthB = GreenWave.BandwidthB(corridor, offsets),
                Cluster = cluster,
                FedIntoRed = cluster ? GreenWave.FedIntoRed(corridor, offsets) : 0f,
            };
        }

        /// <summary>
        /// The feeds of one road of a cluster in one direction: every
        /// movement at <paramref name="from"/> that drives into it, with its
        /// green window. The others, the side road's turns onto the main road
        /// above all, share a weight of 1 by their traffic and wait for the
        /// queue at the far end to move off. The through traffic arrives as
        /// that queue leaves, and weighs <see cref="Clusters.ThroughFeedWeight"/>.
        /// </summary>
        private static void AddFeeds(Corridor corridor, IList<CorridorMember> members, ushort[][] greens, int from, int to,
            int[] feeds, int through, int travel, float length)
        {
            if (feeds == null || feeds.Length == 0)
                return;
            CorridorMember m = members[from];
            float total = 0f;
            int others = 0;
            foreach (int movement in feeds)
            {
                if (movement == through)
                    continue;
                total += VolumeOf(m, movement);
                others++;
            }
            foreach (int movement in feeds)
            {
                PhaseWindow window = PhaseWindow.Of(greens[from], m.Intergreen, PhasesWith(m, movement));
                if (window.Length <= 0)
                    continue;
                float weight = movement == through ? Clusters.ThroughFeedWeight
                    : total > 0f ? VolumeOf(m, movement) / total : 1f / others;
                corridor.Feeds.Add(new Feed
                {
                    From = from,
                    To = to,
                    WindowStart = window.Start,
                    WindowLength = window.Length,
                    Travel = travel,
                    Lead = movement == through ? 0 : Clusters.Lead(length),
                    Weight = weight,
                });
            }
        }

        private static float VolumeOf(CorridorMember m, int movement)
        {
            return m.Volumes != null && movement >= 0 && movement < m.Volumes.Length ? Math.Max(0f, m.Volumes[movement]) : 0f;
        }

        private static int CurrentCycle(CorridorMember m)
        {
            int cycle = 0;
            foreach (PhaseData p in m.Phases)
                cycle += p.Green + m.Intergreen;
            return cycle;
        }

        /// <summary>The measured ratios, or the current greens as ratios when nothing was measured.</summary>
        private static float[] RatiosOf(CorridorMember m)
        {
            if (m.Ratios != null)
            {
                float sum = 0f;
                foreach (float r in m.Ratios)
                    sum += r;
                if (sum > 0f)
                    return m.Ratios;
            }
            var ratios = new float[m.Phases.Length];
            for (int p = 0; p < ratios.Length; p++)
                ratios[p] = m.Phases[p].Green;
            return ratios;
        }

        private static bool[] PhasesWith(CorridorMember m, int movement)
        {
            var has = new bool[m.Phases.Length];
            if (movement < 0)
                return has;
            for (int p = 0; p < has.Length; p++)
                has[p] = (m.PhaseMovements[p] & (1UL << movement)) != 0;
            return has;
        }
    }
}
