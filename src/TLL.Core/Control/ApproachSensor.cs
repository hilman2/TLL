namespace TLL.Core.Control
{
    /// <summary>Vehicles on one approach, ordered by distance to the stop line, nearest first.</summary>
    public interface IVehicleSamples
    {
        int Count { get; }

        /// <summary>Distance to the stop line, metres.</summary>
        float Distance(int index);

        /// <summary>Speed, metres per second.</summary>
        float Speed(int index);
    }

    /// <summary>What the detectors of one approach report, in vehicles.</summary>
    public struct ApproachReading
    {
        /// <summary>
        /// The queue: vehicles in the queue that starts at the stop line, and
        /// behind it every vehicle slower than <see cref="ApproachSensor.QueueSpeed"/>.
        /// </summary>
        public float Waiting;

        /// <summary>Moving vehicles, not in the queue, that reach the line within the passage time.</summary>
        public float Soon;

        /// <summary>Moving vehicles that reach the line within the hold horizon.</summary>
        public float Near;

        /// <summary>Moving vehicles that reach the line within the planning horizon; includes Near.</summary>
        public float Arriving;
    }

    /// <summary>
    /// Turns the vehicles on an approach into detector readings, as a radar
    /// at the stop line would count them.
    ///
    /// The queue is the chain of vehicles from the stop line back to the
    /// first real gap, whether they stand or have started to roll. Counting
    /// only standing vehicles would lose a queue the moment it starts to
    /// discharge: the vehicles behind the first ones accelerate slowly and
    /// are seconds away from the line, so the green would end with most of
    /// the queue still there.
    /// </summary>
    public static class ApproachSensor
    {
        /// <summary>
        /// Passage time in seconds: a vehicle this close to the line keeps
        /// the green, as the gap setting of an actuated controller.
        /// </summary>
        public const float PassageTime = 3f;

        /// <summary>A moving vehicle this close to the line, in metres, counts as at the line.</summary>
        public const float StopLineReach = 12f;

        /// <summary>Arrivals within this many seconds may hold a green in the adaptive mode (see PhaseData.Approaching).</summary>
        public const float HoldHorizon = 8f;

        /// <summary>Planning horizon in seconds: arrivals within it count towards pressure.</summary>
        public const float Horizon = 15f;

        /// <summary>
        /// Largest gap in metres, front to front, between two standing
        /// vehicles of one queue: an articulated bus or a truck with trailer
        /// plus the room a driver leaves behind it. Sized for cars, the queue
        /// ended behind every bus, and the vehicles there dropped out of the
        /// count as soon as they rolled.
        /// </summary>
        public const float QueueGap = 22f;

        /// <summary>
        /// Seconds of headway added to the allowed gap per m/s of speed: a
        /// queue pulls apart as it accelerates, and is still one queue while
        /// each vehicle follows the one ahead this closely.
        /// </summary>
        public const float QueueHeadway = 2f;

        /// <summary>
        /// A vehicle of the queue faster than this, in m/s, is driving
        /// through: it counts as soon at the line, not as waiting. 36 km/h,
        /// most of the way to the speed of a town road.
        /// </summary>
        public const float QueueSpeed = 10f;

        public static ApproachReading Read<TVehicles>(ref TVehicles vehicles)
            where TVehicles : struct, IVehicleSamples
        {
            var reading = new ApproachReading();
            float queueEnd = 0f;
            bool inQueue = true;
            for (int i = 0; i < vehicles.Count; i++)
            {
                float distance = vehicles.Distance(i);
                float speed = vehicles.Speed(i);
                // The first cars of a queue that has started to move are the
                // fast ones; they must not cut off the queue behind them.
                if (inQueue && distance - queueEnd <= QueueGap + QueueHeadway * speed)
                {
                    if (speed < QueueSpeed)
                        reading.Waiting += 1f;
                    else
                        reading.Soon += 1f;
                    queueEnd = distance;
                    continue;
                }
                inQueue = false;
                if (speed < QueueSpeed)
                {
                    // Behind a gap, but slow: the rest of a queue that tore
                    // apart as its first cars pulled away, one slowing down to
                    // join it, or one held up by something else. Waiting all
                    // the same. Counted as arriving, a queue moving off
                    // dropped out of the count, and its green ended with most
                    // of it still to come.
                    reading.Waiting += 1f;
                    continue;
                }
                float eta = distance / speed;
                if (eta <= PassageTime || distance <= StopLineReach)
                {
                    reading.Soon += 1f;
                    continue;
                }
                if (eta <= HoldHorizon)
                    reading.Near += 1f;
                if (eta <= Horizon)
                    reading.Arriving += 1f;
            }
            return reading;
        }
    }
}
