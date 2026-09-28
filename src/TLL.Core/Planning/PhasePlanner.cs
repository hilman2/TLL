using System;
using System.Collections.Generic;

namespace TLL.Core.Planning
{
    public enum PlanStrategy : byte
    {
        /// <summary>Oncoming traffic runs together, long turns give way. Fewest phases.</summary>
        Permissive,

        /// <summary>Long turns and U-turns get their own green instead of giving way to oncoming traffic.</summary>
        ProtectedTurns,

        /// <summary>Each approach runs alone.</summary>
        Split,

        /// <summary>
        /// Saves from before the scramble became a switch of every layout:
        /// <see cref="Permissive"/> with a scramble. <see cref="PhasePlanner.Build"/>
        /// plans it as such; the mod turns it into that when it loads a save.
        /// </summary>
        ExclusivePedestrian,
    }

    /// <summary>
    /// Builds a phase plan for a junction.
    ///
    /// The plan uses as few phases as the conflicts allow: the phases are a
    /// minimum colouring of the graph whose edges are the hard conflicts.
    /// Afterwards every phase is widened by all movements that fit, so a
    /// movement keeps its green across consecutive phases where it can.
    /// </summary>
    public static class PhasePlanner
    {
        /// <summary>The game stores signal groups in a 16-bit mask.</summary>
        public const int MaxPhases = 16;

        /// <summary>Search budget of the exact colouring before it settles for the best found.</summary>
        private const int ColouringBudget = 200000;

        /// <param name="weights">
        /// Per movement, the traffic it carries. With it, the busiest
        /// movements are grouped first, as long as that takes no more phases
        /// than the fewest possible. Without it, the phases follow the
        /// conflicts alone.
        /// </param>
        /// <param name="scramble">
        /// Pedestrians get a phase of their own, in all directions, and walk
        /// in no other: the vehicle phases are planned as if there were no
        /// crosswalks. No effect at a junction without crosswalks.
        /// </param>
        public static PhasePlan Build(JunctionModel junction, PlanStrategy strategy, float[] weights = null, bool scramble = false)
        {
            if (junction.Movements.Count > 64)
                throw new ArgumentException("A junction can have at most 64 movements.", nameof(junction));
            // The pedestrian scramble layout of old saves is the permissive
            // layout with a scramble.
            if (strategy == PlanStrategy.ExclusivePedestrian)
            {
                strategy = PlanStrategy.Permissive;
                scramble = true;
            }
            ulong crosswalks = scramble ? Crosswalks(junction) : 0UL;
            if (crosswalks != 0UL)
                return BuildScramble(junction, strategy, weights, crosswalks);

            ConflictMatrix conflicts = Adjust(junction, strategy);
            int n = junction.Movements.Count;

            int[] colour = MinimumColouring(conflicts, n);
            if (weights != null && weights.Length == n)
            {
                int[] byTraffic = TrafficColouring(junction, conflicts, n, weights);
                if (CountColours(byTraffic) <= CountColours(colour))
                    colour = byTraffic;
            }
            int colourCount = 0;
            for (int i = 0; i < n; i++)
                colourCount = Math.Max(colourCount, colour[i] + 1);

            var phases = new List<Phase>();
            for (int c = 0; c < colourCount; c++)
            {
                ulong members = 0;
                for (int i = 0; i < n; i++)
                {
                    if (colour[i] == c)
                        members |= 1UL << i;
                }
                phases.Add(new Phase { Green = members });
            }

            // What the junction allows once shared lanes are taken into
            // account, without what the strategy adds.
            ConflictMatrix physical = junction.Conflicts.Clone();
            ShareHardConflicts(junction, physical);
            for (int p = 0; p < phases.Count; p++)
            {
                Phase widened = Widen(phases[p], conflicts, n);
                widened.Green = AddFreeOverlaps(widened.Green, junction, physical);
                phases[p] = widened;
            }

            RemoveDuplicates(phases);
            SortPhases(phases, junction);

            var plan = new PhasePlan();
            foreach (Phase phase in phases)
            {
                Phase p = phase;
                p.Permitted = PermittedWithin(p.Green, conflicts, n);
                plan.Phases.Add(p);
            }
            if (plan.Phases.Count > MaxPhases)
                throw new InvalidOperationException($"Junction needs {plan.Phases.Count} phases, the game supports {MaxPhases}.");
            return plan;
        }

        /// <summary>
        /// Returns the relations the strategy works with. Strategies only ever
        /// add hard conflicts; they never allow what the geometry forbids.
        /// </summary>
        public static ConflictMatrix Adjust(JunctionModel junction, PlanStrategy strategy)
        {
            ConflictMatrix m = junction.Conflicts.Clone();
            int n = junction.Movements.Count;
            for (int a = 0; a < n; a++)
            {
                Movement ma = junction.Movements[a];
                for (int b = a + 1; b < n; b++)
                {
                    Movement mb = junction.Movements[b];
                    bool vehicles = !ma.IsPedestrian && !mb.IsPedestrian;
                    switch (strategy)
                    {
                        case PlanStrategy.ProtectedTurns:
                            // A turn from a lane shared with straight traffic
                            // takes that traffic along (see ShareHardConflicts):
                            // the whole approach then runs on its own.
                            if (vehicles && m.Get(a, b) != Relation.Compatible && m.Get(a, b) != Relation.Hard
                                && (IsLongTurn(ma, junction.LeftHandTraffic) || IsLongTurn(mb, junction.LeftHandTraffic)))
                                m.Set(a, b, Relation.Hard);
                            break;
                        case PlanStrategy.Split:
                            if (vehicles && ma.Source != mb.Source)
                                m.Set(a, b, Relation.Hard);
                            break;
                        case PlanStrategy.ExclusivePedestrian:
                            if (ma.IsPedestrian != mb.IsPedestrian)
                                m.Set(a, b, Relation.Hard);
                            break;
                    }
                }
            }
            ShareHardConflicts(junction, m);
            return m;
        }

        /// <summary>
        /// Movements from one lane move only together: the first vehicle
        /// decides for all behind it. So each takes on the hard conflicts of
        /// the others. With the same hard conflicts, widening a phase adds the
        /// partners of every movement in it, so they end up in the same
        /// phases. Giving way is not passed on; straight traffic sharing a
        /// lane with a turn that gives way still has plain green.
        /// </summary>
        private static void ShareHardConflicts(JunctionModel junction, ConflictMatrix m)
        {
            if (junction.SharedLane == null)
                return;
            int n = junction.Movements.Count;
            // Repeated until nothing changes, so the conflicts also travel
            // along chains of lanes (A shares with B, B with C).
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int a = 0; a < n; a++)
                {
                    ulong partners = junction.SharesLaneWith(a);
                    if (partners == 0)
                        continue;
                    for (int b = 0; b < n; b++)
                    {
                        if ((partners & (1UL << b)) == 0 || m.Get(a, b) == Relation.Hard)
                            continue;
                        for (int x = 0; x < n; x++)
                        {
                            if (x != a && x != b && m.Get(b, x) == Relation.Hard && m.Get(a, x) != Relation.Hard)
                            {
                                m.Set(a, x, Relation.Hard);
                                changed = true;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>The junction's crosswalks, as a mask over its movements.</summary>
        public static ulong Crosswalks(JunctionModel junction)
        {
            ulong crosswalks = 0UL;
            for (int i = 0; i < junction.Movements.Count; i++)
            {
                if (junction.Movements[i].IsPedestrian)
                    crosswalks |= 1UL << i;
            }
            return crosswalks;
        }

        /// <summary>
        /// A plan with a scramble: the vehicle phases of
        /// <paramref name="strategy"/> for the junction without its
        /// crosswalks, since vehicles and people never have green together,
        /// and one phase of all crosswalks at the end.
        /// </summary>
        private static PhasePlan BuildScramble(JunctionModel junction, PlanStrategy strategy, float[] weights, ulong crosswalks)
        {
            JunctionModel vehicles = junction.Without(crosswalks, out int[] kept);
            PhasePlan inner = Build(vehicles, strategy, JunctionModel.Select(weights, kept));
            var plan = new PhasePlan();
            foreach (Phase phase in inner.Phases)
                plan.Phases.Add(new Phase { Green = Expand(phase.Green, kept), Permitted = Expand(phase.Permitted, kept) });
            plan.Phases.Add(new Phase { Green = crosswalks });
            if (plan.Phases.Count > MaxPhases)
                throw new InvalidOperationException($"Junction needs {plan.Phases.Count} phases, the game supports {MaxPhases}.");
            return plan;
        }

        /// <summary>A mask over the movements of a reduced model (JunctionModel.Without), as a mask over the full model's.</summary>
        private static ulong Expand(ulong mask, int[] kept)
        {
            ulong result = 0UL;
            for (int i = 0; i < kept.Length; i++)
            {
                if ((mask & (1UL << i)) != 0UL)
                    result |= 1UL << kept[i];
            }
            return result;
        }

        /// <summary>
        /// The movements of <paramref name="green"/> that give way within it,
        /// by the junction's own relations: what a kept plan shows as a
        /// yield signal after the road layout changed under it.
        /// </summary>
        public static ulong PermittedIn(JunctionModel junction, ulong green)
        {
            return PermittedWithin(green, junction.Conflicts, junction.Movements.Count);
        }

        /// <summary>
        /// Movements that may turn on red while <paramref name="green"/> has
        /// green: short turns (right in right-hand traffic) that have red in
        /// this phase and meet nothing in it that they could not give way to.
        /// They then get the game's yield signal instead of red.
        /// </summary>
        /// <remarks>
        /// This uses the junction's geometric relations, not those a strategy
        /// tightened: split phasing forbids different approaches to share a
        /// green, but a right turn that only merges behind the one approach
        /// that has green can still go.
        /// </remarks>
        public static ulong TurnOnRed(JunctionModel junction, ulong green)
        {
            ConflictMatrix conflicts = junction.Conflicts;
            MovementKind shortTurn = junction.LeftHandTraffic ? MovementKind.Left : MovementKind.Right;
            ulong allowed = 0;
            // A phase of crosswalks only is there to keep turning vehicles
            // off them. They would only give way to the people, so the
            // relations below would let them turn.
            bool vehicles = false;
            for (int m = 0; m < junction.Movements.Count; m++)
                vehicles |= (green & (1UL << m)) != 0 && !junction.Movements[m].IsPedestrian;
            if (!vehicles)
                return 0;
            for (int m = 0; m < junction.Movements.Count; m++)
            {
                if ((green & (1UL << m)) != 0 || junction.Movements[m].Kind != shortTurn)
                    continue;
                bool fits = true;
                for (int other = 0; other < junction.Movements.Count && fits; other++)
                {
                    if ((green & (1UL << other)) == 0)
                        continue;
                    Relation r = conflicts.Get(m, other);
                    fits = r == Relation.Compatible || r == Relation.Yields;
                }
                if (fits)
                    allowed |= 1UL << m;
            }
            return allowed;
        }

        private static bool IsLongTurn(Movement m, bool leftHandTraffic)
        {
            if (m.Kind == MovementKind.UTurn)
                return true;
            return m.Kind == (leftHandTraffic ? MovementKind.Right : MovementKind.Left);
        }

        private static Phase Widen(Phase phase, ConflictMatrix conflicts, int n)
        {
            ulong green = phase.Green;
            for (int candidate = 0; candidate < n; candidate++)
            {
                if ((green & (1UL << candidate)) != 0)
                    continue;
                if (FitsInto(candidate, green, conflicts, n))
                    green |= 1UL << candidate;
            }
            return new Phase { Green = green };
        }

        /// <summary>
        /// Adds every movement that meets nothing at all in the phase, judged
        /// by the junction's geometry rather than the strategy. A strategy
        /// keeps apart movements that would have to give way to each other;
        /// it has no reason to hold a movement at red that crosses nobody,
        /// such as a right turn into its own lane while the cross street runs.
        /// </summary>
        /// <param name="physical">
        /// The junction's relations with the hard conflicts passed on along
        /// shared lanes, but none a strategy adds: a movement whose lane
        /// partner cannot run in the phase is not free either, since the
        /// partner at the front of the lane would block it.
        /// </param>
        private static ulong AddFreeOverlaps(ulong green, JunctionModel junction, ConflictMatrix physical)
        {
            int n = junction.Movements.Count;
            for (int candidate = 0; candidate < n; candidate++)
            {
                if ((green & (1UL << candidate)) != 0)
                    continue;
                bool free = true;
                for (int member = 0; member < n && free; member++)
                {
                    if ((green & (1UL << member)) != 0)
                        free = junction.Conflicts.Get(candidate, member) == Relation.Compatible
                            && physical.Get(candidate, member) != Relation.Hard;
                }
                if (free)
                    green |= 1UL << candidate;
            }
            return green;
        }

        private static bool FitsInto(int candidate, ulong green, ConflictMatrix conflicts, int n)
        {
            for (int member = 0; member < n; member++)
            {
                if ((green & (1UL << member)) != 0 && !conflicts.CanShare(candidate, member))
                    return false;
            }
            return true;
        }

        private static ulong PermittedWithin(ulong green, ConflictMatrix conflicts, int n)
        {
            ulong permitted = 0;
            for (int i = 0; i < n; i++)
            {
                if ((green & (1UL << i)) != 0 && conflicts.YieldsWithin(i, green))
                    permitted |= 1UL << i;
            }
            return permitted;
        }

        private static void RemoveDuplicates(List<Phase> phases)
        {
            // Widening can make one phase a subset of another. It then adds
            // nothing but a switch, so it goes.
            for (int i = phases.Count - 1; i >= 0; i--)
            {
                for (int j = 0; j < phases.Count; j++)
                {
                    if (i == j)
                        continue;
                    ulong a = phases[i].Green;
                    ulong b = phases[j].Green;
                    if ((a & ~b) == 0 && (a != b || j < i))
                    {
                        phases.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Puts the phases in the order a driver would expect: grouped by axis,
        /// protected turns ahead of the straight traffic of the same axis, and
        /// a pedestrian-only phase last.
        /// </summary>
        private static void SortPhases(List<Phase> phases, JunctionModel junction)
        {
            var keys = new Dictionary<ulong, int>();
            foreach (Phase p in phases)
                keys[p.Green] = SortKey(p, junction);
            phases.Sort((x, y) => keys[x.Green].CompareTo(keys[y.Green]));
        }

        private static int SortKey(Phase phase, JunctionModel junction)
        {
            int axis = int.MaxValue;
            bool hasStraight = false;
            for (int i = 0; i < junction.Movements.Count; i++)
            {
                if (!phase.Has(i))
                    continue;
                Movement m = junction.Movements[i];
                if (m.IsPedestrian)
                    continue;
                int a = m.Source;
                int opposite = junction.OppositeOf != null && a < junction.OppositeOf.Length ? junction.OppositeOf[a] : -1;
                if (opposite >= 0 && opposite < a)
                    a = opposite;
                if (m.Kind == MovementKind.Straight || m.Kind == MovementKind.Track)
                {
                    if (!hasStraight || a < axis)
                        axis = a;
                    hasStraight = true;
                }
                else if (!hasStraight && a < axis)
                {
                    axis = a;
                }
            }
            if (axis == int.MaxValue)
                return int.MaxValue;
            return axis * 2 + (hasStraight ? 1 : 0);
        }

        /// <summary>
        /// Exact minimum colouring by DSATUR branch and bound. Junction graphs
        /// are small, so the search normally finishes; if it runs out of budget
        /// the best colouring found so far is used.
        /// </summary>
        public static int[] MinimumColouring(ConflictMatrix conflicts, int n)
        {
            var best = Greedy(conflicts, n);
            int bestCount = CountColours(best);
            var current = new int[n];
            for (int i = 0; i < n; i++)
                current[i] = -1;
            int budget = ColouringBudget;
            Search(conflicts, n, current, 0, 0, ref best, ref bestCount, ref budget);
            return best;
        }

        private static void Search(ConflictMatrix conflicts, int n, int[] current, int coloured, int used,
            ref int[] best, ref int bestCount, ref int budget)
        {
            if (used >= bestCount || --budget < 0)
                return;
            if (coloured == n)
            {
                best = (int[])current.Clone();
                bestCount = used;
                return;
            }

            int v = PickVertex(conflicts, n, current);
            for (int c = 0; c <= used && c < bestCount; c++)
            {
                if (!ColourFree(conflicts, n, current, v, c))
                    continue;
                current[v] = c;
                Search(conflicts, n, current, coloured + 1, Math.Max(used, c + 1), ref best, ref bestCount, ref budget);
                current[v] = -1;
            }
        }

        /// <summary>Uncoloured vertex with most distinct neighbour colours, ties by degree.</summary>
        private static int PickVertex(ConflictMatrix conflicts, int n, int[] colour)
        {
            int best = -1;
            int bestSaturation = -1;
            int bestDegree = -1;
            for (int v = 0; v < n; v++)
            {
                if (colour[v] >= 0)
                    continue;
                ulong seen = 0;
                int degree = 0;
                for (int u = 0; u < n; u++)
                {
                    if (u == v || conflicts.CanShare(u, v))
                        continue;
                    degree++;
                    if (colour[u] >= 0)
                        seen |= 1UL << colour[u];
                }
                int saturation = PopCount(seen);
                if (saturation > bestSaturation || (saturation == bestSaturation && degree > bestDegree))
                {
                    best = v;
                    bestSaturation = saturation;
                    bestDegree = degree;
                }
            }
            return best;
        }

        private static bool ColourFree(ConflictMatrix conflicts, int n, int[] colour, int v, int c)
        {
            for (int u = 0; u < n; u++)
            {
                if (colour[u] == c && !conflicts.CanShare(u, v))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// A colouring built from the traffic: vehicle movements by their
        /// volume, busiest first, then the crosswalks, then movements without
        /// traffic, each into the first phase it fits.
        /// </summary>
        /// <remarks>
        /// Among the colourings with the fewest phases, the search takes
        /// whichever it meets first. At a T whose main road bends, that kept
        /// the two directions of the main road apart, because a U-turn that
        /// nobody took sat in the phase of each; the busiest movements now
        /// claim their phases before the idle ones are fitted in.
        /// </remarks>
        private static int[] TrafficColouring(JunctionModel junction, ConflictMatrix conflicts, int n, float[] weights)
        {
            var order = new List<int>(n);
            for (int i = 0; i < n; i++)
                order.Add(i);
            order.Sort((a, b) =>
            {
                int rankA = Rank(junction, weights, a);
                int rankB = Rank(junction, weights, b);
                if (rankA != rankB)
                    return rankA.CompareTo(rankB);
                int byVolume = weights[b].CompareTo(weights[a]);
                return byVolume != 0 ? byVolume : a.CompareTo(b);
            });
            var colour = new int[n];
            for (int i = 0; i < n; i++)
                colour[i] = -1;
            foreach (int v in order)
            {
                int c = 0;
                while (!ColourFree(conflicts, n, colour, v, c))
                    c++;
                colour[v] = c;
            }
            return colour;
        }

        /// <summary>Order of placement: 0 vehicles with traffic, 1 crosswalks, 2 vehicles without.</summary>
        private static int Rank(JunctionModel junction, float[] weights, int movement)
        {
            if (junction.Movements[movement].IsPedestrian)
                return 1;
            return weights[movement] > 0f ? 0 : 2;
        }

        private static int[] Greedy(ConflictMatrix conflicts, int n)
        {
            var colour = new int[n];
            for (int i = 0; i < n; i++)
                colour[i] = -1;
            for (int step = 0; step < n; step++)
            {
                int v = PickVertex(conflicts, n, colour);
                int c = 0;
                while (!ColourFree(conflicts, n, colour, v, c))
                    c++;
                colour[v] = c;
            }
            return colour;
        }

        private static int CountColours(int[] colour)
        {
            int max = -1;
            foreach (int c in colour)
                max = Math.Max(max, c);
            return max + 1;
        }

        private static int PopCount(ulong bits)
        {
            int count = 0;
            while (bits != 0)
            {
                bits &= bits - 1;
                count++;
            }
            return count;
        }
    }
}
