using TLL.Core.Planning;

namespace TLL.Core.Tests.Planning
{
    /// <summary>Junctions from the game, as its set-up logged them, for tests of several classes.</summary>
    internal static class TestJunctions
    {
        /// <summary>
        /// Junction 469506 as the game analysed it: a T whose main road bends,
        /// from the stem (2) into approach 1 and back, with a tram. The other
        /// approach carries little, the U-turns nothing. Movements and hard
        /// conflicts as its set-up logged them; no lane is shared.
        /// </summary>
        /// <returns>The model, peak volumes per movement, and the two busiest movements, stem into 1 and 1 into the stem.</returns>
        public static (JunctionModel model, float[] volumes, int down, int up) BentMainRoad()
        {
            var m = new JunctionModel { ApproachCount = 3, OppositeOf = new[] { 1, 0, -1 } };
            m.Movements.Add(new Movement(0, -1, MovementKind.Pedestrian));
            m.Movements.Add(new Movement(0, 0, MovementKind.UTurn));
            m.Movements.Add(new Movement(0, 1, MovementKind.Straight));
            m.Movements.Add(new Movement(0, 2, MovementKind.Right));
            m.Movements.Add(new Movement(1, -1, MovementKind.Pedestrian));
            m.Movements.Add(new Movement(1, 0, MovementKind.Straight));
            m.Movements.Add(new Movement(1, 1, MovementKind.UTurn));
            m.Movements.Add(new Movement(1, 2, MovementKind.Left));
            m.Movements.Add(new Movement(1, 2, MovementKind.Track));
            m.Movements.Add(new Movement(2, -1, MovementKind.Pedestrian));
            m.Movements.Add(new Movement(2, 0, MovementKind.Left));
            m.Movements.Add(new Movement(2, 1, MovementKind.Right));
            m.Movements.Add(new Movement(2, 2, MovementKind.UTurn));
            int n = m.Movements.Count;
            m.SharedLane = new ulong[n];
            m.Conflicts = new ConflictMatrix(n);
            int[,] hard =
            {
                { 0, 2 }, { 0, 5 }, { 1, 10 }, { 2, 4 }, { 2, 10 }, { 2, 11 }, { 3, 8 }, { 3, 12 }, { 4, 5 },
                { 4, 8 }, { 5, 10 }, { 6, 11 }, { 7, 10 }, { 7, 12 }, { 8, 9 }, { 8, 10 }, { 8, 12 },
            };
            for (int i = 0; i < hard.GetLength(0); i++)
                m.Conflicts.Set(hard[i, 0], hard[i, 1], Relation.Hard);
            var volumes = new float[n];
            volumes[11] = 1464f;
            volumes[7] = 829f;
            volumes[8] = 224f;
            volumes[5] = 243f;
            volumes[2] = 194f;
            volumes[10] = 95f;
            volumes[3] = 27f;
            volumes[4] = 157f;
            return (m, volumes, 11, 7);
        }

        /// <summary>
        /// The same junction as a dump of 28 Sep 2026 showed it, after the
        /// U-turns of approaches 1 and 2 were forbidden: numbered as the dump
        /// numbers it, with its hard conflicts, who gives way to whom, the
        /// shared approach lanes and the peak volumes. The side road (2)
        /// turns right and left from shared lanes; its left turn crosses the
        /// busiest flow the other way, 1 into 2.
        /// </summary>
        /// <returns>The model, peak volumes, and the two busiest movements: 1 into 2, and 2 into 1.</returns>
        public static (JunctionModel model, float[] volumes, int leftIn, int rightOut) Junction284683()
        {
            var m = new JunctionModel { ApproachCount = 3, OppositeOf = new[] { 1, 0, -1 } };
            m.Movements.Add(new Movement(0, -1, MovementKind.Pedestrian));
            m.Movements.Add(new Movement(0, 0, MovementKind.UTurn));
            m.Movements.Add(new Movement(0, 1, MovementKind.Straight, 3));
            m.Movements.Add(new Movement(0, 2, MovementKind.Right, 2));
            m.Movements.Add(new Movement(1, -1, MovementKind.Pedestrian));
            m.Movements.Add(new Movement(1, 0, MovementKind.Straight, 3));
            m.Movements.Add(new Movement(1, 2, MovementKind.Left, 2));
            m.Movements.Add(new Movement(1, 2, MovementKind.Track));
            m.Movements.Add(new Movement(2, -1, MovementKind.Pedestrian));
            m.Movements.Add(new Movement(2, 0, MovementKind.Left, 2));
            m.Movements.Add(new Movement(2, 1, MovementKind.Right, 2));
            int n = m.Movements.Count;
            m.Conflicts = new ConflictMatrix(n);
            int[,] hard =
            {
                { 0, 2 }, { 0, 5 }, { 1, 9 }, { 2, 4 }, { 2, 9 }, { 2, 10 }, { 3, 7 }, { 4, 5 }, { 4, 7 },
                { 5, 9 }, { 6, 9 }, { 7, 8 }, { 7, 9 },
            };
            for (int i = 0; i < hard.GetLength(0); i++)
                m.Conflicts.Set(hard[i, 0], hard[i, 1], Relation.Hard);
            int[,] yields =
            {
                { 1, 0 }, { 1, 5 }, { 3, 0 }, { 3, 8 }, { 6, 2 }, { 6, 3 }, { 6, 4 }, { 6, 8 }, { 7, 2 }, { 9, 0 }, { 9, 8 }, { 10, 4 }, { 10, 8 },
            };
            for (int i = 0; i < yields.GetLength(0); i++)
                m.Conflicts.Set(yields[i, 0], yields[i, 1], Relation.Yields);
            m.ShareLane(1, 2);
            m.ShareLane(1, 3);
            m.ShareLane(2, 3);
            m.ShareLane(5, 6);
            m.ShareLane(5, 7);
            m.ShareLane(9, 10);
            var volumes = new float[] { 84f, 0f, 515f, 113f, 334f, 252f, 1283f, 409f, 104f, 157f, 1289f };
            return (m, volumes, 6, 10);
        }

        /// <summary>
        /// The same junction with its lanes as a later dump showed them: on
        /// each approach the U-turn starts from a lane the other movements
        /// use too. The U-turn of approach 1 crosses the busiest flow (hard
        /// conflict with the stem's right turn), and through the shared lane
        /// holds the left turn of approach 1 away from it.
        /// </summary>
        public static (JunctionModel model, float[] volumes, int down, int up) BentMainRoadSharedLanes()
        {
            var (m, volumes, down, up) = BentMainRoad();
            m.ShareLane(1, 2);
            m.ShareLane(1, 3);
            m.ShareLane(2, 3);
            m.ShareLane(5, 6);
            m.ShareLane(5, 7);
            m.ShareLane(6, 7);
            m.ShareLane(10, 12);
            return (m, volumes, down, up);
        }
    }
}
