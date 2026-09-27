using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    /// <summary>
    /// Trams against cars, on a cross with approaches 0 to 3 counter-clockwise
    /// and right-hand traffic: 0 and 2 face each other, as do 1 and 3.
    /// </summary>
    public class TramRulesTests
    {
        private static readonly int[] Opposite = { 2, 3, 0, 1 };

        private static Relation Classify(Movement a, Movement b, PathContact contact)
        {
            return ConflictRules.Classify(a, b, contact, Opposite, leftHandTraffic: false);
        }

        [Fact]
        public void CarTurningAcrossAnOncomingTramGivesWay()
        {
            var tram = new Movement(0, 2, MovementKind.Track, 1);
            var left = new Movement(2, 3, MovementKind.Left, 1);
            Assert.Equal(Relation.Yields, Classify(left, tram, PathContact.Cross));
            Assert.Equal(Relation.HasPriority, Classify(tram, left, PathContact.Cross));
        }

        [Fact]
        public void TramTurningAcrossOncomingCarsGivesWay()
        {
            var tram = new Movement(0, 1, MovementKind.Track, 1);
            var straight = new Movement(2, 0, MovementKind.Straight, 1);
            Assert.Equal(Relation.Yields, Classify(tram, straight, PathContact.Cross));
        }

        [Fact]
        public void TramAndCarBothTurningAcrossEachOtherStayApart()
        {
            var tram = new Movement(0, 1, MovementKind.Track, 1);
            var left = new Movement(2, 3, MovementKind.Left, 1);
            Assert.Equal(Relation.Hard, Classify(tram, left, PathContact.Cross));
        }

        [Fact]
        public void TramCrossingFromTheSideStaysApart()
        {
            var tram = new Movement(0, 2, MovementKind.Track, 1);
            var straight = new Movement(1, 3, MovementKind.Straight, 1);
            Assert.Equal(Relation.Hard, Classify(tram, straight, PathContact.Cross));
        }

        [Fact]
        public void MainRoadWithTramsRunsBothWaysTogether()
        {
            // A T junction with trams on every arm, as reported from a city:
            // 0 and 1 are the main road, 2 the side road. Each car approach
            // has one lane shared by all its turns; each track splits into
            // the tram's turns. The permissive plan must let the main road
            // run both ways at once, trams going straight included.
            float[] angles = { -90f, 90f, -179f };
            int[] opposite = ChordModel.FindOpposites(angles);
            var m = new JunctionModel { ApproachCount = 3, OppositeOf = opposite, LeftHandTraffic = false };
            (int s, int t, MovementKind k)[] spec =
            {
                (0, 0, MovementKind.UTurn), (0, 1, MovementKind.Straight), (0, 2, MovementKind.Left), (0, 1, MovementKind.Track), (0, 2, MovementKind.Track),
                (1, 0, MovementKind.Straight), (1, 1, MovementKind.UTurn), (1, 2, MovementKind.Right), (1, 0, MovementKind.Track), (1, 2, MovementKind.Track),
                (2, 0, MovementKind.Right), (2, 1, MovementKind.Left), (2, 2, MovementKind.UTurn), (2, 0, MovementKind.Track), (2, 1, MovementKind.Track),
            };
            foreach (var (s, t, k) in spec)
                m.Movements.Add(new Movement(s, t, k, 1));
            m.Conflicts = ChordModel.Classify(m.Movements, angles, opposite, false, includeMerges: false);
            // Crossings the game's lane overlaps report there, which the
            // circle model of a T does not see; the mod adds them the same way.
            (int s, int t, MovementKind k)[][] crossings =
            {
                new[] { (0, 2, MovementKind.Left), (1, 0, MovementKind.Track) },
                new[] { (0, 2, MovementKind.Track), (1, 0, MovementKind.Straight) },
                new[] { (0, 2, MovementKind.Track), (1, 0, MovementKind.Track) },
            };
            foreach (var pair in crossings)
            {
                int a = m.IndexOf(pair[0].s, pair[0].t, pair[0].k);
                int b = m.IndexOf(pair[1].s, pair[1].t, pair[1].k);
                m.Conflicts.Merge(a, b, ConflictRules.Classify(m.Movements[a], m.Movements[b], PathContact.Cross, opposite, false));
            }
            for (int a = 0; a < m.Movements.Count; a++)
            {
                for (int b = a + 1; b < m.Movements.Count; b++)
                {
                    bool sameSource = m.Movements[a].Source == m.Movements[b].Source;
                    bool sameKind = (m.Movements[a].Kind == MovementKind.Track) == (m.Movements[b].Kind == MovementKind.Track);
                    if (sameSource && sameKind)
                        m.ShareLane(a, b);
                }
            }

            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.Permissive);

            int straight01 = m.IndexOf(0, 1, MovementKind.Straight);
            int straight10 = m.IndexOf(1, 0, MovementKind.Straight);
            int tram01 = m.IndexOf(0, 1, MovementKind.Track);
            int tram10 = m.IndexOf(1, 0, MovementKind.Track);
            Assert.Contains(plan.Phases, p => p.Has(straight01) && p.Has(straight10) && p.Has(tram01) && p.Has(tram10));
        }

        [Fact]
        public void TramMergingWithCarsStaysApart()
        {
            var tram = new Movement(0, 2, MovementKind.Track, 1);
            var right = new Movement(3, 2, MovementKind.Right, 1);
            Assert.Equal(Relation.Hard, Classify(tram, right, PathContact.Merge));
        }
    }
}
