namespace TLL.Core.Planning
{
    public enum MovementKind : byte
    {
        Straight,
        Left,
        Right,
        UTurn,
        Pedestrian,
        Track,
    }

    /// <summary>
    /// A way through a junction that the signal plan switches as one unit:
    /// all lanes from one approach to one other approach, or one crosswalk.
    ///
    /// Approaches are numbered by the caller. The planner only compares the
    /// numbers, so any stable numbering works.
    /// </summary>
    public struct Movement
    {
        /// <summary>Approach the traffic comes from. For a crosswalk, the approach it crosses.</summary>
        public int Source;

        /// <summary>Approach the traffic leaves by. -1 for a crosswalk.</summary>
        public int Target;

        public MovementKind Kind;

        /// <summary>Number of lanes, used to weigh demand. At least 1.</summary>
        public int LaneCount;

        public Movement(int source, int target, MovementKind kind, int laneCount = 1)
        {
            Source = source;
            Target = target;
            Kind = kind;
            LaneCount = laneCount < 1 ? 1 : laneCount;
        }

        public bool IsPedestrian => Kind == MovementKind.Pedestrian;

        public bool IsTurn => Kind == MovementKind.Left || Kind == MovementKind.Right || Kind == MovementKind.UTurn;

        public override string ToString()
        {
            return IsPedestrian ? $"Ped@{Source}" : $"{Source}->{Target} {Kind}";
        }
    }
}
