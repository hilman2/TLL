using TLL.Core.Control;
using Xunit;

namespace TLL.Core.Tests.Control
{
    public class PlanMatchTests
    {
        [Fact]
        public void TheSamePlanInAnotherOrderMatches()
        {
            // The game listed the roads the other way round: movements and
            // phases come in another order, the plan is the same.
            string[] before = { "a", "b", "c", "d" };
            string[] after = { "c", "d", "a", "b" };
            ulong[] phasesBefore = { 0b0011, 0b1100 };
            ulong[] phasesAfter = { 0b0011, 0b1100 };

            int[] map = PlanMatch.PhaseMap(before, after, phasesBefore, phasesAfter);

            Assert.Equal(new[] { 1, 0 }, map);
        }

        [Fact]
        public void AnotherPhaseIsAnotherPlan()
        {
            string[] movements = { "a", "b", "c" };
            Assert.Null(PlanMatch.PhaseMap(movements, movements, new ulong[] { 0b011, 0b100 }, new ulong[] { 0b001, 0b110 }));
        }

        [Fact]
        public void AnotherMovementIsAnotherPlan()
        {
            Assert.Null(PlanMatch.PhaseMap(new[] { "a", "b" }, new[] { "a", "x" }, new ulong[] { 0b01, 0b10 }, new ulong[] { 0b01, 0b10 }));
        }

        [Fact]
        public void AMissingPhaseIsAnotherPlan()
        {
            string[] movements = { "a", "b" };
            Assert.Null(PlanMatch.PhaseMap(movements, movements, new ulong[] { 0b01, 0b10 }, new ulong[] { 0b01, 0b10, 0b11 }));
        }
    }
}
