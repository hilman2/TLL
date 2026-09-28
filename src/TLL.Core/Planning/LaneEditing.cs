namespace TLL.Core.Planning
{
    /// <summary>What became of a click on a lane's arrow. Numbers are shared with the panel.</summary>
    public enum LaneEdit : byte
    {
        Done = 0,

        /// <summary>It is the lane's only direction; a lane has to lead somewhere.</summary>
        LastDirection = 1,

        /// <summary>The lane would serve directions with one it does not serve between them.</summary>
        Gap = 2,

        /// <summary>Paths from two lanes would cross inside the junction.</summary>
        Cross = 3,

        /// <summary>The direction would lose its last lane.</summary>
        Uncovered = 4,

        /// <summary>More lanes would lead into the road than it has lanes to take them.</summary>
        TooMany = 5,
    }

    /// <summary>
    /// Changes the lane arrows of one approach by hand, one direction of one
    /// lane at a time, under the rules of <see cref="LaneArrows"/>: each lane
    /// serves a range of consecutive directions, from the kerb side outwards,
    /// the ranges move outwards from lane to lane so no two paths cross,
    /// every direction keeps a lane, and none gets more lanes than the road
    /// it leads into has.
    /// </summary>
    public static class LaneEditing
    {
        /// <summary>
        /// Gives lane <paramref name="lane"/> direction <paramref name="target"/>,
        /// or takes it away if it has it. Returns why not where the result
        /// would break a rule; <paramref name="result"/> is then the lanes as
        /// they were.
        /// </summary>
        /// <param name="uses">Per lane from the kerb outwards, the directions it serves, numbered from the kerb side outwards.</param>
        /// <param name="receiving">Per direction, the lanes of the road it leads into.</param>
        public static LaneEdit Toggle(LaneUse[] uses, int lane, int target, int[] receiving, out LaneUse[] result)
        {
            result = uses;
            LaneUse u = uses[lane];
            LaneUse changed;
            if (u.Serves(target))
            {
                if (u.First == u.Last)
                    return LaneEdit.LastDirection;
                if (target != u.First && target != u.Last)
                    return LaneEdit.Gap;
                changed = target == u.First ? new LaneUse(u.First + 1, u.Last) : new LaneUse(u.First, u.Last - 1);
            }
            else if (target == u.First - 1)
            {
                changed = new LaneUse(target, u.Last);
            }
            else if (target == u.Last + 1)
            {
                changed = new LaneUse(u.First, target);
            }
            else
            {
                return LaneEdit.Gap;
            }
            var next = (LaneUse[])uses.Clone();
            next[lane] = changed;
            LaneEdit valid = Check(next, receiving);
            if (valid == LaneEdit.Done)
                result = next;
            return valid;
        }

        /// <summary>Whether lanes keep the rules; the first rule they break, or Done.</summary>
        public static LaneEdit Check(LaneUse[] uses, int[] receiving)
        {
            for (int j = 1; j < uses.Length; j++)
            {
                if (uses[j].First < uses[j - 1].First || uses[j].Last < uses[j - 1].Last)
                    return LaneEdit.Cross;
            }
            for (int m = 0; m < receiving.Length; m++)
            {
                int count = 0;
                foreach (LaneUse use in uses)
                    count += use.Serves(m) ? 1 : 0;
                if (count == 0)
                    return LaneEdit.Uncovered;
                if (count > System.Math.Max(1, receiving[m]))
                    return LaneEdit.TooMany;
            }
            return LaneEdit.Done;
        }
    }
}
