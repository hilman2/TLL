using System;
using System.Linq;
using TLL.Core.Control;
using Xunit;

namespace TLL.Core.Tests.Control
{
    public class SignalControllerTests
    {
        private static ControllerHarness FixedThreePhase(int offset = 0, ControlMode mode = ControlMode.FixedTime)
        {
            var config = ControllerConfig.Default(mode);
            config.Offset = offset;
            return new ControllerHarness(config,
                ControllerHarness.Phase(5, 60, 30, PhaseFlags.Coordinated),
                ControllerHarness.Phase(5, 60, 15),
                ControllerHarness.Phase(5, 60, 10));
        }

        [Fact]
        public void FixedTimeGreensStartOnScheduleAfterOneCycle()
        {
            ControllerHarness h = FixedThreePhase(offset: 17);
            var access = new PhaseArray(h.Phases);
            int cycle = SignalController.CycleOf(in h.Config, ref access);

            h.Run(1000, cycle * 6);

            foreach (var (step, phase) in h.GreenStarts().Where(g => g.step >= 1000 + cycle))
            {
                int position = SimTime.Mod(step - 17, cycle);
                Assert.Equal(SignalController.StartOf(in h.Config, ref access, phase), position);
            }
        }

        [Fact]
        public void FixedTimeGreensHaveThePlannedLength()
        {
            ControllerHarness h = FixedThreePhase();
            var access = new PhaseArray(h.Phases);
            int cycle = SignalController.CycleOf(in h.Config, ref access);
            h.Run(0, cycle * 5);
            foreach (var (phase, length) in h.GreenLengths().Skip(3))
                Assert.Equal(h.Phases[phase].Green, length);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(13)]
        [InlineData(101)]
        [InlineData(777)]
        public void ControllersStartedAtDifferentTimesShowTheSameSignals(int lateStart)
        {
            // Two junctions with the same plan and offset must agree once both
            // are running, however late the second one was switched on. This
            // is what keeps a green wave together.
            ControllerHarness early = FixedThreePhase(offset: 5, mode: ControlMode.Coordinated);
            ControllerHarness late = FixedThreePhase(offset: 5, mode: ControlMode.Coordinated);
            var access = new PhaseArray(early.Phases);
            int cycle = SignalController.CycleOf(in early.Config, ref access);
            Action<long, PhaseData[]> allDemand = (s, p) => { foreach (int i in Enumerable.Range(0, p.Length)) p[i].Demand = 1f; };

            early.Run(0, lateStart + cycle * 4, allDemand);
            late.Run(lateStart, cycle * 4, allDemand);

            long settled = lateStart + cycle * 2;
            var a = early.Trace.Where(r => r.Step >= settled).Select(r => (r.Step, r.Stage, r.Phase)).ToList();
            var b = late.Trace.Where(r => r.Step >= settled).Select(r => (r.Step, r.Stage, r.Phase)).ToList();
            Assert.Equal(a, b);
        }

        [Theory]
        [InlineData(ControlMode.FixedTime)]
        [InlineData(ControlMode.Coordinated)]
        [InlineData(ControlMode.Actuated)]
        [InlineData(ControlMode.Adaptive)]
        public void NoGreenIsShorterThanItsMinimum(ControlMode mode)
        {
            var random = new Random(99);
            var config = ControllerConfig.Default(mode);
            var h = new ControllerHarness(config,
                ControllerHarness.Phase(6, 40, 20, PhaseFlags.Coordinated),
                ControllerHarness.Phase(8, 30, 12),
                ControllerHarness.Phase(4, 20, 9),
                ControllerHarness.Phase(10, 25, 14));
            h.Run(0, 20000, (s, p) =>
            {
                for (int i = 0; i < p.Length; i++)
                {
                    p[i].Demand = random.Next(3) == 0 ? 0f : random.Next(10);
                    p[i].Pressure = p[i].Demand - random.Next(5);
                    p[i].Preempt = random.Next(2000) == 0;
                }
            });
            foreach (var (phase, length) in h.GreenLengths())
                Assert.True(length >= h.Phases[phase].MinGreen, $"phase {phase} green for {length} steps, minimum {h.Phases[phase].MinGreen}");
        }

        [Theory]
        [InlineData(ControlMode.FixedTime)]
        [InlineData(ControlMode.Coordinated)]
        [InlineData(ControlMode.Actuated)]
        [InlineData(ControlMode.Adaptive)]
        public void EveryChangeOfGreenPassesThroughTheFullIntergreen(ControlMode mode)
        {
            var random = new Random(7);
            var config = ControllerConfig.Default(mode);
            var h = new ControllerHarness(config,
                ControllerHarness.Phase(5, 30, 20, PhaseFlags.Coordinated),
                ControllerHarness.Phase(5, 30, 12),
                ControllerHarness.Phase(5, 30, 9));
            h.Run(0, 10000, (s, p) =>
            {
                for (int i = 0; i < p.Length; i++)
                {
                    p[i].Demand = random.Next(2) == 0 ? 0f : 3f;
                    p[i].Pressure = random.Next(10);
                }
            });

            // Between two greens the trace must show exactly Yellow, AllRed and
            // Prepare, each for its configured number of steps.
            string expected = new string('Y', config.Yellow) + new string('R', config.AllRed) + new string('P', config.Prepare);
            string trace = new string(h.Trace.Select(r => r.Stage switch
            {
                Stage.Green => 'G',
                Stage.Yellow => 'Y',
                Stage.AllRed => 'R',
                Stage.Prepare => 'P',
                _ => '?',
            }).ToArray());
            // The run may stop in the middle of a transition; only complete gaps count.
            string complete = trace.Substring(0, trace.LastIndexOf('G') + 1);
            Assert.True(complete.Length > trace.Length - expected.Length - 1, $"trace ends with {trace.Substring(complete.Length)}");
            foreach (string gap in complete.Split('G', StringSplitOptions.RemoveEmptyEntries))
                Assert.Equal(expected, gap);
        }

        [Fact]
        public void ActuatedSkipsPhasesWithoutDemandAndRestsWhereDemandIs()
        {
            var h = new ControllerHarness(ControllerConfig.Default(ControlMode.Actuated),
                ControllerHarness.Phase(5, 30, 0),
                ControllerHarness.Phase(5, 30, 0),
                ControllerHarness.Phase(5, 30, 0));
            h.Run(0, 2000, (s, p) =>
            {
                p[0].Demand = 0f;
                p[1].Demand = 0f;
                p[2].Demand = 2f;
            });
            Assert.Equal(Stage.Green, h.State.Stage);
            Assert.Equal(2, h.State.Phase);
            Assert.DoesNotContain(h.GreenStarts().Skip(1), g => g.phase != 2);
        }

        [Fact]
        public void ActuatedGreenEndsAtMaximumWhenOthersWait()
        {
            var h = new ControllerHarness(ControllerConfig.Default(ControlMode.Actuated),
                ControllerHarness.Phase(5, 20, 0),
                ControllerHarness.Phase(5, 20, 0));
            h.Run(0, 3000, (s, p) =>
            {
                p[0].Demand = 5f;
                p[1].Demand = 5f;
            });
            int max = h.Phases[0].MaxGreen;
            foreach (var (_, length) in h.GreenLengths())
                Assert.InRange(length, max, max + 1);
        }

        [Fact]
        public void AdaptiveServesAStarvedPhaseDespiteLowPressure()
        {
            var config = ControllerConfig.Default(ControlMode.Adaptive);
            var h = new ControllerHarness(config,
                ControllerHarness.Phase(5, 3000, 0),
                ControllerHarness.Phase(5, 3000, 0));
            h.Run(0, 5000, (s, p) =>
            {
                p[0].Demand = 50f;
                p[0].Pressure = 1000f;
                p[1].Demand = 1f;
                p[1].Pressure = 1f;
            });
            int longestWait = 0;
            int wait = 0;
            foreach (var r in h.Trace)
            {
                wait = r.Stage == Stage.Green && r.Phase == 1 ? 0 : wait + 1;
                longestWait = Math.Max(longestWait, wait);
            }
            int bound = config.MaxWait + h.Phases[0].MinGreen + config.Intergreen + 2;
            Assert.True(longestWait <= bound, $"phase 1 waited {longestWait} steps, bound {bound}");
        }

        /// <summary>
        /// Phase 0 starts with a car at the line, then has nobody waiting but
        /// <paramref name="platoon"/> vehicles a few seconds out; phase 1 has
        /// <paramref name="queue"/> waiting.
        /// </summary>
        private static ControllerHarness PlatoonCase(float platoon, float queue, float maxGreenSeconds, int steps)
        {
            var h = new ControllerHarness(ControllerConfig.Default(ControlMode.Adaptive),
                ControllerHarness.Phase(5, maxGreenSeconds, 0),
                ControllerHarness.Phase(5, 3000, 0));
            h.Run(0, steps, (s, p) =>
            {
                p[0].Demand = s < 3 ? 1f : 0f;
                p[0].Pressure = s < 3 ? 1f : 0.5f * platoon;
                p[0].Approaching = s < 3 ? 0f : platoon;
                p[1].Demand = s < 3 ? 0f : queue;
                p[1].Pressure = s < 3 ? 0f : queue;
            });
            return h;
        }

        [Fact]
        public void AdaptiveHoldsGreenForAPlatoonLargerThanTheQueue()
        {
            ControllerHarness h = PlatoonCase(platoon: 4f, queue: 1f, maxGreenSeconds: 60f, steps: 150);
            Assert.All(h.Trace, r => Assert.True(r.Stage == Stage.Green && r.Phase == 0, $"step {r.Step}: {r.Stage} phase {r.Phase}"));
        }

        [Theory]
        [InlineData(4f, 4f)]
        [InlineData(1f, 0.5f)]
        public void AdaptiveDoesNotHoldForASmallPlatoon(float platoon, float queue)
        {
            ControllerHarness h = PlatoonCase(platoon, queue, maxGreenSeconds: 60f, steps: 150);
            long bound = h.Phases[0].MinGreen + h.Config.Intergreen + 2;
            Assert.Contains(h.GreenStarts(), g => g.phase == 1 && g.step <= bound);
        }

        [Fact]
        public void PlatoonHoldEndsAtMaximumGreen()
        {
            ControllerHarness h = PlatoonCase(platoon: 10f, queue: 1f, maxGreenSeconds: 20f, steps: 400);
            long bound = h.Phases[0].MaxGreen + h.Config.Intergreen + 2;
            Assert.Contains(h.GreenStarts(), g => g.phase == 1 && g.step <= bound);
        }

        [Fact]
        public void AdaptiveSwitchesToMuchHigherPressure()
        {
            var h = new ControllerHarness(ControllerConfig.Default(ControlMode.Adaptive),
                ControllerHarness.Phase(5, 3000, 0),
                ControllerHarness.Phase(5, 3000, 0));
            h.Run(0, 400, (s, p) =>
            {
                p[0].Demand = 1f;
                p[0].Pressure = 2f;
                p[1].Demand = 20f;
                p[1].Pressure = 40f;
            });
            Assert.Equal(1, h.State.Phase);
        }

        [Theory]
        [InlineData(ControlMode.FixedTime)]
        [InlineData(ControlMode.Coordinated)]
        [InlineData(ControlMode.Actuated)]
        [InlineData(ControlMode.Adaptive)]
        public void EmergencyVehicleGetsGreenWithinMinimumPlusIntergreen(ControlMode mode)
        {
            var config = ControllerConfig.Default(mode);
            var h = new ControllerHarness(config,
                ControllerHarness.Phase(5, 60, 40, PhaseFlags.Coordinated),
                ControllerHarness.Phase(5, 60, 20));
            h.Run(0, 50, (s, p) => { p[0].Demand = 5f; p[0].Pressure = 5f; });
            Assert.Equal(0, h.State.Phase);

            h.Run(50, 200, (s, p) => { p[0].Demand = 5f; p[1].Preempt = true; p[1].Demand = 1f; });

            long green = h.Trace.First(r => r.Step >= 50 && r.Stage == Stage.Green && r.Phase == 1).Step;
            Assert.True(green - 50 <= h.Phases[0].MinGreen + config.Intergreen + 1, $"green after {green - 50} steps");
        }

        [Fact]
        public void CoordinatedPhaseStartsAtTheSameCyclePositionWhateverTheSideStreetsDo()
        {
            var random = new Random(3);
            ControllerHarness h = FixedThreePhase(offset: 40, mode: ControlMode.Coordinated);
            var access = new PhaseArray(h.Phases);
            int cycle = SignalController.CycleOf(in h.Config, ref access);
            h.Run(0, cycle * 30, (s, p) =>
            {
                p[0].Demand = 3f;
                p[1].Demand = random.Next(3) == 0 ? 0f : 2f;
                p[2].Demand = random.Next(3) == 0 ? 0f : 2f;
            });

            // Early arrival is allowed, a late one is not: the coordinated green
            // must be on at the scheduled start of its window every cycle.
            int start = SignalController.StartOf(in h.Config, ref access, 0);
            int end = SignalController.EndOf(in h.Config, ref access, 0);
            foreach (var r in h.Trace.Where(r => r.Step >= cycle * 2))
            {
                int position = SimTime.Mod(r.Step - 40, cycle);
                if (position >= start && position < end)
                    Assert.True(r.Stage == Stage.Green && r.Phase == 0, $"step {r.Step}, cycle position {position}: {r.Stage} phase {r.Phase}");
            }
        }

        [Fact]
        public void WithoutIntergreenTheScheduleStillHolds()
        {
            ControllerHarness h = FixedThreePhase(offset: 9);
            h.Config.Yellow = 0;
            h.Config.AllRed = 0;
            h.Config.Prepare = 0;
            var access = new PhaseArray(h.Phases);
            int cycle = SignalController.CycleOf(in h.Config, ref access);

            h.Run(0, cycle * 5);

            Assert.All(h.Trace, r => Assert.Equal(Stage.Green, r.Stage));
            foreach (var (step, phase) in h.GreenStarts().Where(g => g.step >= cycle))
                Assert.Equal(SignalController.StartOf(in h.Config, ref access, phase), SimTime.Mod(step - 9, cycle));
        }

        [Fact]
        public void LeavingFlashingGoesThroughAllRed()
        {
            ControllerHarness h = FixedThreePhase();
            h.Config.Mode = ControlMode.Flashing;
            h.Run(0, 10);
            Assert.Equal(Stage.Flashing, h.State.Stage);

            h.Config.Mode = ControlMode.FixedTime;
            h.Run(10, 1);
            Assert.Equal(Stage.AllRed, h.State.Stage);
        }
    }
}
