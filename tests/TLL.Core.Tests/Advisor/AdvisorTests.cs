using System;
using System.Linq;
using TLL.Core.Advisor;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Advisor
{
    public class AdvisorTests
    {
        private static readonly float[] Cross = { 0f, 90f, 180f, 270f };
        private static readonly DelayParameters P = DelayParameters.Default;

        /// <summary>Volumes for a plain cross: straight, left and right per approach, and pedestrians per crosswalk.</summary>
        private static float[] Volumes(JunctionModel m, float straight, float left, float right, float pedestrians)
        {
            var v = new float[m.Movements.Count];
            for (int i = 0; i < v.Length; i++)
            {
                switch (m.Movements[i].Kind)
                {
                    case MovementKind.Straight: v[i] = straight; break;
                    case MovementKind.Left: v[i] = left; break;
                    case MovementKind.Right: v[i] = right; break;
                    case MovementKind.Pedestrian: v[i] = pedestrians; break;
                }
            }
            return v;
        }

        private static PlanEstimate Estimate(JunctionModel m, PlanStrategy s, float[] v)
        {
            return DelayModel.Estimate(m, PhasePlanner.Build(m, s), v, P);
        }

        [Fact]
        public void GapCapacityMatchesTheHcmFormula()
        {
            Assert.Equal(1440f, DelayModel.GapCapacity(0f, 4.5f, 2.5f), 1);
            Assert.Equal(572f, DelayModel.GapCapacity(1000f, 4.5f, 2.5f), 0);
        }

        [Fact]
        public void SignalDelayMatchesTheHcmFormula()
        {
            float d = DelayModel.SignalDelay(500f, 1000f, 0.5f, 60f, 0.25f, out float x);
            Assert.Equal(0.5f, x, 3);
            Assert.Equal(11.79f, d, 1);
        }

        [Fact]
        public void DelayGrowsWithTraffic()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            foreach (PlanStrategy s in JunctionAdvisor.Strategies)
            {
                float previous = 0f;
                for (float load = 50f; load <= 800f; load += 50f)
                {
                    PlanEstimate e = Estimate(m, s, Volumes(m, load, load / 3f, load / 3f, load / 4f));
                    Assert.True(e.AverageDelay >= previous - 0.01f, $"{s}: {e.AverageDelay} s at {load} veh/h after {previous} s");
                    Assert.False(float.IsNaN(e.TotalDelay) || float.IsInfinity(e.TotalDelay), $"{s} at {load}");
                    previous = e.AverageDelay;
                }
            }
        }

        [Fact]
        public void HeavyLeftTurnsAgainstHeavyOncomingTrafficGetTheirOwnPhase()
        {
            // A north-south arterial with two through lanes per direction:
            // the through lanes cope, but a left turn finds few gaps in
            // 1400 oncoming vehicles per hour. East-west is a quiet side road.
            JunctionModel m = ChordModel.Build(Cross, false);
            for (int i = 0; i < m.Movements.Count; i++)
            {
                Movement mv = m.Movements[i];
                if (mv.Kind == MovementKind.Straight && (mv.Source == 1 || mv.Source == 3))
                    m.Movements[i] = new Movement(mv.Source, mv.Target, mv.Kind, 2);
            }
            float[] v = Volumes(m, 1400f, 300f, 100f, 20f);
            for (int i = 0; i < v.Length; i++)
            {
                if (!m.Movements[i].IsPedestrian && (m.Movements[i].Source == 0 || m.Movements[i].Source == 2))
                    v[i] /= 6f;
            }
            PlanEstimate[] e = JunctionAdvisor.EvaluateAll(m, v, P);
            string table = string.Join("; ", e.Select((x, i) => $"{JunctionAdvisor.Strategies[i]}: {x.AverageDelay:0.0} s, X {x.WorstSaturation:0.00}"));
            Assert.True(JunctionAdvisor.Choose(PlanStrategy.Permissive, e) == PlanStrategy.ProtectedTurns, table);
        }

        [Fact]
        public void FewLeftTurnsGiveWayToOncomingTraffic()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            PlanEstimate[] e = JunctionAdvisor.EvaluateAll(m, Volumes(m, 400f, 30f, 60f, 20f), P);
            Assert.Equal(PlanStrategy.Permissive, JunctionAdvisor.Choose(PlanStrategy.Split, e));
        }

        [Fact]
        public void CrowdedCrosswalksWithHeavyTurningGetAPedestrianPhase()
        {
            JunctionModel m = ChordModel.Build(Cross, false);
            PlanEstimate[] crowded = JunctionAdvisor.EvaluateAll(m, Volumes(m, 150f, 150f, 250f, 1800f), P);
            Assert.Equal(PlanStrategy.ExclusivePedestrian, JunctionAdvisor.Choose(PlanStrategy.Permissive, crowded));

            PlanEstimate[] quiet = JunctionAdvisor.EvaluateAll(m, Volumes(m, 150f, 150f, 250f, 50f), P);
            Assert.NotEqual(PlanStrategy.ExclusivePedestrian, JunctionAdvisor.Choose(PlanStrategy.Permissive, quiet));
        }

        [Fact]
        public void ALayoutThatCopesBeatsOneThatOverloadsWhateverTheAverage()
        {
            var e = new PlanEstimate[JunctionAdvisor.Strategies.Length];
            for (int i = 0; i < e.Length; i++)
                e[i] = new PlanEstimate { TotalDelay = 50000f, AverageDelay = 40f, WorstSaturation = 1.3f };
            e[0] = new PlanEstimate { TotalDelay = 20000f, AverageDelay = 20f, WorstSaturation = 1.1f };
            e[2] = new PlanEstimate { TotalDelay = 30000f, AverageDelay = 30f, WorstSaturation = 0.9f };
            Assert.Equal(PlanStrategy.Split, JunctionAdvisor.Choose(PlanStrategy.Permissive, e));
        }

        [Fact]
        public void SmallGainsDoNotSwitchTheLayout()
        {
            var e = new PlanEstimate[JunctionAdvisor.Strategies.Length];
            for (int i = 0; i < e.Length; i++)
                e[i] = new PlanEstimate { TotalDelay = 10000f, AverageDelay = 20f };
            e[1] = new PlanEstimate { TotalDelay = 9000f, AverageDelay = 18f };
            Assert.Equal(PlanStrategy.Permissive, JunctionAdvisor.Choose(PlanStrategy.Permissive, e));
        }

        [Fact]
        public void FlashingHasHysteresis()
        {
            // Traffic in the band between the thresholds keeps whatever runs now.
            Assert.False(FlashAdvisor.Decide(false, 400f, 60f, 100f));
            Assert.True(FlashAdvisor.Decide(true, 400f, 60f, 100f));
            Assert.True(FlashAdvisor.Decide(false, 200f, 30f, 60f));
            Assert.False(FlashAdvisor.Decide(true, 900f, 100f, 200f));
        }

        [Fact]
        public void BusySideRoadNeedsSignalsQuietOneDoesNot()
        {
            Assert.Equal(SignalAdvice.AddSignals, SignalAdvisor.Decide(false, 1400f, 400f, 25f));
            Assert.Equal(SignalAdvice.Keep, SignalAdvisor.Decide(false, 300f, 40f, 25f));
            Assert.Equal(SignalAdvice.RemoveSignals, SignalAdvisor.Decide(true, 250f, 40f, 25f));
            Assert.Equal(SignalAdvice.Keep, SignalAdvisor.Decide(true, 1400f, 400f, 25f));
        }
    }
}
