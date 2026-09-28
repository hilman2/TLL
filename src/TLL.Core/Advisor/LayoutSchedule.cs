using TLL.Core.Planning;

namespace TLL.Core.Advisor
{
    /// <summary>
    /// When the layout the autopilot recommends at its reviews is actually
    /// applied. Every change rebuilds the junction and starts its controller
    /// over, so a change must hold: two reviews in a row have to agree, one
    /// is enough while the running layout jams, and a new layout runs at
    /// least <see cref="DwellReviews"/> reviews before the next change, jam
    /// or not. Without that, junctions that jammed in every layout switched
    /// at every review.
    /// </summary>
    public struct LayoutSchedule
    {
        /// <summary>Reviews in a row that must recommend the same change.</summary>
        public const int ConfirmReviews = 2;

        /// <summary>
        /// Reviews a layout runs at least: long enough to be measured twice
        /// (LayoutMemory), the first period being cut short by the change.
        /// </summary>
        public const int DwellReviews = 3;

        public PlanStrategy Pending;

        /// <summary>The layout recommended is to have a scramble.</summary>
        public bool PendingScramble;

        public byte PendingReviews;

        /// <summary>Reviews since the last change.</summary>
        public byte Age;

        /// <summary>A schedule that may change at the first review, for a junction new to the autopilot.</summary>
        public static LayoutSchedule Start => new LayoutSchedule { Age = byte.MaxValue };

        /// <summary>Called at every review. Returns whether to change to <paramref name="choice"/> now.</summary>
        /// <param name="current">The layout running.</param>
        /// <param name="choice">The layout the review recommends.</param>
        /// <param name="jammed">The running layout's queues did not clear.</param>
        public bool Review(PlanStrategy current, PlanStrategy choice, bool jammed)
        {
            return Review(current, false, choice, false, jammed);
        }

        /// <summary>Called at every review. Returns whether to change to <paramref name="choice"/> with <paramref name="choiceScramble"/> now.</summary>
        /// <param name="currentScramble">The running layout has a scramble.</param>
        /// <param name="choiceScramble">The layout recommended is to have one.</param>
        public bool Review(PlanStrategy current, bool currentScramble, PlanStrategy choice, bool choiceScramble, bool jammed)
        {
            if (Age < byte.MaxValue)
                Age++;
            if (choice == current && choiceScramble == currentScramble)
            {
                PendingReviews = 0;
                return false;
            }
            if (Pending == choice && PendingScramble == choiceScramble && PendingReviews > 0)
            {
                if (PendingReviews < byte.MaxValue)
                    PendingReviews++;
            }
            else
            {
                Pending = choice;
                PendingScramble = choiceScramble;
                PendingReviews = 1;
            }
            if (Age < DwellReviews || PendingReviews < (jammed ? 1 : ConfirmReviews))
                return false;
            PendingReviews = 0;
            Age = 0;
            return true;
        }
    }
}
