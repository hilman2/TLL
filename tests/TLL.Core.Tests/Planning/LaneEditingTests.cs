using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class LaneEditingTests
    {
        // Three lanes, directions 0 right, 1 straight, 2 left (right-hand
        // traffic, from the kerb outwards). Each road takes two lanes.
        private static readonly int[] Two = { 2, 2, 2 };

        private static LaneUse[] Lanes(params (int first, int last)[] uses)
        {
            var result = new LaneUse[uses.Length];
            for (int i = 0; i < uses.Length; i++)
                result[i] = new LaneUse(uses[i].first, uses[i].last);
            return result;
        }

        [Fact]
        public void AStraightLaneMayAlsoTurnLeftWhereTheLaneBesideItTurnsLeftToo()
        {
            LaneUse[] lanes = Lanes((0, 0), (1, 1), (2, 2));

            Assert.Equal(LaneEdit.Done, LaneEditing.Toggle(lanes, 1, 2, Two, out LaneUse[] result));
            Assert.Equal(new LaneUse(1, 2), result[1]);
            Assert.Equal(new LaneUse(1, 1), lanes[1]);
        }

        [Fact]
        public void AnInnerLaneTurningRightWouldCrossTheLaneOutsideIt()
        {
            // Lane 2 turns left; giving it right as well would take a
            // direction beyond the straight one in between.
            Assert.Equal(LaneEdit.Gap, LaneEditing.Toggle(Lanes((0, 0), (1, 1), (2, 2)), 2, 0, Two, out _));
            // Lane 1 turning right while lane 0, nearer the kerb, only goes
            // straight: the right turn would cross lane 0's straight path.
            Assert.Equal(LaneEdit.Cross, LaneEditing.Toggle(Lanes((1, 1), (1, 1), (2, 2)), 1, 0, Two, out LaneUse[] kept));
            Assert.Equal(new LaneUse(1, 1), kept[1]);
            // Two lanes both turning right and going straight run side by side.
            Assert.Equal(LaneEdit.Done, LaneEditing.Toggle(Lanes((0, 1), (1, 1), (2, 2)), 1, 0, Two, out _));
        }

        [Fact]
        public void TheLastDirectionOfALaneAndTheLastLaneOfADirectionStay()
        {
            Assert.Equal(LaneEdit.LastDirection, LaneEditing.Toggle(Lanes((0, 0), (1, 1), (2, 2)), 1, 1, Two, out _));
            Assert.Equal(LaneEdit.Uncovered, LaneEditing.Toggle(Lanes((0, 1), (1, 1), (2, 2)), 0, 0, Two, out _));
        }

        [Fact]
        public void ADirectionGetsNoMoreLanesThanItsRoadTakes()
        {
            // The left turn leads into a road with one lane.
            Assert.Equal(LaneEdit.TooMany, LaneEditing.Toggle(Lanes((0, 0), (1, 1), (2, 2)), 1, 2, new[] { 2, 2, 1 }, out _));
        }

        [Fact]
        public void AMiddleDirectionCannotBeTakenFromALaneServingThreeOfThem()
        {
            Assert.Equal(LaneEdit.Gap, LaneEditing.Toggle(Lanes((0, 2), (2, 2)), 0, 1, Two, out _));
        }
    }
}
