using TLL.Core.Advisor;
using TLL.Core.Planning;
using Xunit;

namespace TLL.Core.Tests.Advisor
{
    public class LayoutMemoryTests
    {
        private static readonly float[] Cross = { 0f, 90f, 180f, 270f };

        private static int Index(PlanStrategy s)
        {
            return System.Array.IndexOf(JunctionAdvisor.Strategies, s);
        }

        /// <summary>Estimates where the model rates Split best by far and all layouts cope.</summary>
        private static PlanEstimate[] SplitLooksBest()
        {
            var e = new PlanEstimate[JunctionAdvisor.Strategies.Length];
            for (int i = 0; i < e.Length; i++)
                e[i] = new PlanEstimate { TotalDelay = 30000f, VehicleDelay = 30000f, Vehicles = 1000f, People = 1000f, AverageDelay = 30f, WorstSaturation = 0.8f };
            e[Index(PlanStrategy.Split)] = new PlanEstimate { TotalDelay = 15000f, VehicleDelay = 15000f, Vehicles = 1000f, People = 1000f, AverageDelay = 15f, WorstSaturation = 0.7f };
            return e;
        }

        [Fact]
        public void ALayoutThatDidBadlyIsNotChosenAgain()
        {
            // On paper Split halves the delay. Run, it waited three times as
            // long as the model said, while Permissive matched the model.
            var memory = new LayoutMemory();
            memory.Record(Index(PlanStrategy.Split), false, 45f, 15f, false);
            memory.Record(Index(PlanStrategy.Permissive), false, 30f, 30f, false);
            PlanEstimate[] corrected = JunctionAdvisor.Correct(SplitLooksBest(), memory, Index(PlanStrategy.Permissive), false);
            Assert.Equal(PlanStrategy.Permissive, JunctionAdvisor.Choose(PlanStrategy.Permissive, corrected));
        }

        [Fact]
        public void WithoutMeasurementTheModelDecides()
        {
            PlanEstimate[] corrected = JunctionAdvisor.Correct(SplitLooksBest(), new LayoutMemory(), Index(PlanStrategy.Permissive), false);
            Assert.Equal(PlanStrategy.Split, JunctionAdvisor.Choose(PlanStrategy.Permissive, corrected));
        }

        [Fact]
        public void AnUntriedLayoutBorrowsTheRunningOnesFactor()
        {
            // The junction waits twice as long as the model says in the game;
            // an untried layout is expected to as well, so the comparison on
            // paper stands.
            var memory = new LayoutMemory();
            memory.Record(Index(PlanStrategy.Permissive), false, 60f, 30f, false);
            PlanEstimate[] corrected = JunctionAdvisor.Correct(SplitLooksBest(), memory, Index(PlanStrategy.Permissive), false);
            Assert.Equal(30f, corrected[Index(PlanStrategy.Split)].AverageDelay, 3);
            Assert.Equal(60f, corrected[Index(PlanStrategy.Permissive)].AverageDelay, 3);
        }

        [Fact]
        public void BacklogBeatsTheModelsOpinion()
        {
            // The model says Permissive copes and is best. It jammed: any
            // layout that copes now wins, even one the model rates worse.
            var e = new PlanEstimate[JunctionAdvisor.Strategies.Length];
            for (int i = 0; i < e.Length; i++)
                e[i] = new PlanEstimate { TotalDelay = 30000f, VehicleDelay = 30000f, Vehicles = 1000f, People = 1000f, AverageDelay = 30f, WorstSaturation = 0.9f };
            e[Index(PlanStrategy.Permissive)] = new PlanEstimate { TotalDelay = 20000f, VehicleDelay = 20000f, Vehicles = 1000f, People = 1000f, AverageDelay = 20f, WorstSaturation = 0.8f };
            var memory = new LayoutMemory();
            memory.Record(Index(PlanStrategy.Permissive), false, 25f, 20f, true);

            PlanEstimate[] corrected = JunctionAdvisor.Correct(e, memory, Index(PlanStrategy.Permissive), false);

            Assert.NotEqual(PlanStrategy.Permissive, JunctionAdvisor.Choose(PlanStrategy.Permissive, corrected));
        }

        private static PlanEstimate Estimate(float average, float saturation)
        {
            return new PlanEstimate
            {
                TotalDelay = average * 1000f, VehicleDelay = average * 1000f, Vehicles = 1000f, People = 1000f,
                AverageDelay = average, WorstSaturation = saturation,
            };
        }

        [Fact]
        public void AMeasuredLayoutThatJammedOnceBeatsAMeasuredOneTwiceAsSlow()
        {
            // Junction 894465 on 28 Sep 2026: near capacity by the model.
            // Split waited 1.28 times the model and jammed in one of three
            // periods; Permissive waited 2.74 times the model without
            // jamming, about twice as long as Split.
            var e = new PlanEstimate[JunctionAdvisor.Strategies.Length];
            e[Index(PlanStrategy.Permissive)] = Estimate(38.7f, 1.09f);
            e[Index(PlanStrategy.ProtectedTurns)] = Estimate(39.7f, 1.2f);
            e[Index(PlanStrategy.Split)] = Estimate(36.9f, 1.03f);
            e[Index(PlanStrategy.ExclusivePedestrian)] = Estimate(50.7f, 1.1f);
            var memory = new LayoutMemory();
            int split = Index(PlanStrategy.Split);
            memory.Record(split, false, 36.9f * 1.28f, 36.9f, false);
            memory.Record(split, false, 36.9f * 1.28f, 36.9f, false);
            memory.Record(split, false, 36.9f * 1.28f, 36.9f, true);
            memory.Record(Index(PlanStrategy.Permissive), false, 38.7f * 2.74f, 38.7f, false);
            memory.Record(Index(PlanStrategy.Permissive), false, 38.7f * 2.74f, 38.7f, false);
            Assert.True(memory.Get(split, false).Backlog >= LayoutMemory.BacklogShare);

            PlanEstimate[] corrected = JunctionAdvisor.Correct(e, memory, split, false);

            Assert.Equal(PlanStrategy.Split, JunctionAdvisor.Choose(PlanStrategy.Split, corrected));
        }

        [Fact]
        public void AMeasuredLayoutThatAlwaysJamsLosesToOneALittleSlower()
        {
            var e = new PlanEstimate[JunctionAdvisor.Strategies.Length];
            for (int i = 0; i < e.Length; i++)
                e[i] = Estimate(60f, 0.9f);
            e[Index(PlanStrategy.Permissive)] = Estimate(30f, 0.9f);
            e[Index(PlanStrategy.Split)] = Estimate(30f, 0.9f);
            var memory = new LayoutMemory();
            int split = Index(PlanStrategy.Split);
            for (int period = 0; period < 3; period++)
                memory.Record(split, false, 47f, 30f, true);
            memory.Record(Index(PlanStrategy.Permissive), false, 60f, 30f, false);

            PlanEstimate[] corrected = JunctionAdvisor.Correct(e, memory, split, false);

            Assert.Equal(PlanStrategy.Permissive, JunctionAdvisor.Choose(PlanStrategy.Split, corrected));
        }

        [Fact]
        public void AMeasuredLayoutThatFlowedCopesWhateverTheModel()
        {
            var e = SplitLooksBest();
            e[Index(PlanStrategy.Permissive)].WorstSaturation = 1.3f;
            var memory = new LayoutMemory();
            memory.Record(Index(PlanStrategy.Permissive), false, 30f, 30f, false);
            PlanEstimate[] corrected = JunctionAdvisor.Correct(e, memory, Index(PlanStrategy.Permissive), false);
            Assert.True(corrected[Index(PlanStrategy.Permissive)].WorstSaturation <= JunctionAdvisor.Capacity);
        }

        [Fact]
        public void BacklogOfALayoutNotRunningFadesWithinTheDay()
        {
            var memory = new LayoutMemory();
            int split = Index(PlanStrategy.Split);
            memory.Record(split, false, 40f, 20f, true);
            for (int review = 0; review < 4; review++)
                memory.Fade(Index(PlanStrategy.Permissive));
            Assert.True(memory.Get(split, false).Backlog >= LayoutMemory.BacklogShare, "gone after 4 reviews");
            memory.Fade(Index(PlanStrategy.Permissive));
            Assert.True(memory.Get(split, false).Backlog < LayoutMemory.BacklogShare, "still there after 5 reviews");
        }

        [Fact]
        public void TheRunningLayoutKeepsItsBacklog()
        {
            var memory = new LayoutMemory();
            int split = Index(PlanStrategy.Split);
            memory.Record(split, false, 40f, 20f, true);
            for (int review = 0; review < 10; review++)
                memory.Fade(split);
            Assert.Equal(1f, memory.Get(split, false).Backlog);
        }

        [Fact]
        public void OneBacklogIsEnoughOneCleanPeriodIsNot()
        {
            var memory = new LayoutMemory();
            int layout = Index(PlanStrategy.Permissive);
            memory.Record(layout, false, 20f, 20f, false);
            memory.Record(layout, false, 20f, 20f, true);
            Assert.True(memory.Get(layout, false).Backlog >= LayoutMemory.BacklogShare);
            memory.Record(layout, false, 20f, 20f, false);
            Assert.True(memory.Get(layout, false).Backlog < LayoutMemory.BacklogShare);
        }

        [Fact]
        public void WaveAndAloneAreKeptApart()
        {
            var memory = new LayoutMemory();
            int layout = Index(PlanStrategy.Permissive);
            memory.Record(layout, true, 40f, 20f, false);
            Assert.False(memory.Get(layout, false).Measured);
            Assert.Equal(2f, memory.Get(layout, true).Factor, 3);
        }

        [Fact]
        public void AWaveThatMakesItsJunctionsWaitLongerHurts()
        {
            Calibration Measured(float factor, float backlog = 0f)
            {
                return new Calibration { Factor = factor, Samples = JunctionAdvisor.WaveSamples, Backlog = backlog };
            }
            float[] weights = { 800f, 400f };
            Assert.True(JunctionAdvisor.WaveHurts(new[] { Measured(1f), Measured(1f) }, new[] { Measured(1.3f), Measured(1.1f) }, weights));
            Assert.False(JunctionAdvisor.WaveHurts(new[] { Measured(1f), Measured(1f) }, new[] { Measured(0.7f), Measured(1.1f) }, weights));
            // A little faster on average, but a queue where there was none.
            Assert.True(JunctionAdvisor.WaveHurts(new[] { Measured(1f), Measured(1f) }, new[] { Measured(0.9f, 1f), Measured(0.9f) }, weights));
            // Not measured alone yet: no verdict.
            Assert.False(JunctionAdvisor.WaveHurts(new[] { default(Calibration), default(Calibration) }, new[] { Measured(2f), Measured(2f) }, weights));
        }
    }
}
