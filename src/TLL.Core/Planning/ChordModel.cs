using System;
using System.Collections.Generic;

namespace TLL.Core.Planning
{
    /// <summary>
    /// Builds a <see cref="JunctionModel"/> from nothing but the directions of
    /// the approaches.
    ///
    /// The junction is treated as a circle. Every approach puts two points on
    /// it, one where traffic enters and one where it leaves, side by side in
    /// the order the driving side dictates. A movement is the chord from its
    /// entry point to its exit point. Two movements cross exactly when their
    /// chords cross, and merge when they share the exit point. A crosswalk is
    /// a short chord spanning both points of its approach.
    ///
    /// This is exact for the topology of an ordinary junction and ignores the
    /// real lane shapes. The mod uses the game's lane overlaps where it has
    /// them; this model serves tests and junctions without overlap data.
    /// </summary>
    public static class ChordModel
    {
        /// <param name="anglesDeg">
        /// Direction from the junction centre out along each approach, in
        /// degrees, counter-clockwise. Approach i gets index i.
        /// </param>
        public static JunctionModel Build(float[] anglesDeg, bool leftHandTraffic, bool uTurns = false, bool crosswalks = true)
        {
            int n = anglesDeg.Length;
            var model = new JunctionModel
            {
                ApproachCount = n,
                LeftHandTraffic = leftHandTraffic,
                OppositeOf = FindOpposites(anglesDeg),
            };

            for (int s = 0; s < n; s++)
            {
                for (int t = 0; t < n; t++)
                {
                    if (s == t && !uTurns)
                        continue;
                    model.Movements.Add(new Movement(s, t, KindOf(anglesDeg[s], anglesDeg[t], s == t)));
                }
            }
            if (crosswalks)
            {
                for (int a = 0; a < n; a++)
                    model.Movements.Add(new Movement(a, -1, MovementKind.Pedestrian));
            }

            model.Conflicts = Classify(model.Movements, anglesDeg, model.OppositeOf, leftHandTraffic);
            return model;
        }

        /// <summary>
        /// Relations between the given movements as the circle model sees
        /// them. The mod combines this with the game's lane overlaps, so a
        /// conflict the overlaps miss still keeps two movements apart.
        /// </summary>
        /// <param name="includeMerges">
        /// Whether movements into the same road count as merging. The model
        /// only knows roads, not lanes: on a road with several lanes, a turn
        /// often gets a lane of its own and meets nobody. Crossings, in
        /// contrast, are certain on road level. The mod therefore takes only
        /// crossings from here and merges from the game's lanes.
        /// </param>
        public static ConflictMatrix Classify(IList<Movement> movements, float[] anglesDeg, int[] oppositeOf, bool leftHandTraffic, bool includeMerges = true)
        {
            float epsilon = SmallestGap(anglesDeg) / 8f;
            // Side of the arm axis where inbound traffic drives: counter-clockwise
            // of the axis in right-hand traffic, clockwise in left-hand traffic.
            float side = leftHandTraffic ? -1f : 1f;
            int count = movements.Count;
            var from = new float[count];
            var to = new float[count];
            for (int i = 0; i < count; i++)
            {
                Movement m = movements[i];
                if (m.IsPedestrian)
                {
                    from[i] = Normalize(anglesDeg[m.Source] - 2f * epsilon);
                    to[i] = Normalize(anglesDeg[m.Source] + 2f * epsilon);
                }
                else
                {
                    from[i] = Normalize(anglesDeg[m.Source] + side * epsilon);
                    to[i] = Normalize(anglesDeg[m.Target] - side * epsilon);
                }
            }

            var conflicts = new ConflictMatrix(count);
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    PathContact contact = Contact(movements[i], movements[j], from[i], to[i], from[j], to[j]);
                    if (contact == PathContact.Merge && !includeMerges)
                        contact = PathContact.None;
                    conflicts.Set(i, j, ConflictRules.Classify(movements[i], movements[j], contact, oppositeOf, leftHandTraffic));
                }
            }
            return conflicts;
        }

        /// <summary>
        /// Kind of the turn from approach <paramref name="source"/> to <paramref name="target"/>.
        /// A turn of less than 45 degrees either way counts as straight on.
        /// </summary>
        public static MovementKind KindOf(float sourceDeg, float targetDeg, bool sameApproach)
        {
            if (sameApproach)
                return MovementKind.UTurn;
            // Inbound traffic heads towards the centre, opposite to its arm.
            float turn = Normalize(targetDeg - (sourceDeg + 180f));
            if (turn > 180f)
                turn -= 360f;
            if (Math.Abs(turn) < 45f)
                return MovementKind.Straight;
            return turn > 0f ? MovementKind.Left : MovementKind.Right;
        }

        private static PathContact Contact(Movement a, Movement b, float a1, float a2, float b1, float b2)
        {
            if (!a.IsPedestrian && !b.IsPedestrian)
            {
                if (a.Source == b.Source)
                    return PathContact.Diverge;
                if (a.Target == b.Target)
                    return PathContact.Merge;
            }
            bool in1 = InOpenArc(b1, a1, a2);
            bool in2 = InOpenArc(b2, a1, a2);
            return in1 != in2 ? PathContact.Cross : PathContact.None;
        }

        /// <summary>Whether <paramref name="q"/> lies strictly inside the counter-clockwise arc from p1 to p2.</summary>
        private static bool InOpenArc(float q, float p1, float p2)
        {
            float d = Normalize(q - p1);
            float span = Normalize(p2 - p1);
            return d > 0f && d < span;
        }

        /// <summary>
        /// Pairs each approach with the one most nearly opposite, if that is
        /// within 45 degrees of straight across and the choice is mutual.
        /// </summary>
        public static int[] FindOpposites(float[] anglesDeg)
        {
            int n = anglesDeg.Length;
            var best = new int[n];
            for (int i = 0; i < n; i++)
            {
                best[i] = -1;
                float bestError = 45f;
                for (int j = 0; j < n; j++)
                {
                    if (i == j)
                        continue;
                    float diff = Normalize(anglesDeg[j] - anglesDeg[i]);
                    float error = Math.Abs(diff - 180f);
                    if (error < bestError)
                    {
                        bestError = error;
                        best[i] = j;
                    }
                }
            }
            var result = new int[n];
            for (int i = 0; i < n; i++)
                result[i] = best[i] >= 0 && best[best[i]] == i ? best[i] : -1;
            return result;
        }

        /// <summary>Smallest angle between two approaches, in degrees.</summary>
        public static float SmallestGap(float[] anglesDeg)
        {
            float gap = 360f;
            for (int i = 0; i < anglesDeg.Length; i++)
            {
                for (int j = 0; j < anglesDeg.Length; j++)
                {
                    if (i == j)
                        continue;
                    float d = Normalize(anglesDeg[j] - anglesDeg[i]);
                    if (d > 0f && d < gap)
                        gap = d;
                }
            }
            return gap;
        }

        private static float Normalize(float deg)
        {
            deg %= 360f;
            return deg < 0f ? deg + 360f : deg;
        }
    }
}
