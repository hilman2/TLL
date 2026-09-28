using TLL.Core.Control;
using Xunit;

namespace TLL.Core.Tests.Control
{
    public class ApproachSensorTests
    {
        private struct Samples : IVehicleSamples
        {
            public (float distance, float speed)[] Vehicles;

            public int Count => Vehicles.Length;

            public float Distance(int index) => Vehicles[index].distance;

            public float Speed(int index) => Vehicles[index].speed;
        }

        private static ApproachReading Read(params (float distance, float speed)[] vehicles)
        {
            var samples = new Samples { Vehicles = vehicles };
            return ApproachSensor.Read(ref samples);
        }

        [Fact]
        public void DischargingQueueStillCountsAsWaiting()
        {
            // The queue has started to move: the first car crosses the line,
            // the ones behind accelerate. None of them stands any more, and
            // most are more than the passage time away.
            ApproachReading r = Read((2f, 6f), (10f, 4.5f), (18f, 3.5f), (26f, 2.5f), (34f, 2f), (41f, 1.6f));
            Assert.Equal(6f, r.Waiting);
        }

        [Fact]
        public void FastLeadersDoNotCutOffTheQueueBehind()
        {
            // A queue a few seconds into its green: the first car has picked
            // up speed and drives through, the ones behind are still
            // accelerating or standing.
            ApproachReading r = Read((3f, 10f), (14f, 9f), (24f, 6f), (33f, 4f), (41f, 2f), (48f, 0f), (55f, 0f));
            Assert.Equal(6f, r.Waiting);
            Assert.Equal(1f, r.Soon);
        }

        [Fact]
        public void BusesAndTrucksDoNotBreakTheQueue()
        {
            // A discharging queue with an articulated bus as its third
            // vehicle: the car behind it has its front 21 m back, the bus's
            // length and the room left behind it. It is still the same queue.
            ApproachReading r = Read((2f, 6f), (10f, 4.5f), (18f, 3.5f), (39f, 2.5f), (47f, 2f), (54f, 1.6f));
            Assert.Equal(6f, r.Waiting);
        }

        [Fact]
        public void QueueEndsAtTheFirstGap()
        {
            ApproachReading r = Read((3f, 0f), (10f, 0f), (17f, 0f), (80f, 12f));
            Assert.Equal(3f, r.Waiting);
            Assert.Equal(1f, r.Near);
            Assert.Equal(1f, r.Arriving);
        }

        [Fact]
        public void FreeFlowingPlatoonKeepsTheGreenButDoesNotWait()
        {
            // Cars 1 to 3 follow each other at about 2 s headway: a real
            // actuated controller keeps the green for them (gap below the 3 s
            // passage time). None of them waits. The fourth comes after a real
            // gap and only counts as arriving.
            ApproachReading r = Read((5f, 14f), (20f, 14f), (49f, 14f), (130f, 14f));
            Assert.Equal(0f, r.Waiting);
            Assert.Equal(3f, r.Soon);
            Assert.Equal(0f, r.Near);
            Assert.Equal(1f, r.Arriving);
        }

        [Fact]
        public void StandingBehindAGapStillWaits()
        {
            ApproachReading r = Read((120f, 0f));
            Assert.Equal(1f, r.Waiting);
        }
    }
}
