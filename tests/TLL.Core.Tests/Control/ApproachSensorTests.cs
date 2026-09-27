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
        public void QueueEndsAtTheFirstGap()
        {
            ApproachReading r = Read((3f, 0f), (10f, 0f), (17f, 0f), (80f, 12f));
            Assert.Equal(3f, r.Waiting);
            Assert.Equal(1f, r.Near);
            Assert.Equal(1f, r.Arriving);
        }

        [Fact]
        public void FastTrafficIsNotAQueue()
        {
            // Free-flowing traffic close together is not waiting; the first
            // car is at the line, the second 2 s away, the third 3.5 s.
            ApproachReading r = Read((5f, 14f), (20f, 14f), (49f, 14f));
            Assert.Equal(0f, r.Waiting);
            Assert.Equal(2f, r.Soon);
            Assert.Equal(1f, r.Near);
        }

        [Fact]
        public void StandingBehindAGapStillWaits()
        {
            ApproachReading r = Read((120f, 0f));
            Assert.Equal(1f, r.Waiting);
        }
    }
}
