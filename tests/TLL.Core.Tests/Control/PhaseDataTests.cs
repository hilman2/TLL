using System.Linq;
using TLL.Core.Control;
using Xunit;

namespace TLL.Core.Tests.Control
{
    public class PhaseDataTests
    {
        [Fact]
        public void PedestrianPhaseOfTheScrambleLayoutRunsAfterAScrambleOnDemand()
        {
            // The scramble on demand of one layout and the pedestrian phase of
            // the scramble layout give green to the same crosswalks. When the
            // layout changes from the one to the other, the new phase keeps
            // the old one's timing. It must still run whenever someone
            // presses the button: in the scramble layout no vehicle crosses a
            // crosswalk, so pedestrians are never diverted.
            PhaseData onDemand = ControllerHarness.Phase(5, 30, 10, PhaseFlags.Pedestrian | PhaseFlags.Scramble);
            PhaseData walk = ControllerHarness.Phase(5, 30, 10, PhaseFlags.Pedestrian);
            walk.WalkGreen = (ushort)SimTime.ToSteps(12f);
            walk.KeepFrom(in onDemand);

            var h = new ControllerHarness(ControllerConfig.Default(ControlMode.Adaptive),
                ControllerHarness.Phase(5, 30, 20), ControllerHarness.Phase(5, 30, 20), walk);
            h.Run(0, 800, (s, p) =>
            {
                p[0].Demand = 5f;
                p[0].Pressure = 5f;
                p[1].Demand = 6f;
                p[1].Pressure = 6f;
                p[2].PedestrianCall = true;
            });
            Assert.True(h.GreenStarts().Count(g => g.phase == 2) >= 2, "the pedestrian phase hardly ever gets green");
            Assert.Contains(h.Trace, r => r.Stage == Stage.Green && r.Phase == 2 && r.Walk);
        }

        [Fact]
        public void KeepsTheTimingAndTheGreenWave()
        {
            var old = new PhaseData { Green = 40, MaxGreen = 90, Flags = PhaseFlags.Coordinated };
            var fresh = new PhaseData { Green = 10, MaxGreen = 30, Flags = PhaseFlags.None };
            fresh.KeepFrom(in old);
            Assert.Equal(40, fresh.Green);
            Assert.Equal(90, fresh.MaxGreen);
            Assert.Equal(PhaseFlags.Coordinated, fresh.Flags);
        }

        [Fact]
        public void TheNewPlanDecidesWhatThePhaseIs()
        {
            // The other way round: the scramble on demand comes back after the
            // scramble layout, and must wait for a diversion again.
            var old = new PhaseData { Flags = PhaseFlags.Pedestrian };
            var fresh = new PhaseData { Flags = PhaseFlags.Pedestrian | PhaseFlags.Scramble };
            fresh.KeepFrom(in old);
            Assert.Equal(PhaseFlags.Pedestrian | PhaseFlags.Scramble, fresh.Flags);
        }
    }
}
