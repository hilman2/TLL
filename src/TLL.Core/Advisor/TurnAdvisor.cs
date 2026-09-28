using TLL.Core.Planning;

namespace TLL.Core.Advisor
{
    /// <summary>
    /// Decides which turns of a signalled junction to forbid.
    ///
    /// A turn with little traffic can cost a junction a whole phase. Its
    /// vehicles queue in a lane they share with others, and the first vehicle
    /// holds up all behind it, so every movement from that lane can only have
    /// green when the turn can. A U-turn that crosses the busiest flow keeps
    /// the left turn and the straight traffic of its approach away from that
    /// flow, even when nobody turns. Forbidding it sends its few vehicles
    /// round the block and frees the plan.
    ///
    /// The advisor weighs the delay the junction saves against the detour of
    /// the vehicles that turned there: both in seconds per hour. A forbidden
    /// turn stays usable in the game, at a high cost in the route search, so
    /// a vehicle that has no other way still gets through.
    /// </summary>
    public static class TurnAdvisor
    {
        /// <summary>Extra travel time of a vehicle whose U-turn is forbidden, in seconds: to the next junction and back, or round a block.</summary>
        public const float UTurnDetour = 60f;

        /// <summary>Extra travel time of a vehicle whose left or right turn is forbidden, in seconds: round a block.</summary>
        public const float TurnDetour = 90f;

        /// <summary>
        /// A change of the forbidden turns must save at least this many
        /// seconds per road user, the margin <see cref="JunctionAdvisor"/>
        /// asks of a layout change. It keeps a junction from forbidding and
        /// allowing a turn back and forth on small shifts in traffic.
        /// </summary>
        public const float MinSaving = JunctionAdvisor.SwitchSeconds;

        /// <summary>Most turns weighed at one junction; each costs a set of plan estimates.</summary>
        public const int MaxCandidates = 8;

        /// <summary>Extra travel time of a vehicle whose movement is forbidden, or -1 for a movement that is never forbidden.</summary>
        public static float Detour(MovementKind kind)
        {
            switch (kind)
            {
                case MovementKind.UTurn:
                    return UTurnDetour;
                case MovementKind.Left:
                case MovementKind.Right:
                    return TurnDetour;
                default:
                    return -1f;
            }
        }

        /// <summary>
        /// The turns to forbid, as a mask over the movements of <paramref name="model"/>.
        /// </summary>
        /// <param name="volumes">
        /// Per movement, vehicles per hour. For a forbidden turn, what it
        /// carried before it was forbidden: the vehicles that would come back.
        /// </param>
        /// <param name="weights">Per movement, the traffic the phases are laid out by (PhasePlanner.Build); null for none.</param>
        /// <param name="candidates">The turns the advisor may forbid or allow.</param>
        /// <param name="current">The candidates forbidden now; they stay unless a change saves enough.</param>
        /// <param name="fixedOut">Movements forbidden whatever the advisor says, such as those the player forbade.</param>
        public static ulong Choose(JunctionModel model, float[] volumes, float[] weights, DelayParameters p,
            ulong candidates, ulong current, ulong fixedOut = 0UL)
        {
            candidates = Limit(model, candidates & ~fixedOut);
            current &= candidates;
            if (candidates == 0UL)
                return 0UL;

            // Start from every candidate forbidden and allow them back one by
            // one, the one that saves most first, while allowing does not
            // cost more. Starting from all forbidden finds turns that only
            // pay together, such as the U-turns of both ends of a main road
            // that each keep it from running with the side road.
            ulong forbidden = candidates;
            float cost = Cost(model, volumes, weights, p, forbidden, fixedOut, out _);
            while (forbidden != 0UL)
            {
                int bestTurn = -1;
                float bestCost = float.MaxValue;
                for (int m = 0; m < model.Movements.Count; m++)
                {
                    if ((forbidden & (1UL << m)) == 0)
                        continue;
                    float c = Cost(model, volumes, weights, p, forbidden & ~(1UL << m), fixedOut, out _);
                    if (c < bestCost)
                    {
                        bestCost = c;
                        bestTurn = m;
                    }
                }
                if (bestTurn < 0 || bestCost > cost)
                    break;
                forbidden &= ~(1UL << bestTurn);
                cost = bestCost;
            }

            if (forbidden == current)
                return current;
            float now = Cost(model, volumes, weights, p, current, fixedOut, out float people);
            return now - cost >= MinSaving * people ? forbidden : current;
        }

        /// <summary>
        /// Delay of the junction with <paramref name="forbidden"/> taken out,
        /// under its best layout, plus the detours of their vehicles: seconds
        /// per hour.
        /// </summary>
        /// <param name="people">Road users per hour, those on a detour included.</param>
        public static float Cost(JunctionModel model, float[] volumes, float[] weights, DelayParameters p,
            ulong forbidden, ulong fixedOut, out float people)
        {
            JunctionModel reduced = model.Without(forbidden | fixedOut, out int[] kept);
            PlanEstimate[] estimates = JunctionAdvisor.EvaluateAll(reduced, JunctionModel.Select(volumes, kept), p, JunctionModel.Select(weights, kept));
            PlanEstimate best = estimates[JunctionAdvisor.Best(estimates)];
            float detours = 0f;
            float detoured = 0f;
            for (int m = 0; m < model.Movements.Count; m++)
            {
                if ((forbidden & (1UL << m)) == 0 || m >= volumes.Length)
                    continue;
                detours += volumes[m] * Detour(model.Movements[m].Kind);
                detoured += volumes[m];
            }
            people = best.People + detoured;
            return best.TotalDelay + detours;
        }

        /// <summary>
        /// The candidates the advisor weighs: only turns, and at most
        /// <see cref="MaxCandidates"/> of them, U-turns first, since they
        /// carry least and block most.
        /// </summary>
        private static ulong Limit(JunctionModel model, ulong candidates)
        {
            ulong result = 0UL;
            int count = 0;
            foreach (MovementKind kind in new[] { MovementKind.UTurn, MovementKind.Left, MovementKind.Right })
            {
                for (int m = 0; m < model.Movements.Count && count < MaxCandidates; m++)
                {
                    if ((candidates & (1UL << m)) != 0 && model.Movements[m].Kind == kind)
                    {
                        result |= 1UL << m;
                        count++;
                    }
                }
            }
            return result;
        }
    }
}
