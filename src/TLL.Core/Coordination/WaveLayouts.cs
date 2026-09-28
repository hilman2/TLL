using System.Collections.Generic;
using TLL.Core.Optimization;

namespace TLL.Core.Coordination
{
    /// <summary>
    /// Layouts for the junctions of a corridor that let a green wave run on
    /// it. Each junction picks its layout for itself, and the layouts that
    /// suit one junction best, with a phase per approach or one for the
    /// pedestrians, leave the corridor's through traffic too short a green
    /// for a band. Given what each member can afford instead, this picks the
    /// fewest changes that make the band worth running.
    /// </summary>
    public static class WaveLayouts
    {
        /// <summary>
        /// Chooses per member which of its options to run.
        /// </summary>
        /// <param name="options">
        /// Per member, the layouts it can run as the coordinator sees them:
        /// index 0 the one it runs now, the others alternatives it can afford.
        /// </param>
        /// <param name="hasA">Whether any member has through traffic in direction A; likewise hasB.</param>
        /// <param name="plan">The plan for the choice returned, or null.</param>
        /// <returns>
        /// Per member the index of the option to run, or null if no choice
        /// gives a band worth running (Coupling.BandWorthIt for a new wave).
        /// </returns>
        public static int[] Choose(CorridorPath path, IList<IList<CorridorMember>> options, bool hasA, bool hasB, OptimizerLimits limits,
            out CoordinationPlan plan)
        {
            int n = options.Count;
            var choice = new int[n];
            plan = Plan(path, options, choice, limits);
            float score = Score(plan, hasA, hasB);
            // Greedy: each pass takes the one change that widens the band
            // most, so that no member changes that the band does not need.
            for (int pass = 0; pass < n; pass++)
            {
                if (Coupling.BandWorthIt(plan.BandwidthA, plan.BandwidthB, plan.Cycle, hasA, hasB, false))
                    return choice;
                int bestMember = -1;
                int bestOption = 0;
                CoordinationPlan bestPlan = null;
                float bestScore = score;
                for (int k = 0; k < n; k++)
                {
                    if (choice[k] != 0)
                        continue;
                    for (int o = 1; o < options[k].Count; o++)
                    {
                        choice[k] = o;
                        CoordinationPlan trial = Plan(path, options, choice, limits);
                        float trialScore = Score(trial, hasA, hasB);
                        if (trialScore > bestScore)
                        {
                            bestScore = trialScore;
                            bestMember = k;
                            bestOption = o;
                            bestPlan = trial;
                        }
                    }
                    choice[k] = 0;
                }
                if (bestMember < 0)
                    break;
                choice[bestMember] = bestOption;
                plan = bestPlan;
                score = bestScore;
            }
            if (Coupling.BandWorthIt(plan.BandwidthA, plan.BandwidthB, plan.Cycle, hasA, hasB, false))
                return choice;
            plan = null;
            return null;
        }

        private static CoordinationPlan Plan(CorridorPath path, IList<IList<CorridorMember>> options, int[] choice, OptimizerLimits limits)
        {
            var members = new List<CorridorMember>(options.Count);
            for (int k = 0; k < options.Count; k++)
                members.Add(options[k][choice[k]]);
            return Coordinator.Plan(path, members, limits);
        }

        /// <summary>The band as a share of the cycle, as Coupling.BandWorthIt weighs it.</summary>
        private static float Score(CoordinationPlan plan, bool hasA, bool hasB)
        {
            if (plan.Cycle <= 0)
                return 0f;
            if (hasA && hasB)
                return (plan.BandwidthA + plan.BandwidthB) / (2f * plan.Cycle);
            return (hasA ? plan.BandwidthA : plan.BandwidthB) / (float)plan.Cycle;
        }
    }
}
