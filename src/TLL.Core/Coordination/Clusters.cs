using System;

namespace TLL.Core.Coordination
{
    /// <summary>
    /// Signals so close together that the road between them cannot hold the
    /// queue of one red: a cluster (in German practice a Knotenpunktverbund).
    ///
    /// Run on their own, each with its cycle, such signals send traffic into
    /// a road whose far end is red. The road fills within seconds, the queue
    /// backs up into the junction that sent it, and the next green there,
    /// the side road's turn onto the main road above all, moves nobody.
    /// Joined, they share one cycle with offsets chosen so that no green
    /// feeds a road whose far end will be red when the traffic arrives
    /// (<see cref="GreenWave"/> with <see cref="Corridor.Feeds"/>), and a
    /// green into a full road waits for room while the far end clears it.
    ///
    /// A cluster is coordinated whatever green band it leaves the main road
    /// (<see cref="Coupling.BandWorthIt"/>): the band decides whether a
    /// green wave pays off, but in a cluster the alternative is the jam.
    /// </summary>
    public static class Clusters
    {
        /// <summary>Road length a queued vehicle takes up, including the gap to the next one, in metres.</summary>
        public const float VehicleSpacing = 7f;

        /// <summary>
        /// The red the storage of a road is weighed against, in seconds: what
        /// a main road waits while its side roads and turns have green.
        /// </summary>
        public const float RedSeconds = 40f;

        /// <summary>
        /// Share of a road's storage the queue of one red may take before
        /// the road counts as too short. The rest is for the traffic that
        /// arrives while the queue starts moving, and for turning vehicles.
        /// </summary>
        public const float StorageShare = 0.5f;

        /// <summary>A road up to this length in metres is always too short: a handful of cars per lane.</summary>
        public const float AlwaysTight = 100f;

        /// <summary>A road from this length in metres never is; there platoons are a matter for the green wave.</summary>
        public const float NeverTight = 250f;

        /// <summary>
        /// Two-way traffic in vehicles per hour below which two signals are
        /// not joined, however close: too few cars to fill the road.
        /// </summary>
        public const float MinimumVolume = 150f;

        /// <summary>Seconds a queue needs to start moving at green.</summary>
        public const float StartLoss = 2f;

        /// <summary>Seconds between two vehicles leaving a queue, per lane.</summary>
        public const float Headway = 2f;

        /// <summary>
        /// Most vehicles per lane the lead waits for: the front of a queue.
        /// The rest of it leaves while the side road's traffic is on its way.
        /// </summary>
        public const float LeadVehicles = 2f;

        /// <summary>
        /// Weight of the main road's through traffic reaching the next
        /// junction at red, against the side road's turns onto the main road
        /// doing so (<see cref="Feed.Weight"/>).
        ///
        /// On a road this short the next junction always gets traffic from
        /// this one: the main road's platoon while the main road is green
        /// here, the side road's turns while it is red. Its own red stops one
        /// of them. The platoon fits into the road and leaves at the next
        /// green there. The side road gets green only for a few seconds, and
        /// with the road full, or its far end red, nobody moves in them, and
        /// its queue waits a whole cycle more. So the side road's turns come
        /// first, and the platoon's stop only decides between offsets that
        /// serve them equally well.
        /// </summary>
        public const float ThroughFeedWeight = 0.25f;

        /// <summary>
        /// Vehicles of room a road of a cluster must have left, counted from
        /// the queue at its far end, to take more traffic. Below that a green
        /// into it waits, and the far end is asked to empty it.
        /// </summary>
        public const float MinRoom = 3f;

        /// <summary>Vehicles one direction of a road holds standing.</summary>
        /// <param name="length">Metres from stop line to stop line.</param>
        /// <param name="lanes">Car lanes arriving at the far end.</param>
        public static float Capacity(float length, int lanes)
        {
            if (length <= 0f || lanes <= 0)
                return 0f;
            return lanes * length / VehicleSpacing;
        }

        /// <summary>Whether one direction of a road is too short for the queue of a red.</summary>
        /// <param name="length">Metres from stop line to stop line.</param>
        /// <param name="lanes">Car lanes arriving at the far end.</param>
        /// <param name="inflow">Vehicles per hour arriving at the far end from this road.</param>
        public static bool IsTight(float length, int lanes, float inflow)
        {
            if (length >= NeverTight || lanes <= 0)
                return false;
            if (length <= AlwaysTight)
                return true;
            float queue = inflow * RedSeconds / 3600f;
            return queue >= StorageShare * Capacity(length, lanes);
        }

        /// <summary>
        /// Whether two neighbouring signals must run as one: the road
        /// between them is too short in either direction, and enough traffic
        /// uses it to fill it.
        /// </summary>
        /// <param name="length">Metres between the stop lines.</param>
        /// <param name="lanesToA">Car lanes arriving at junction A; likewise <paramref name="lanesToB"/>.</param>
        /// <param name="inflowA">Vehicles per hour arriving at A from the road; likewise <paramref name="inflowB"/>.</param>
        public static bool Join(float length, int lanesToA, float inflowA, int lanesToB, float inflowB)
        {
            if (inflowA + inflowB < MinimumVolume)
                return false;
            return IsTight(length, lanesToA, inflowA) || IsTight(length, lanesToB, inflowB);
        }

        /// <summary>
        /// Steps the green at the far end of a road must have run before
        /// side traffic is let in at the near end: time for what stood at
        /// the far stop line to start moving and make room. The through
        /// traffic of the main road needs none; it arrives as the queue in
        /// front of it leaves.
        /// </summary>
        /// <param name="length">Metres between the stop lines.</param>
        public static int Lead(float length)
        {
            float perLane = Math.Max(0f, length) / VehicleSpacing;
            return SimTime.ToSteps(StartLoss + Headway * Math.Min(perLane, LeadVehicles));
        }
    }
}
