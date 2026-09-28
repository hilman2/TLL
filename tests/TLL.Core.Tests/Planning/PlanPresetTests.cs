using System.Globalization;
using System.Linq;
using System.Threading;
using TLL.Core.Control;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Planning
{
    public class PlanPresetTests
    {
        private static PlanPreset Sample()
        {
            JunctionModel m = ChordModel.Build(new[] { 0f, 90f, 180f, 270f }, false);
            var preset = new PlanPreset { Name = "Kreuzung, 3 Phasen: Ärger & Co", Angles = new[] { 0f, 92.5f, 180f, 271.25f }, HasTiming = true, Mode = ControlMode.Actuated, AutoTiming = false, TurnOnRed = true };
            preset.Movements.AddRange(m.Movements);
            foreach (Phase p in PhasePlanner.Build(m, PlanStrategy.ProtectedTurns).Phases)
                preset.Phases.Add(new PresetPhase { Green = p.Green, MinGreen = 5.5f, MaxGreen = 45f, GreenTime = 20.25f });
            preset.Phases[preset.Phases.Count - 1] = new PresetPhase { Green = preset.Phases[preset.Phases.Count - 1].Green, MinGreen = 7f, MaxGreen = 30f, GreenTime = 12f, Scramble = true };
            preset.Lanes.Add(new PresetLane { Arm = 0, Lane = 0, Targets = 0b0010 });
            preset.Lanes.Add(new PresetLane { Arm = 0, Lane = 1, Targets = 0b1100 });
            preset.HasTurnBans = true;
            preset.ForbiddenTurns.Add((2, 2));
            preset.SolidLines.Add(new PresetSolidLines { Arm = 1, Pieces = 2, Lines = 0b101 });
            return preset;
        }

        [Fact]
        public void TextRoundTripsUnderAGermanCulture()
        {
            // A comma as decimal separator must not reach the text: a preset
            // shared from a German game must read in an English one.
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                PlanPreset preset = Sample();
                string text = preset.ToText();
                Assert.Contains("92.5", text);
                Assert.DoesNotContain("92,5", text);

                PlanPreset read = PlanPreset.Parse(text, out string error);

                Assert.Null(error);
                Assert.Equal(preset.Name, read.Name);
                Assert.Equal(preset.Angles, read.Angles);
                Assert.Equal(preset.Movements.Select(x => (x.Source, x.Target, x.Kind)), read.Movements.Select(x => (x.Source, x.Target, x.Kind)));
                Assert.Equal(preset.Phases, read.Phases);
                Assert.True(read.HasTiming);
                Assert.Equal(ControlMode.Actuated, read.Mode);
                Assert.False(read.AutoTiming);
                Assert.True(read.TurnOnRed);
                Assert.Equal(preset.Lanes, read.Lanes);
                Assert.True(read.HasTurnBans);
                Assert.Equal(preset.ForbiddenTurns, read.ForbiddenTurns);
                Assert.Equal(preset.SolidLines, read.SolidLines);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void APresetPastedWithChatAroundItReadsAndUnknownLinesAreSkipped()
        {
            string text = "Here is my junction:\n```\n" + Sample().ToText().Replace("end\n", "blinkers 3 fast\nend\n") + "```\nhave fun";

            PlanPreset read = PlanPreset.Parse(text, out string error);

            Assert.Null(error);
            Assert.Equal(Sample().Phases.Count, read.Phases.Count);
        }

        [Fact]
        public void APresetOfANewerVersionIsRefusedWithAReason()
        {
            string text = Sample().ToText().Replace("TLL preset 1", "TLL preset 9");

            Assert.Null(PlanPreset.Parse(text, out string error));
            Assert.Contains("newer", error);
        }

        [Fact]
        public void APhaseNamingAMovementThePresetLacksIsRefused()
        {
            PlanPreset preset = Sample();
            preset.Phases[0] = new PresetPhase { Green = 1UL << preset.Movements.Count };

            Assert.Null(PlanPreset.Parse(preset.ToText(), out string error));
            Assert.NotNull(error);
            Assert.NotNull(preset.Validate());
        }

        [Fact]
        public void BrokenTextIsRefusedNotThrown()
        {
            Assert.Null(PlanPreset.Parse("TLL preset 1\narms 0 x 180\nend\n", out string error));
            Assert.NotNull(error);
            Assert.Null(PlanPreset.Parse("hello", out error));
            Assert.Null(PlanPreset.Parse("", out error));
        }

        [Fact]
        public void APresetForTheOtherSideSwapsItsTurns()
        {
            PlanPreset preset = Sample();

            PlanPreset mirrored = preset.For(true);

            Assert.NotSame(preset, mirrored);
            Assert.True(mirrored.LeftHandTraffic);
            Assert.Equal(preset.Movements.Count(x => x.Kind == MovementKind.Left), mirrored.Movements.Count(x => x.Kind == MovementKind.Right));
            Assert.Equal(360f - 92.5f, mirrored.Angles[1]);
            Assert.Same(preset, preset.For(false));
        }
    }
}
