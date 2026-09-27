namespace TLL.Core.Planning
{
    /// <summary>
    /// Turns the geometric contact of two movements into a signal relation.
    ///
    /// The rules follow what the game already does at a vanilla junction:
    /// vanilla lets everything from two opposite approaches run together and
    /// relies on the lane priorities inside the junction to sort out who goes
    /// first. TLL keeps that for oncoming traffic, trams included, and only
    /// forbids what the game cannot resolve: paths from perpendicular
    /// approaches that cross, straight traffic over a crosswalk, and trams
    /// that would have to wait inside the junction (see ClassifyTram).
    /// </summary>
    public static class ConflictRules
    {
        /// <summary>
        /// Classifies the contact of movement <paramref name="a"/> with <paramref name="b"/>.
        /// </summary>
        /// <param name="oppositeOf">
        /// For each approach, the approach straight across the junction, or -1.
        /// Two movements are oncoming when their sources are opposite.
        /// </param>
        /// <param name="leftHandTraffic">
        /// Decides which turn is the short one. The long turn crosses oncoming
        /// traffic and gives way to it.
        /// </param>
        public static Relation Classify(Movement a, Movement b, PathContact contact, int[] oppositeOf, bool leftHandTraffic)
        {
            if (contact == PathContact.None || contact == PathContact.Diverge)
                return Relation.Compatible;

            if (a.IsPedestrian && b.IsPedestrian)
                return Relation.Compatible;

            if (a.IsPedestrian || b.IsPedestrian)
                return ClassifyPedestrian(a, b);

            if (a.Source == b.Source)
                return Relation.Compatible;

            bool oncoming = IsOpposite(a.Source, b.Source, oppositeOf);
            if (a.Kind == MovementKind.Track || b.Kind == MovementKind.Track)
                return ClassifyTram(a, b, contact, oppositeOf);

            int rankA = Rank(a.Kind, leftHandTraffic);
            int rankB = Rank(b.Kind, leftHandTraffic);

            if (contact == PathContact.Cross)
            {
                if (!oncoming)
                    return Relation.Hard;
                if (rankA == rankB)
                    return Relation.Compatible;
                return rankA < rankB ? Relation.Yields : Relation.HasPriority;
            }

            // Merge: both paths end in the same lane.
            if (rankA == rankB)
                return Relation.Hard;
            return rankA < rankB ? Relation.Yields : Relation.HasPriority;
        }

        /// <summary>
        /// A tram against another vehicle. Oncoming, the game sorts them out
        /// by lane priority as it does for cars, and so does TLL: whoever
        /// turns across the other's path gives way, a tram going straight
        /// has priority. Where both turn across each other, where paths
        /// cross from the side, and where they merge, they stay apart: a
        /// tram waiting inside the junction blocks everything around it.
        /// </summary>
        /// <remarks>
        /// Treating every tram contact as hard made a turn from a lane shared
        /// with straight traffic keep the whole approach from running with
        /// the oncoming one: on streets with trams, every layout became one
        /// approach at a time, and green waves had no band.
        /// </remarks>
        private static Relation ClassifyTram(Movement a, Movement b, PathContact contact, int[] oppositeOf)
        {
            if (contact != PathContact.Cross || !IsOpposite(a.Source, b.Source, oppositeOf))
                return Relation.Hard;
            bool aTurns = Turns(a, oppositeOf);
            bool bTurns = Turns(b, oppositeOf);
            if (aTurns == bTurns)
                return Relation.Hard;
            return aTurns ? Relation.Yields : Relation.HasPriority;
        }

        /// <summary>Whether a vehicle movement leaves by another road than the one straight across.</summary>
        private static bool Turns(Movement m, int[] oppositeOf)
        {
            if (m.Kind != MovementKind.Track)
                return m.Kind != MovementKind.Straight;
            return !IsOpposite(m.Source, m.Target, oppositeOf);
        }

        private static Relation ClassifyPedestrian(Movement a, Movement b)
        {
            bool aIsVehicle = !a.IsPedestrian;
            Movement vehicle = aIsVehicle ? a : b;
            if (vehicle.Kind == MovementKind.Straight || vehicle.Kind == MovementKind.Track)
                return Relation.Hard;
            // Turning vehicles cross the crosswalk at walking pace and wait for it.
            return aIsVehicle ? Relation.Yields : Relation.HasPriority;
        }

        /// <summary>Right of way among vehicles: higher rank goes first.</summary>
        private static int Rank(MovementKind kind, bool leftHandTraffic)
        {
            switch (kind)
            {
                case MovementKind.Straight:
                    return 3;
                case MovementKind.Right:
                    return leftHandTraffic ? 1 : 2;
                case MovementKind.Left:
                    return leftHandTraffic ? 2 : 1;
                default:
                    return 0;
            }
        }

        private static bool IsOpposite(int a, int b, int[] oppositeOf)
        {
            if (oppositeOf == null)
                return false;
            return (a >= 0 && a < oppositeOf.Length && oppositeOf[a] == b)
                || (b >= 0 && b < oppositeOf.Length && oppositeOf[b] == a);
        }
    }
}
