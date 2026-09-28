using System;
using System.Collections.Generic;

namespace TLL.Core.Planning
{
    /// <summary>What became of a plan carried over to a junction.</summary>
    public sealed class TransferResult
    {
        /// <summary>The plan on the new junction, as masks over its movements.</summary>
        public readonly List<ulong> Phases = new List<ulong>();

        /// <summary>For each phase of <see cref="Phases"/>, the phase of the old plan it comes from, or -1 for a new one.</summary>
        public readonly List<int> Origin = new List<int>();

        /// <summary>Movements the old plan did not know, placed where they fit (PlanEditing.Place).</summary>
        public ulong Added;

        /// <summary>
        /// Movements taken out of a phase: their path now crosses another
        /// movement there, or their lane partners changed. Each still has
        /// green somewhere, or is also in <see cref="Added"/>.
        /// </summary>
        public ulong Moved;

        /// <summary>Movements of the old plan the junction does not have, by their old index.</summary>
        public readonly List<int> Dropped = new List<int>();

        /// <summary>Movements that got no place because the plan was full. Empty for every usable result.</summary>
        public ulong Unplaced;

        /// <summary>Whether the plan came over unchanged.</summary>
        public bool Unchanged => Added == 0UL && Moved == 0UL && Dropped.Count == 0 && Unplaced == 0UL && !PhasesDropped;

        /// <summary>A phase of the old plan was left with nothing and went.</summary>
        public bool PhasesDropped;
    }

    /// <summary>
    /// Carries a phase plan from one junction to another of the same shape,
    /// or to the same junction after its roads changed: for presets, for
    /// copy and paste, and for keeping the player's plan when a road is
    /// rebuilt.
    ///
    /// Junctions are compared by their arms, the roads meeting there, in
    /// counter-clockwise order with their directions. An arm map says which
    /// arm of the new junction each arm of the old one became; movements
    /// are then matched by their arms and kind.
    /// </summary>
    public static class PlanTransfer
    {
        /// <summary>
        /// Most degrees an arm may point differently, relative to the others,
        /// for two junctions to count as the same shape.
        /// </summary>
        public const float AngleTolerance = 35f;

        /// <summary>
        /// Every way the arms of junction <paramref name="from"/> can be laid
        /// onto those of <paramref name="to"/> by turning it, keeping their
        /// order around the junction, with no arm off by more than
        /// <paramref name="tolerance"/> degrees. Each map gives, for arm i of
        /// <paramref name="from"/>, the arm of <paramref name="to"/>. The best
        /// fit comes first. Empty where the number of arms differs or no
        /// turn fits.
        /// </summary>
        /// <param name="from">Directions of the arms in degrees, counter-clockwise, in any order.</param>
        public static List<int[]> Rotations(float[] from, float[] to, float tolerance = AngleTolerance)
        {
            var result = new List<int[]>();
            var deviations = new List<float>();
            int n = from.Length;
            if (n == 0 || n != to.Length)
                return result;
            int[] fromOrder = CounterClockwise(from);
            int[] toOrder = CounterClockwise(to);
            for (int r = 0; r < n; r++)
            {
                float worst = 0f;
                var map = new int[n];
                for (int k = 0; k < n; k++)
                {
                    int a = fromOrder[k];
                    int b = toOrder[(k + r) % n];
                    float fromRelative = Normalize(from[a] - from[fromOrder[0]]);
                    float toRelative = Normalize(to[b] - to[toOrder[r]]);
                    worst = Math.Max(worst, Math.Abs(Signed(toRelative - fromRelative)));
                    map[a] = b;
                }
                if (worst > tolerance)
                    continue;
                int at = deviations.Count;
                while (at > 0 && deviations[at - 1] > worst)
                    at--;
                deviations.Insert(at, worst);
                result.Insert(at, map);
            }
            return result;
        }

        /// <summary>
        /// Which arm each arm of a junction became after its roads changed.
        /// Arms known by their road (<paramref name="known"/>) keep that
        /// match. The others are matched to the remaining new arms by
        /// direction, corrected by how far the known arms turned, the closest
        /// pairs first, within <see cref="AngleTolerance"/>.
        /// </summary>
        /// <param name="known">For each old arm, the new arm with the same road, or -1.</param>
        /// <returns>For each old arm the new arm, or -1 where it is gone.</returns>
        public static int[] MatchArms(float[] oldAngles, float[] newAngles, int[] known)
        {
            int n = oldAngles.Length;
            var map = new int[n];
            var taken = new bool[newAngles.Length];
            float turnSum = 0f;
            int turnCount = 0;
            for (int i = 0; i < n; i++)
            {
                map[i] = known != null && i < known.Length ? known[i] : -1;
                if (map[i] >= 0 && map[i] < newAngles.Length && !taken[map[i]])
                {
                    taken[map[i]] = true;
                    turnSum += Signed(newAngles[map[i]] - oldAngles[i]);
                    turnCount++;
                }
                else
                {
                    map[i] = -1;
                }
            }
            float turn = turnCount > 0 ? turnSum / turnCount : 0f;
            while (true)
            {
                int bestOld = -1;
                int bestNew = -1;
                float best = AngleTolerance;
                for (int i = 0; i < n; i++)
                {
                    if (map[i] >= 0)
                        continue;
                    for (int j = 0; j < newAngles.Length; j++)
                    {
                        if (taken[j])
                            continue;
                        float d = Math.Abs(Signed(newAngles[j] - oldAngles[i] - turn));
                        if (d <= best)
                        {
                            best = d;
                            bestOld = i;
                            bestNew = j;
                        }
                    }
                }
                if (bestOld < 0)
                    break;
                map[bestOld] = bestNew;
                taken[bestNew] = true;
            }
            return map;
        }

        /// <summary>
        /// The plan <paramref name="phases"/> of a junction with
        /// <paramref name="movements"/>, carried over to <paramref name="target"/>
        /// through <paramref name="armMap"/>. The result keeps to the target's
        /// geometry: movements whose paths now cross leave the phase, lane
        /// partners share their greens, and every movement of the target gets
        /// green somewhere where the plan has room.
        /// </summary>
        /// <param name="movements">The old movements, their Source and Target as old arm indices.</param>
        /// <param name="armMap">For each old arm, the arm of the target, or -1 where it is gone.</param>
        public static TransferResult Transfer(IList<Movement> movements, IList<ulong> phases, int[] armMap, JunctionModel target)
        {
            var result = new TransferResult();
            int n = target.Movements.Count;
            var toNew = new int[movements.Count];
            ulong used = 0UL;
            for (int i = 0; i < movements.Count; i++)
            {
                toNew[i] = Find(movements[i], armMap, target, used);
                if (toNew[i] < 0)
                    result.Dropped.Add(i);
                else
                    used |= 1UL << toNew[i];
            }

            for (int p = 0; p < phases.Count; p++)
            {
                ulong green = 0UL;
                for (int i = 0; i < movements.Count && i < 64; i++)
                {
                    if ((phases[p] & (1UL << i)) != 0UL && toNew[i] >= 0)
                        green |= 1UL << toNew[i];
                }
                result.Phases.Add(green);
                result.Origin.Add(p);
            }

            result.Moved |= RemoveCrossings(target, result.Phases);
            result.Moved |= JoinPartners(target, result.Phases);

            for (int p = result.Phases.Count - 1; p >= 0; p--)
            {
                if (result.Phases[p] != 0UL)
                    continue;
                if (phases[result.Origin[p]] != 0UL)
                    result.PhasesDropped = true;
                result.Phases.RemoveAt(p);
                result.Origin.RemoveAt(p);
            }

            for (int m = 0; m < n; m++)
            {
                if (Covered(result.Phases, m))
                    continue;
                ulong group = PlanEditing.Partners(target, m) | (1UL << m);
                if (PlanEditing.Place(target, result.Phases, m) == 0U)
                {
                    result.Unplaced |= group;
                    continue;
                }
                while (result.Origin.Count < result.Phases.Count)
                    result.Origin.Add(-1);
                // A movement the old plan served, but in a phase it had to
                // leave, is moved rather than new.
                result.Added |= group & ~used;
                result.Moved |= group & used;
            }
            return result;
        }

        /// <summary>
        /// The arms and movements of a junction as a city driving on the
        /// other side sees it: mirrored, so left turns become right turns and
        /// the arms run the other way round. For presets made in a city that
        /// drives on the other side.
        /// </summary>
        public static float[] Mirror(float[] angles, IList<Movement> movements)
        {
            var mirrored = new float[angles.Length];
            for (int i = 0; i < angles.Length; i++)
                mirrored[i] = Normalize(-angles[i]);
            for (int i = 0; i < movements.Count; i++)
            {
                Movement m = movements[i];
                if (m.Kind == MovementKind.Left)
                    m.Kind = MovementKind.Right;
                else if (m.Kind == MovementKind.Right)
                    m.Kind = MovementKind.Left;
                movements[i] = m;
            }
            return mirrored;
        }

        /// <summary>
        /// The target's movement for an old one: same arms and kind; for a
        /// vehicle movement whose kind changed with the angles (straight on
        /// that became a gentle left), same arms and any vehicle kind. -1
        /// where there is none, or it is taken.
        /// </summary>
        private static int Find(Movement old, int[] armMap, JunctionModel target, ulong used)
        {
            int source = old.Source >= 0 && old.Source < armMap.Length ? armMap[old.Source] : -1;
            int destination = old.Target < 0 ? -1 : old.Target < armMap.Length ? armMap[old.Target] : -1;
            if (source < 0 || (old.Target >= 0 && destination < 0))
                return -1;
            int fallback = -1;
            for (int m = 0; m < target.Movements.Count; m++)
            {
                Movement t = target.Movements[m];
                if ((used & (1UL << m)) != 0UL || t.Source != source || t.Target != destination)
                    continue;
                if (t.Kind == old.Kind)
                    return m;
                if (IsRoadVehicle(t.Kind) && IsRoadVehicle(old.Kind) && fallback < 0)
                    fallback = m;
            }
            return fallback;
        }

        private static bool IsRoadVehicle(MovementKind kind)
        {
            return kind == MovementKind.Straight || kind == MovementKind.Left || kind == MovementKind.Right || kind == MovementKind.UTurn;
        }

        /// <summary>
        /// Takes movements out of phases where their path crosses another's.
        /// Of such a pair, the one with green in more phases leaves, since it
        /// keeps green elsewhere; on a tie the later one in the model.
        /// </summary>
        /// <returns>The movements taken out somewhere.</returns>
        private static ulong RemoveCrossings(JunctionModel model, List<ulong> phases)
        {
            ulong moved = 0UL;
            int n = model.Movements.Count;
            for (int p = 0; p < phases.Count; p++)
            {
                bool again = true;
                while (again)
                {
                    again = false;
                    for (int a = 0; a < n && !again; a++)
                    {
                        for (int b = a + 1; b < n && !again; b++)
                        {
                            if ((phases[p] & (1UL << a)) == 0UL || (phases[p] & (1UL << b)) == 0UL || model.Conflicts.CanShare(a, b))
                                continue;
                            int leaving = GreenCount(phases, a) > GreenCount(phases, b) ? a : b;
                            phases[p] &= ~(1UL << leaving);
                            moved |= 1UL << leaving;
                            again = true;
                        }
                    }
                }
            }
            return moved;
        }

        /// <summary>
        /// Gives the movements of each shared lane the same greens: every
        /// phase in which one of them has green gets all of them where they
        /// fit, and loses all of them where they do not.
        /// </summary>
        /// <returns>The movements whose greens changed.</returns>
        private static ulong JoinPartners(JunctionModel model, List<ulong> phases)
        {
            ulong moved = 0UL;
            ulong done = 0UL;
            for (int m = 0; m < model.Movements.Count; m++)
            {
                if ((done & (1UL << m)) != 0UL)
                    continue;
                ulong group = PlanEditing.Partners(model, m) | (1UL << m);
                done |= group;
                if (group == (1UL << m))
                    continue;
                for (int p = 0; p < phases.Count; p++)
                {
                    ulong inPhase = phases[p] & group;
                    if (inPhase == 0UL || inPhase == group)
                        continue;
                    ulong rest = phases[p] & ~group;
                    phases[p] = PlanEditing.Blocking(model, rest, group) == 0UL ? rest | group : rest;
                    moved |= group;
                }
            }
            return moved;
        }

        private static bool Covered(List<ulong> phases, int movement)
        {
            foreach (ulong p in phases)
            {
                if ((p & (1UL << movement)) != 0UL)
                    return true;
            }
            return false;
        }

        private static int GreenCount(List<ulong> phases, int movement)
        {
            int count = 0;
            foreach (ulong p in phases)
                count += (p & (1UL << movement)) != 0UL ? 1 : 0;
            return count;
        }

        /// <summary>The arm indices sorted by direction, counter-clockwise from 0 degrees.</summary>
        public static int[] CounterClockwise(float[] angles)
        {
            var order = new int[angles.Length];
            for (int i = 0; i < order.Length; i++)
                order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int byAngle = Normalize(angles[a]).CompareTo(Normalize(angles[b]));
                return byAngle != 0 ? byAngle : a.CompareTo(b);
            });
            return order;
        }

        private static float Normalize(float deg)
        {
            deg %= 360f;
            return deg < 0f ? deg + 360f : deg;
        }

        /// <summary>An angle in degrees as the shorter turn, from -180 to 180.</summary>
        private static float Signed(float deg)
        {
            deg = Normalize(deg);
            return deg > 180f ? deg - 360f : deg;
        }
    }
}
