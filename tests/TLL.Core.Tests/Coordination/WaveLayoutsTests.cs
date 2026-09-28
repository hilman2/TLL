using System.Collections.Generic;
using TLL.Core.Control;
using TLL.Core.Coordination;
using TLL.Core.Optimization;
using Xunit;

namespace TLL.Core.Tests.Coordination
{
    public class WaveLayoutsTests
    {
        private const int ThroughA = 0;
        private const int ThroughB = 1;
        private const int CrossStreet = 2;
        private const int Crosswalks = 3;

        private static PhaseData Green(int steps)
        {
            return new PhaseData { MinGreen = 20, MaxGreen = 300, Green = (ushort)steps };
        }

        /// <summary>Both through directions in one phase, then the cross street.</summary>
        private static CorridorMember Together()
        {
            return new CorridorMember
            {
                Phases = new[] { Green(80), Green(40) },
                PhaseMovements = new[] { (1UL << ThroughA) | (1UL << ThroughB), 1UL << CrossStreet },
                Intergreen = 16,
                MovementA = ThroughA,
                MovementB = ThroughB,
                Ratios = new[] { 0.45f, 0.25f },
            };
        }

        /// <summary>
        /// Each direction on its own, the cross street, and a phase for the
        /// pedestrians: the through traffic gets a quarter of the cycle.
        /// </summary>
        private static CorridorMember Apart()
        {
            return new CorridorMember
            {
                Phases = new[] { Green(40), Green(40), Green(40), Green(60) },
                PhaseMovements = new[] { 1UL << ThroughA, 1UL << ThroughB, 1UL << CrossStreet, 1UL << Crosswalks },
                Intergreen = 16,
                MovementA = ThroughA,
                MovementB = ThroughB,
                Ratios = new[] { 0.25f, 0.25f, 0.25f, 0.25f },
            };
        }

        private static CorridorPath Path(int junctions)
        {
            var path = new CorridorPath();
            for (int i = 0; i < junctions; i++)
            {
                path.Junctions.Add(i);
                path.ApproachBack.Add(i == 0 ? -1 : 2);
                path.ApproachAhead.Add(i == junctions - 1 ? -1 : 0);
                if (i < junctions - 1)
                    path.Links.Add(new SignalLink { A = i, ApproachA = 0, B = i + 1, ApproachB = 2, Length = 300f, Speed = 13.9f });
            }
            return path;
        }

        [Fact]
        public void TheOneJunctionThatNarrowsTheBandChanges()
        {
            // The middle junction runs its directions apart; with it the band
            // is too narrow. Running them together, it lets the wave through.
            var options = new List<IList<CorridorMember>>
            {
                new[] { Together() },
                new[] { Apart(), Together() },
                new[] { Together() },
            };
            CoordinationPlan before = Coordinator.Plan(Path(3), new[] { Together(), Apart(), Together() }, OptimizerLimits.Default);
            Assert.False(Coupling.BandWorthIt(before.BandwidthA, before.BandwidthB, before.Cycle, true, true, false), "the band was wide enough anyway");

            int[] choice = WaveLayouts.Choose(Path(3), options, true, true, OptimizerLimits.Default, out CoordinationPlan plan);

            Assert.Equal(new[] { 0, 1, 0 }, choice);
            Assert.True(Coupling.BandWorthIt(plan.BandwidthA, plan.BandwidthB, plan.Cycle, true, true, false));
        }

        [Fact]
        public void OnlyTheChangesTheBandNeeds()
        {
            // Every junction could change, only the one running apart needs to.
            var options = new List<IList<CorridorMember>>
            {
                new[] { Together(), Apart() },
                new[] { Apart(), Together() },
                new[] { Together(), Apart() },
            };
            int[] choice = WaveLayouts.Choose(Path(3), options, true, true, OptimizerLimits.Default, out _);
            Assert.Equal(new[] { 0, 1, 0 }, choice);
        }

        [Fact]
        public void NoWaveWhereNoJunctionCanAffordIt()
        {
            var options = new List<IList<CorridorMember>>
            {
                new[] { Together() },
                new[] { Apart() },
                new[] { Apart() },
            };
            Assert.Null(WaveLayouts.Choose(Path(3), options, true, true, OptimizerLimits.Default, out CoordinationPlan plan));
            Assert.Null(plan);
        }
    }
}
