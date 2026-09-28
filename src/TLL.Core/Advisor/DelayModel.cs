using System;
using TLL.Core.Planning;

namespace TLL.Core.Advisor
{
    public struct DelayParameters
    {
        /// <summary>Vehicles per hour one lane discharges on green, with nobody to give way to.</summary>
        public float SaturationPerLane;

        /// <summary>Seconds of yellow, all-red and start-up lost per phase change.</summary>
        public float Intergreen;

        public float MinGreen;

        /// <summary>
        /// Shortest green a phase with a crosswalk gets, for the walk: 7 s of
        /// walk plus the clearance of a typical 15 m crosswalk at 1.2 m/s,
        /// less yellow and all-red, as JunctionInitSystem times the walk.
        /// </summary>
        public float PedestrianMinGreen;

        public float MinCycle;
        public float MaxCycle;

        /// <summary>Analysis period of the HCM incremental delay, in hours.</summary>
        public float Period;

        /// <summary>Short turns may go on red where PhasePlanner.TurnOnRed allows it.</summary>
        public bool TurnOnRed;

        public static DelayParameters Default => new DelayParameters
        {
            SaturationPerLane = 1800f,
            Intergreen = 4.3f,
            MinGreen = 5f,
            PedestrianMinGreen = 15f,
            MinCycle = 40f,
            MaxCycle = 120f,
            Period = 0.25f,
        };
    }

    /// <summary>What a plan is expected to cost, from <see cref="DelayModel.Estimate"/>.</summary>
    public struct PlanEstimate
    {
        /// <summary>Total delay of everybody arriving in one hour, in seconds.</summary>
        public float TotalDelay;

        /// <summary>Mean delay per road user in seconds.</summary>
        public float AverageDelay;

        /// <summary>
        /// The vehicles' part of <see cref="TotalDelay"/>, and how many
        /// vehicles arrive in the hour. The rest is the pedestrians'.
        /// </summary>
        public float VehicleDelay;
        public float Vehicles;

        /// <summary>Road users arriving in one hour, vehicles and pedestrians.</summary>
        public float People;

        public float Cycle;

        /// <summary>Highest volume-to-capacity ratio of any movement; above 1 the junction cannot keep up.</summary>
        public float WorstSaturation;

        /// <summary>
        /// The junction has run this layout, and the delay is corrected by
        /// what it measured (JunctionAdvisor.Correct); <see cref="Backlog"/>
        /// then says how often its queues did not clear, from 0 to 1.
        /// </summary>
        public bool Measured;
        public float Backlog;

        public int Phases;

        /// <summary>Volume-to-capacity ratio per movement; 0 for movements without traffic and for crosswalks.</summary>
        public float[] Saturation;

        /// <summary>Mean delay per movement in seconds.</summary>
        public float[] Delay;

        /// <summary>Critical flow ratio and green, in seconds, per phase.</summary>
        public float[] PhaseRatio;
        public float[] PhaseGreen;
    }

    /// <summary>
    /// Estimates the delay a signal plan causes for given volumes, the way
    /// traffic engineers compare signal plans on paper.
    ///
    /// Cycle and greens follow Webster from the phases' critical flow ratios.
    /// A movement's delay is the HCM signal delay: a uniform part from the red
    /// it sees, plus an incremental part that grows sharply as the movement
    /// nears capacity and stays finite beyond it. A movement that has to give
    /// way loses capacity to the flow it gives way to: to vehicles by the
    /// HCM gap acceptance formula, to pedestrians by a linear friction.
    /// Pedestrians wait for their green, on average half the red.
    /// </summary>
    public static class DelayModel
    {
        /// <summary>Critical gap and follow-up time of a turn giving way to oncoming traffic, in seconds (HCM).</summary>
        private const float CriticalGap = 4.5f;
        private const float FollowUp = 2.5f;

        /// <summary>
        /// Critical gap and follow-up time of a turn on red, which starts from
        /// a stop like a side road turning right at a stop sign (HCM).
        /// </summary>
        private const float OnRedCriticalGap = 6.2f;
        private const float OnRedFollowUp = 3.3f;

        /// <summary>Pedestrians per hour on a crosswalk at which turning traffic across it comes to a stand.</summary>
        private const float PedestrianBlockage = 2000f;

        /// <summary>Walking speed for the crossing time of a crosswalk, in m/s.</summary>
        public const float WalkingSpeed = 1.2f;

        /// <param name="volumes">Per movement, vehicles or pedestrians per hour.</param>
        public static PlanEstimate Estimate(JunctionModel junction, PhasePlan plan, float[] volumes, DelayParameters p)
        {
            int n = junction.Movements.Count;
            int phases = plan.Phases.Count;
            var saturation = new float[phases, n];

            // Saturation flow of every movement in every phase that gives it green.
            for (int ph = 0; ph < phases; ph++)
            {
                Phase phase = plan.Phases[ph];
                for (int m = 0; m < n; m++)
                {
                    if (!phase.Has(m) || junction.Movements[m].IsPedestrian)
                        continue;
                    float s = Nominal(junction, m, volumes, p);
                    if ((phase.Permitted & (1UL << m)) != 0)
                        s *= YieldFactor(junction, phase, m, volumes, p, CriticalGap, FollowUp);
                    saturation[ph, m] = s;
                }
            }

            // Turn on red: a short turn with red in a phase goes through the
            // gaps in what that phase lets run. It adds to the turn's capacity
            // but asks for no green time; the greens are planned as without it.
            var onRed = new float[phases, n];
            if (p.TurnOnRed)
            {
                for (int ph = 0; ph < phases; ph++)
                {
                    Phase phase = plan.Phases[ph];
                    ulong allowed = PhasePlanner.TurnOnRed(junction, phase.Green);
                    for (int m = 0; m < n; m++)
                    {
                        if ((allowed & (1UL << m)) != 0)
                            onRed[ph, m] = Nominal(junction, m, volumes, p) * YieldFactor(junction, phase, m, volumes, p, OnRedCriticalGap, OnRedFollowUp);
                    }
                }
            }

            // Split a movement's volume over the phases that serve it, in
            // proportion to how much each can move, and take each phase's
            // most demanding movement as its critical flow ratio.
            var ratio = new float[phases];
            float y = 0f;
            for (int ph = 0; ph < phases; ph++)
            {
                for (int m = 0; m < n; m++)
                {
                    float s = saturation[ph, m];
                    if (s <= 0f)
                        continue;
                    float total = 0f;
                    for (int q = 0; q < phases; q++)
                        total += saturation[q, m];
                    float share = volumes[m] * s / total;
                    ratio[ph] = Math.Max(ratio[ph], share / s);
                }
                y += ratio[ph];
            }

            float lost = phases * p.Intergreen;
            float[] minimum = MinimumGreens(junction, plan, volumes, p);
            float cycle = PlanCycle(ratio, y, minimum, lost, p);
            float[] green = PlanGreens(ratio, y, minimum, lost, cycle);

            var estimate = new PlanEstimate
            {
                Cycle = cycle,
                Phases = phases,
                Saturation = new float[n],
                Delay = new float[n],
                PhaseRatio = ratio,
                PhaseGreen = green,
            };
            float people = 0f;
            for (int m = 0; m < n; m++)
            {
                float v = volumes[m];
                if (v <= 0f)
                    continue;
                float effective = 0f;
                float capacity = 0f;
                for (int ph = 0; ph < phases; ph++)
                {
                    if (plan.Phases[ph].Has(m))
                    {
                        effective += green[ph];
                        capacity += saturation[ph, m] * green[ph] / cycle;
                    }
                    else if (onRed[ph, m] > 0f)
                    {
                        // Counts as that part of a green, for the uniform
                        // delay, as its flow is of the full one.
                        effective += green[ph] * onRed[ph, m] / Nominal(junction, m, volumes, p);
                        capacity += onRed[ph, m] * green[ph] / cycle;
                    }
                }
                float delay;
                if (junction.Movements[m].IsPedestrian)
                {
                    float red = cycle - effective;
                    delay = red * red / (2f * cycle);
                }
                else
                {
                    delay = SignalDelay(v, capacity, effective / cycle, cycle, p.Period, out float x);
                    estimate.WorstSaturation = Math.Max(estimate.WorstSaturation, x);
                    estimate.Saturation[m] = x;
                    estimate.VehicleDelay += v * delay;
                    estimate.Vehicles += v;
                }
                estimate.Delay[m] = delay;
                estimate.TotalDelay += v * delay;
                people += v;
            }
            estimate.AverageDelay = people > 0f ? estimate.TotalDelay / people : 0f;
            estimate.People = people;
            return estimate;
        }

        /// <summary>
        /// HCM signal delay per vehicle in seconds: uniform delay plus
        /// incremental delay, the latter valid below and above capacity.
        /// </summary>
        public static float SignalDelay(float volume, float capacity, float greenRatio, float cycle, float period, out float saturation)
        {
            if (capacity <= 0f)
            {
                saturation = float.PositiveInfinity;
                return 3600f * period;
            }
            float x = volume / capacity;
            saturation = x;
            float g = Clamp(greenRatio, 0f, 1f);
            float uniform = 0.5f * cycle * (1f - g) * (1f - g) / (1f - Math.Min(1f, x) * g);
            float over = x - 1f;
            float incremental = 900f * period * (over + (float)Math.Sqrt(over * over + 4f * x / (capacity * period)));
            return uniform + incremental;
        }

        /// <summary>
        /// Share of its saturation flow a movement keeps while giving way to
        /// the other movements green in the same phase.
        /// </summary>
        private static float YieldFactor(JunctionModel junction, Phase phase, int m, float[] volumes, DelayParameters p, float criticalGap, float followUp)
        {
            float opposing = 0f;
            float pedestrians = 0f;
            for (int o = 0; o < junction.Movements.Count; o++)
            {
                if (!phase.Has(o) || junction.Conflicts.Get(m, o) != Relation.Yields)
                    continue;
                if (junction.Movements[o].IsPedestrian)
                    pedestrians += volumes[o];
                else
                    opposing += volumes[o];
            }
            // The gap capacity is the movement's flow in veh/h (HCM). Even
            // with nobody to give way to, a turning vehicle follows at the
            // follow-up headway, not at the saturation headway, as the HCM
            // also discounts unopposed turns.
            float gaps = GapCapacity(opposing, criticalGap, followUp) / p.SaturationPerLane;
            float walk = 1f - pedestrians / PedestrianBlockage;
            return Clamp(Math.Min(gaps, 1f) * walk, 0.05f, 1f);
        }

        /// <summary>
        /// The part of its lanes' flow a movement can use. Lanes shared with
        /// other movements (JunctionModel.SharedLane) carry their vehicles
        /// too, so the movement gets the lanes in proportion to its own share
        /// of the traffic on them. Its lane count counts each shared lane in
        /// full, so without this a lane for left, straight and right would
        /// count as three.
        /// </summary>
        /// <summary>Saturation flow of a movement on green with nobody to give way to, in vehicles per hour.</summary>
        private static float Nominal(JunctionModel junction, int m, float[] volumes, DelayParameters p)
        {
            return p.SaturationPerLane * junction.Movements[m].LaneCount * LaneShare(junction, m, volumes);
        }

        private static float LaneShare(JunctionModel junction, int m, float[] volumes)
        {
            ulong partners = junction.SharesLaneWith(m);
            if (partners == 0 || volumes[m] <= 0f)
                return 1f;
            float together = volumes[m];
            for (int o = 0; o < junction.Movements.Count; o++)
            {
                if ((partners & (1UL << o)) != 0)
                    together += volumes[o];
            }
            return volumes[m] / together;
        }

        /// <summary>
        /// Vehicles per hour that can enter gaps in a conflicting flow (HCM):
        /// c = v e^(-v tc / 3600) / (1 - e^(-v tf / 3600)). Without
        /// conflicting flow this is the follow-up headway rate, 3600 / tf.
        /// </summary>
        public static float GapCapacity(float conflicting, float criticalGap, float followUp)
        {
            if (conflicting < 1f)
                return 3600f / followUp;
            double v = conflicting;
            return (float)(v * Math.Exp(-v * criticalGap / 3600.0) / (1.0 - Math.Exp(-v * followUp / 3600.0)));
        }

        /// <summary>Degree of saturation the planned greens aim at, as a traffic engineer times a signal.</summary>
        private const float TargetSaturation = 0.9f;

        /// <summary>
        /// Webster's cycle, lengthened until every phase gets its minimum
        /// green and a green long enough for its critical flow at the target
        /// saturation: C = L + sum of max(minimum, y C / x). Webster alone
        /// ignores the minimum greens; where they take up most of the cycle,
        /// the busiest phase would be left short and overloaded at low
        /// traffic, and delays would fall as traffic grows.
        /// </summary>
        private static float PlanCycle(float[] ratio, float y, float[] minimum, float lost, DelayParameters p)
        {
            float cycle = y >= 0.95f ? p.MaxCycle : (1.5f * lost + 5f) / (1f - y);
            cycle = Clamp(cycle, p.MinCycle, p.MaxCycle);
            // The need grows with the cycle at a slope of y / x, below 1
            // while the junction can cope, so this settles; otherwise the
            // cycle ends at its maximum.
            for (int i = 0; i < 50 && cycle < p.MaxCycle; i++)
            {
                float need = lost;
                for (int ph = 0; ph < ratio.Length; ph++)
                    need += Math.Max(minimum[ph], ratio[ph] * cycle / TargetSaturation);
                if (need <= cycle + 0.01f)
                    break;
                cycle = Math.Min(need, p.MaxCycle);
            }
            return cycle;
        }

        /// <summary>
        /// Greens for a cycle: each phase its minimum or its need at the
        /// target saturation, whichever is longer; time left over goes by
        /// flow ratio. When the cycle is capped below the need, the part
        /// above the minimums is cut back in proportion.
        /// </summary>
        private static float[] PlanGreens(float[] ratio, float y, float[] minimum, float lost, float cycle)
        {
            int phases = ratio.Length;
            var green = new float[phases];
            float total = 0f;
            float minimums = 0f;
            for (int ph = 0; ph < phases; ph++)
            {
                green[ph] = Math.Max(minimum[ph], ratio[ph] * cycle / TargetSaturation);
                total += green[ph];
                minimums += minimum[ph];
            }
            float available = cycle - lost;
            if (available >= total)
            {
                float spare = available - total;
                for (int ph = 0; ph < phases; ph++)
                    green[ph] += y > 0f ? spare * ratio[ph] / y : spare / phases;
                return green;
            }
            float above = total - minimums;
            float keep = above > 0f ? Math.Max(0f, available - minimums) / above : 0f;
            for (int ph = 0; ph < phases; ph++)
                green[ph] = minimum[ph] + (green[ph] - minimum[ph]) * keep;
            return green;
        }

        /// <summary>Cycle assumed for the chance of a pedestrian call before the cycle is known, seconds.</summary>
        private const float CallCycle = 60f;

        /// <summary>
        /// Expected shortest green of every phase. With the push button a
        /// phase needs the walk time only in the cycles in which someone
        /// calls; with pedestrians arriving at random that is 1 - e^(-v C /
        /// 3600) of the cycles. A pedestrian walks with the first green that
        /// serves the crosswalk, so each crosswalk is charged to the first
        /// phase that has it, not to every one.
        /// </summary>
        private static float[] MinimumGreens(JunctionModel junction, PhasePlan plan, float[] volumes, DelayParameters p)
        {
            int phases = plan.Phases.Count;
            var pedestrians = new float[phases];
            for (int m = 0; m < junction.Movements.Count; m++)
            {
                if (!junction.Movements[m].IsPedestrian)
                    continue;
                for (int ph = 0; ph < phases; ph++)
                {
                    if (plan.Phases[ph].Has(m))
                    {
                        pedestrians[ph] += volumes[m];
                        break;
                    }
                }
            }
            var green = new float[phases];
            for (int ph = 0; ph < phases; ph++)
            {
                float call = 1f - (float)Math.Exp(-pedestrians[ph] * CallCycle / 3600f);
                green[ph] = p.MinGreen + (p.PedestrianMinGreen - p.MinGreen) * call;
            }
            return green;
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
