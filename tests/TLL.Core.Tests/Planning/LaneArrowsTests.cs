using System.Linq;
using TLL.Core.Advisor;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class LaneArrowsTests
    {
        // Right, straight, left: the order of a right-hand-traffic approach.
        private static readonly bool[] Far = { false, false, true };
        private static readonly bool[] Straight = { false, true, false };

        /// <summary>What the game usually builds on three lanes: right and straight, straight, left.</summary>
        private static LaneUse[] ThreeLanes() => new[] { new LaneUse(0, 1), new LaneUse(1, 1), new LaneUse(2, 2) };

        private static int LanesFor(LaneUse[] lanes, int movement) => lanes.Count(l => l.Serves(movement));

        [Fact]
        public void AHeavyLeftTurnGetsASecondLane()
        {
            float[] volumes = { 100f, 300f, 600f };

            LaneUse[] chosen = LaneArrows.Choose(ThreeLanes(), volumes, Far, Straight, new[] { 2, 3, 2 }, signalled: false, out float before, out float after);

            Assert.True(LanesFor(chosen, 2) >= 2, string.Join(",", chosen));
            Assert.True(after <= before * (1f - LaneArrows.MinGain), $"{before} -> {after}");
        }

        [Fact]
        public void AtSignalsNoLaneMixesStraightAndLeft()
        {
            float[] volumes = { 100f, 300f, 600f };

            LaneUse[] chosen = LaneArrows.Choose(ThreeLanes(), volumes, Far, Straight, new[] { 2, 3, 2 }, signalled: true, out _, out _);

            Assert.DoesNotContain(chosen, l => l.Serves(1) && l.Serves(2));
        }

        [Fact]
        public void ASingleLaneAtSignalsServesEverything()
        {
            Assert.NotEqual(float.MaxValue, LaneArrows.Load(new[] { new LaneUse(0, 2) }, new[] { 50f, 100f, 50f }, Far, Straight, signalled: true));
        }

        [Fact]
        public void BalancedTrafficKeepsItsLanes()
        {
            LaneUse[] current = ThreeLanes();

            LaneUse[] chosen = LaneArrows.Choose(current, new[] { 250f, 700f, 350f }, Far, Straight, new[] { 2, 3, 2 }, signalled: false, out _, out _);

            Assert.Equal(current, chosen);
        }

        [Fact]
        public void AQuietApproachKeepsItsLanes()
        {
            // Lopsided, but no lane comes near being full.
            LaneUse[] current = ThreeLanes();

            LaneUse[] chosen = LaneArrows.Choose(current, new[] { 10f, 20f, 200f }, Far, Straight, new[] { 2, 3, 2 }, signalled: false, out _, out _);

            Assert.Equal(current, chosen);
        }

        [Fact]
        public void ATurnGetsNoMoreLanesThanItsRoadTakes()
        {
            float[] volumes = { 100f, 300f, 900f };

            LaneUse[] chosen = LaneArrows.Choose(ThreeLanes(), volumes, Far, Straight, new[] { 2, 3, 1 }, signalled: false, out _, out _);

            Assert.Equal(1, LanesFor(chosen, 2));
        }

        [Fact]
        public void EveryMovementKeepsALane()
        {
            float[] volumes = { 0f, 900f, 500f };

            LaneUse[] chosen = LaneArrows.Choose(ThreeLanes(), volumes, Far, Straight, new[] { 2, 3, 2 }, signalled: false, out _, out _);

            Assert.True(LanesFor(chosen, 0) >= 1, string.Join(",", chosen));
        }

        [Fact]
        public void NoTwoPathsCross()
        {
            foreach (LaneUse[] lanes in LaneArrows.Assignments(4, new[] { 2, 3, 2 }))
            {
                Assert.Equal(0, lanes[0].First);
                Assert.Equal(2, lanes[lanes.Length - 1].Last);
                for (int j = 1; j < lanes.Length; j++)
                {
                    Assert.True(lanes[j].First >= lanes[j - 1].First && lanes[j].Last >= lanes[j - 1].Last, string.Join(",", lanes));
                    Assert.True(lanes[j].First <= lanes[j - 1].Last + 1, string.Join(",", lanes));
                }
            }
        }

        [Fact]
        public void ASharedSideRoadLaneKeepsTheBusiestFlowsApart()
        {
            var (m, volumes, leftIn, rightOut) = TestJunctions.Junction284683();

            PhasePlan plan = PhasePlanner.Build(m, PlanStrategy.Permissive, volumes);

            Assert.DoesNotContain(plan.Phases, p => p.Has(leftIn) && p.Has(rightOut));
        }

        [Fact]
        public void AtSignalsTheSideRoadGetsALaneForEachTurn()
        {
            var (m, volumes, leftIn, rightOut) = TestJunctions.Junction284683();
            // Approach 2, from the kerb out: right into 1, left into 0.
            int[] movements = { 10, 9 };
            var current = new[] { new LaneUse(0, 1), new LaneUse(0, 1), new LaneUse(1, 1) };
            float Cost(LaneUse[] lanes)
            {
                PlanEstimate[] estimates = JunctionAdvisor.EvaluateAll(m.WithLanes(movements, lanes), volumes, DelayParameters.Default, volumes);
                return estimates[JunctionAdvisor.Best(estimates)].TotalDelay;
            }

            LaneUse[] chosen = LaneArrows.ChooseBy(current, new[] { 1289f, 157f }, new[] { false, true }, new[] { false, false }, new[] { 3, 5 },
                signalled: true, Cost, 1000f, out float before, out float after);

            Assert.DoesNotContain(chosen, l => l.Shared);
            PhasePlan plan = PhasePlanner.Build(m.WithLanes(movements, chosen), PlanStrategy.Permissive, volumes);
            Assert.Contains(plan.Phases, p => p.Has(leftIn) && p.Has(rightOut));
            Assert.True(after < before, $"{before} -> {after}");
        }

        [Fact]
        public void WithLanesTiesOnlyTheMovementsThatShareALane()
        {
            var (m, _, _, _) = TestJunctions.Junction284683();

            JunctionModel separate = m.WithLanes(new[] { 10, 9 }, new[] { new LaneUse(0, 0), new LaneUse(0, 0), new LaneUse(1, 1) });

            Assert.Equal(0UL, separate.SharesLaneWith(10) & (1UL << 9));
            Assert.Equal(2, separate.Movements[10].LaneCount);
            Assert.Equal(1, separate.Movements[9].LaneCount);
            // The other approaches keep their ties.
            Assert.NotEqual(0UL, separate.SharesLaneWith(5) & (1UL << 7));
        }

        [Fact]
        public void SharingSpreadsTheLoadButCostsCapacity()
        {
            float[] volumes = { 100f, 300f, 600f };
            float own = LaneArrows.Load(new[] { new LaneUse(0, 0), new LaneUse(1, 1), new LaneUse(2, 2) }, volumes, Far, Straight, false);
            float shared = LaneArrows.Load(new[] { new LaneUse(0, 1), new LaneUse(1, 2), new LaneUse(2, 2) }, volumes, Far, Straight, false);

            Assert.Equal(600f, own, 1);
            // Evenly split, 1000 vehicles on three lanes would be 333 each;
            // the shared lanes carry less than a lane of their own.
            Assert.True(shared > 1000f / 3f + 1f && shared < own, $"{shared}");
        }
    }
}
