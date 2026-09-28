using System.Collections.Generic;

namespace TLL.Core.Planning
{
    /// <summary>What happened to a click on a movement in the planner.</summary>
    public struct ToggleResult
    {
        /// <summary>The phase changed.</summary>
        public bool Changed;

        /// <summary>The movement now has green in the phase.</summary>
        public bool Added;

        /// <summary>
        /// Movements that went in or out together with the one clicked,
        /// because they start from the same approach lane (see
        /// <see cref="PlanEditing.Partners"/>). Does not include the one clicked.
        /// </summary>
        public ulong Partners;

        /// <summary>
        /// Refused: the movements of the phase whose paths cross the one
        /// clicked or one of its partners. Zero when the click went through.
        /// </summary>
        public ulong Blocking;
    }

    /// <summary>
    /// Changes a phase plan by hand, the way the planner in the panel does:
    /// one movement at a time, never against the geometry. A plan here is a
    /// list of phases, each the movements with green as a bit mask over
    /// <see cref="JunctionModel.Movements"/>. Who gives way inside a phase is
    /// not edited; it follows from the geometry (<see cref="PhasePlanner.PermittedIn"/>).
    /// </summary>
    public static class PlanEditing
    {
        /// <summary>
        /// The movements that start from an approach lane <paramref name="movement"/>
        /// uses too, directly or along a chain of such lanes. They can only
        /// have green together: the first vehicle of a shared lane holds up
        /// all behind it, whichever way they go.
        /// </summary>
        public static ulong Partners(JunctionModel model, int movement)
        {
            ulong group = 1UL << movement;
            ulong frontier = group;
            while (frontier != 0UL)
            {
                ulong next = 0UL;
                for (int m = 0; m < model.Movements.Count; m++)
                {
                    if ((frontier & (1UL << m)) != 0UL)
                        next |= model.SharesLaneWith(m);
                }
                frontier = next & ~group;
                group |= next;
            }
            return group & ~(1UL << movement);
        }

        /// <summary>
        /// The movements of <paramref name="phase"/> that may not have green
        /// together with any of <paramref name="adding"/>, by the geometry.
        /// </summary>
        public static ulong Blocking(JunctionModel model, ulong phase, ulong adding)
        {
            ulong blocking = 0UL;
            int n = model.Movements.Count;
            for (int a = 0; a < n; a++)
            {
                if ((adding & (1UL << a)) == 0UL)
                    continue;
                for (int b = 0; b < n; b++)
                {
                    if ((phase & (1UL << b)) != 0UL && (adding & (1UL << b)) == 0UL && !model.Conflicts.CanShare(a, b))
                        blocking |= 1UL << b;
                }
            }
            return blocking;
        }

        /// <summary>
        /// Gives <paramref name="movement"/> green in phase <paramref name="phase"/>,
        /// or takes it away if it has green there, together with its
        /// <see cref="Partners"/>. Refused, and nothing changes, where a
        /// movement of the phase crosses the path of one that would join.
        /// </summary>
        public static ToggleResult Toggle(JunctionModel model, IList<ulong> phases, int phase, int movement)
        {
            ulong partners = Partners(model, movement);
            ulong group = partners | (1UL << movement);
            ulong green = phases[phase];
            if ((green & (1UL << movement)) != 0UL)
            {
                phases[phase] = green & ~group;
                return new ToggleResult { Changed = true, Added = false, Partners = partners & green };
            }
            ulong blocking = Blocking(model, green, group);
            if (blocking != 0UL)
                return new ToggleResult { Blocking = blocking };
            phases[phase] = green | group;
            return new ToggleResult { Changed = true, Added = true, Partners = partners & ~green };
        }

        /// <summary>
        /// The phases in which <paramref name="movement"/> could get green
        /// with its partners, as a bit mask over the phases; those where it
        /// has green already are not included.
        /// </summary>
        public static uint FitsIn(JunctionModel model, IList<ulong> phases, int movement)
        {
            ulong group = Partners(model, movement) | (1UL << movement);
            uint result = 0U;
            for (int p = 0; p < phases.Count && p < 32; p++)
            {
                if ((phases[p] & (1UL << movement)) == 0UL && Blocking(model, phases[p], group) == 0UL)
                    result |= 1U << p;
            }
            return result;
        }

        /// <summary>
        /// The phase with every movement added that fits, each with its
        /// partners: what the button "Fill up" does. Movements are tried
        /// in the order of the model, vehicles before crosswalks, so the
        /// result does not depend on how the phase came about.
        /// </summary>
        public static ulong FillUp(JunctionModel model, ulong phase)
        {
            int n = model.Movements.Count;
            for (int pass = 0; pass < 2; pass++)
            {
                bool crosswalks = pass == 1;
                for (int m = 0; m < n; m++)
                {
                    if ((phase & (1UL << m)) != 0UL || model.Movements[m].IsPedestrian != crosswalks)
                        continue;
                    ulong group = Partners(model, m) | (1UL << m);
                    if (Blocking(model, phase, group) == 0UL)
                        phase |= group;
                }
            }
            return phase;
        }

        /// <summary>
        /// Gives a movement no phase serves a place in the plan: where its
        /// lane partners have green, else in every phase where traffic from
        /// its approach has green and it fits, else in the first phase it
        /// fits, else in a new phase at the end.
        /// </summary>
        /// <returns>The phases it went into, as a bit mask; 0 if the plan is full.</returns>
        public static uint Place(JunctionModel model, IList<ulong> phases, int movement)
        {
            ulong partners = Partners(model, movement);
            ulong group = partners | (1UL << movement);
            uint fits = FitsIn(model, phases, movement);

            uint withPartners = 0U;
            for (int p = 0; p < phases.Count && p < 32; p++)
            {
                if ((phases[p] & partners) != 0UL)
                    withPartners |= 1U << p;
            }
            uint chosen = withPartners != 0U && (withPartners & ~fits) == 0U ? withPartners : 0U;

            if (chosen == 0U && !model.Movements[movement].IsPedestrian)
            {
                int source = model.Movements[movement].Source;
                for (int p = 0; p < phases.Count && p < 32; p++)
                {
                    if ((fits & (1U << p)) != 0U && FromApproach(model, phases[p], source))
                        chosen |= 1U << p;
                }
            }
            if (chosen == 0U && fits != 0U)
                chosen = fits & (~fits + 1U);
            if (chosen == 0U)
            {
                if (phases.Count >= PhasePlanner.MaxPhases)
                    return 0U;
                phases.Add(0UL);
                chosen = 1U << (phases.Count - 1);
            }
            for (int p = 0; p < phases.Count && p < 32; p++)
            {
                if ((chosen & (1U << p)) != 0U)
                    phases[p] |= group;
            }
            return chosen;
        }

        /// <summary>Whether a vehicle movement from <paramref name="approach"/> has green in the phase.</summary>
        private static bool FromApproach(JunctionModel model, ulong phase, int approach)
        {
            for (int m = 0; m < model.Movements.Count; m++)
            {
                if ((phase & (1UL << m)) != 0UL && !model.Movements[m].IsPedestrian && model.Movements[m].Source == approach)
                    return true;
            }
            return false;
        }

        /// <summary>The number of set bits.</summary>
        public static int Count(ulong bits)
        {
            int count = 0;
            while (bits != 0UL)
            {
                bits &= bits - 1UL;
                count++;
            }
            return count;
        }
    }
}
