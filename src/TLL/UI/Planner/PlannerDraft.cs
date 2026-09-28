using System.Collections.Generic;
using TLL.Core;
using TLL.Core.Control;
using TLL.Core.Planning;
using Unity.Entities;

namespace TLL.UI.Planner
{
    /// <summary>Who decides a junction's layout and times. Numbers are shared with the panel.</summary>
    public enum PlannerOwner : byte
    {
        /// <summary>The autopilot plans the junction, as an automatic junction.</summary>
        Autopilot = 0,

        /// <summary>The player's phases and lanes; the optimiser fits the times (JunctionOptions.AutoTiming).</summary>
        Layout = 1,

        /// <summary>Nothing changes without the player.</summary>
        Everything = 2,
    }

    /// <summary>The planned times of one phase, in controller steps, as PhaseData keeps them.</summary>
    public struct PhaseTiming
    {
        public ushort MinGreen;
        public ushort MaxGreen;
        public ushort Green;

        /// <summary>
        /// The times a new phase starts with, the same the set-up gives a
        /// generated one: longer for a phase with straight traffic, which
        /// usually carries the most.
        /// </summary>
        public static PhaseTiming Default(JunctionModel model, ulong phase)
        {
            bool straight = false;
            for (int m = 0; m < model.Movements.Count; m++)
            {
                MovementKind kind = model.Movements[m].Kind;
                straight |= (phase & (1UL << m)) != 0UL && (kind == MovementKind.Straight || kind == MovementKind.Track);
            }
            return new PhaseTiming
            {
                MinGreen = (ushort)SimTime.ToSteps(5f),
                MaxGreen = (ushort)SimTime.ToSteps(straight ? 45f : 25f),
                Green = (ushort)SimTime.ToSteps(straight ? 20f : 10f),
            };
        }
    }

    /// <summary>
    /// A junction's plan as the player edits it in the planner, before it is
    /// applied: its phases with their times, and the junction's settings.
    /// Phases are masks over the movements of the layout the planner opened
    /// with (JunctionAnalysis), which is the order the junction stores them in.
    /// </summary>
    public sealed class PlannerDraft
    {
        public readonly List<ulong> Phases = new List<ulong>();
        public readonly List<PhaseTiming> Timing = new List<PhaseTiming>();

        public PlannerOwner Owner;
        public ControlMode Mode;
        public bool TurnOnRed;

        /// <summary>Pedestrians walk only in phases of crosswalks alone (JunctionOptions.Scramble).</summary>
        public bool Scramble;

        public byte Yellow;
        public byte AllRed;
        public byte Prepare;
        public ushort MaxWait;

        /// <summary>The road the player named as the main road, or Null to let TLL choose.</summary>
        public Entity MajorApproach;

        /// <summary>Per approach, the road pieces of solid lines before the junction (SolidLineRule); 0 for none.</summary>
        public int[] Solid = new int[0];

        /// <summary>
        /// Lane arrows the player changed, per approach: which targets each
        /// lane serves, from the kerb outwards (ApproachLanes). An approach
        /// not listed keeps the lanes it has.
        /// </summary>
        public readonly SortedDictionary<int, LaneUse[]> Lanes = new SortedDictionary<int, LaneUse[]>();

        public PlannerDraft Clone()
        {
            var result = new PlannerDraft
            {
                Owner = Owner, Mode = Mode, TurnOnRed = TurnOnRed, Scramble = Scramble,
                Yellow = Yellow, AllRed = AllRed, Prepare = Prepare, MaxWait = MaxWait, MajorApproach = MajorApproach,
                Solid = (int[])Solid.Clone(),
            };
            result.Phases.AddRange(Phases);
            result.Timing.AddRange(Timing);
            foreach (KeyValuePair<int, LaneUse[]> lanes in Lanes)
                result.Lanes[lanes.Key] = (LaneUse[])lanes.Value.Clone();
            return result;
        }

        /// <summary>The draft's lanes as text, to tell two drafts' lanes apart and to cache what depends on them.</summary>
        public string LanesKey()
        {
            var key = new System.Text.StringBuilder();
            foreach (KeyValuePair<int, LaneUse[]> lanes in Lanes)
                key.Append(lanes.Key).Append(':').Append(string.Join(" ", lanes.Value)).Append(';');
            return key.ToString();
        }

        public bool SameAs(PlannerDraft other)
        {
            if (other == null || Owner != other.Owner || Mode != other.Mode || TurnOnRed != other.TurnOnRed || Scramble != other.Scramble
                || Yellow != other.Yellow || AllRed != other.AllRed || Prepare != other.Prepare || MaxWait != other.MaxWait
                || MajorApproach != other.MajorApproach || Phases.Count != other.Phases.Count || !System.Linq.Enumerable.SequenceEqual(Solid, other.Solid)
                || LanesKey() != other.LanesKey())
                return false;
            for (int p = 0; p < Phases.Count; p++)
            {
                if (Phases[p] != other.Phases[p] || !Timing[p].Equals(other.Timing[p]))
                    return false;
            }
            return true;
        }

        /// <summary>Inserts a phase with its times at <paramref name="index"/>.</summary>
        public void Insert(int index, ulong phase, PhaseTiming timing)
        {
            Phases.Insert(index, phase);
            Timing.Insert(index, timing);
        }

        public void RemoveAt(int index)
        {
            Phases.RemoveAt(index);
            Timing.RemoveAt(index);
        }

        /// <summary>
        /// Brings the times in line with the phases after PlanEditing added
        /// phases of its own (Place), which know nothing of times.
        /// </summary>
        public void FillTiming(JunctionModel model)
        {
            while (Timing.Count < Phases.Count)
                Timing.Add(PhaseTiming.Default(model, Phases[Timing.Count]));
            while (Timing.Count > Phases.Count)
                Timing.RemoveAt(Timing.Count - 1);
        }
    }
}
