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

        /// <summary>Shortest green a phase with a crosswalk gets, for the walk.</summary>
        public float PedestrianMinGreen;

        public float MinCycle;
        public float MaxCycle;

        /// <summary>Analysis period of the HCM incremental delay, in hours.</summary>
        public float Period;

        public static DelayParameters Default => new DelayParameters
        {
            SaturationPerLane = 1800f,
            Intergreen = 4.3f,
            MinGreen = 5f,
            PedestrianMinGreen = 7f,
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

        public float Cycle;

        /// <summary>Highest volume-to-capacity ratio of any movement; above 1 the junction cannot keep up.</summary>
        public float WorstSaturation;

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
                    float s = p.SaturationPerLane * junction.Movements[m].LaneCount;
                    if ((phase.Permitted & (1UL << m)) != 0)
                        s *= YieldFactor(junction, phase, m, volumes, p);
                    saturation[ph, m] = s;
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
            float cycle = y >= 0.95f ? p.MaxCycle : (1.5f * lost + 5f) / (1f - y);
            cycle = Clamp(cycle, p.MinCycle, p.MaxCycle);

            var green = new float[phases];
            float minimums = 0f;
            for (int ph = 0; ph < phases; ph++)
            {
                green[ph] = HasCrosswalk(junction, plan.Phases[ph]) ? p.PedestrianMinGreen : p.MinGreen;
                minimums += green[ph];
            }
            cycle = Math.Max(cycle, minimums + lost);
            float spare = cycle - lost - minimums;
            for (int ph = 0; ph < phases; ph++)
                green[ph] += y > 0f ? spare * ratio[ph] / y : spare / phases;

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
                    if (!plan.Phases[ph].Has(m))
                        continue;
                    effective += green[ph];
                    capacity += saturation[ph, m] * green[ph] / cycle;
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
                }
                estimate.Delay[m] = delay;
                estimate.TotalDelay += v * delay;
                people += v;
            }
            estimate.AverageDelay = people > 0f ? estimate.TotalDelay / people : 0f;
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
        private static float YieldFactor(JunctionModel junction, Phase phase, int m, float[] volumes, DelayParameters p)
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
            float gaps = GapCapacity(opposing, CriticalGap, FollowUp) / p.SaturationPerLane;
            float walk = 1f - pedestrians / PedestrianBlockage;
            return Clamp(Math.Min(gaps, 1f) * walk, 0.05f, 1f);
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

        private static bool HasCrosswalk(JunctionModel junction, Phase phase)
        {
            for (int m = 0; m < junction.Movements.Count; m++)
            {
                if (phase.Has(m) && junction.Movements[m].IsPedestrian)
                    return true;
            }
            return false;
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
