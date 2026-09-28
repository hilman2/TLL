using System;
using System.Collections.Generic;
using Colossal.UI.Binding;
using TLL.Components;
using TLL.Core.Planning;
using TLL.Systems;
using Unity.Entities;

namespace TLL.UI.Planner
{
    // The planner's lane arrows: which lane of a road leading in goes where.
    // The player edits them in the draft like the phases; applied, they
    // become the player's lane rules (LaneConnectionRule).
    public partial class PlannerSystem
    {
        private List<LaneEnd> m_Ends;
        private HashSet<ApproachLanes.Link> m_Links;
        private List<ApproachLanes> m_Approaches = new List<ApproachLanes>();

        /// <summary>The junction as the draft's lanes make it, and the draft lanes it was made for.</summary>
        private JunctionModel m_DraftModel;
        private string m_DraftModelKey;

        /// <summary>
        /// The junction the draft plans for: the layout, with the lanes of
        /// the approaches whose arrows the draft changes. A left turn given a
        /// lane of its own no longer shares one with straight traffic, so the
        /// phases may part them.
        /// </summary>
        private JunctionModel Model
        {
            get
            {
                if (m_Draft == null || m_Draft.Lanes.Count == 0)
                    return m_Layout.Model;
                string key = m_Draft.LanesKey();
                if (key == m_DraftModelKey && m_DraftModel != null)
                    return m_DraftModel;
                JunctionModel model = m_Layout.Model;
                foreach (KeyValuePair<int, LaneUse[]> lanes in m_Draft.Lanes)
                {
                    ApproachLanes approach = m_Approaches.Find(a => a.Approach == lanes.Key);
                    int[] movements = approach != null ? MovementsOf(model, approach) : null;
                    if (movements != null)
                        model = model.WithLanes(movements, lanes.Value);
                }
                m_DraftModel = model;
                m_DraftModelKey = key;
                return model;
            }
        }

        /// <summary>The model's movements for an approach's targets, in their order; null if one has none (a forbidden turn).</summary>
        private static int[] MovementsOf(JunctionModel model, ApproachLanes approach)
        {
            var movements = new int[approach.Targets.Count];
            for (int m = 0; m < movements.Length; m++)
            {
                movements[m] = -1;
                for (int i = 0; i < model.Movements.Count; i++)
                {
                    Movement mv = model.Movements[i];
                    if (mv.Source == approach.Approach && mv.Target == approach.Targets[m] && !mv.IsPedestrian && mv.Kind != MovementKind.Track)
                        movements[m] = i;
                }
                if (movements[m] < 0)
                    return null;
            }
            return movements;
        }

        /// <summary>Reads the lanes of the junction's roads leading in, as they run now.</summary>
        private void LoadLanes()
        {
            m_Ends = LaneEnds.Collect(EntityManager, m_Node, m_Layout.Edges);
            m_Links = ApproachLanes.Current(EntityManager, m_Node, m_Ends);
            m_Approaches = ApproachLanes.Read(EntityManager, m_Node, m_Layout.Edges, m_Ends, m_Links, m_Layout.Model.LeftHandTraffic);
            m_DraftModel = null;
            m_DraftModelKey = null;
        }

        /// <summary>An approach's lanes in the draft: the draft's change, or as they run now.</summary>
        private LaneUse[] UsesOf(ApproachLanes approach)
        {
            return m_Draft.Lanes.TryGetValue(approach.Approach, out LaneUse[] uses) ? uses : approach.Uses;
        }

        /// <summary>
        /// Sets an approach's lanes in the draft. Lanes as they run now are
        /// no change, so the draft forgets them, and a draft that only went
        /// there and back has no changes.
        /// </summary>
        private void SetLanes(ApproachLanes approach, LaneUse[] uses)
        {
            if (Same(uses, approach.Uses))
                m_Draft.Lanes.Remove(approach.Approach);
            else
                m_Draft.Lanes[approach.Approach] = uses;
        }

        private bool ToggleLane(int approachIndex, int lane, int target)
        {
            ApproachLanes approach = m_Approaches.Find(a => a.Approach == approachIndex);
            if (approach == null || lane < 0 || lane >= approach.Lanes.Count || target < 0 || target >= approach.Targets.Count)
                return false;
            LaneEdit result = LaneEditing.Toggle(UsesOf(approach), lane, target, approach.Receiving, out LaneUse[] uses);
            if (result != LaneEdit.Done)
            {
                Say("Lane" + result, null);
                return false;
            }
            SetLanes(approach, uses);
            return true;
        }

        /// <summary>Takes the lanes the autopilot's lane review would choose for the measured traffic.</summary>
        private bool UseLaneSuggestion(int approachIndex)
        {
            ApproachLanes approach = m_Approaches.Find(a => a.Approach == approachIndex);
            LaneUse[] suggestion = approach != null ? Suggestion(approach, out _) : null;
            if (suggestion == null)
                return false;
            SetLanes(approach, suggestion);
            return true;
        }

        /// <summary>
        /// Gives an approach back the lanes the game builds by itself: the
        /// draft drops every rule of the player's and the autopilot's for it
        /// once applied.
        /// </summary>
        private bool ResetLanes(int approachIndex)
        {
            ApproachLanes approach = m_Approaches.Find(a => a.Approach == approachIndex);
            if (approach == null)
                return false;
            LaneUse[] game = approach.UsesFrom(ApproachLanes.GameLinks(m_Ends, m_Links, RulesOf(approach)));
            if (game == null)
                return false;
            // The game's own lanes may be what runs now even though rules
            // exist (rules that change nothing); marked as a change anyway,
            // so Apply clears the rules.
            m_Draft.Lanes[approach.Approach] = game;
            return true;
        }

        /// <summary>The lane rules of one approach, the player's and the autopilot's.</summary>
        private List<LaneConnectionRule> RulesOf(ApproachLanes approach)
        {
            var own = new List<LaneConnectionRule>();
            if (!EntityManager.HasBuffer<LaneConnectionRule>(m_Node))
                return own;
            Entity edge = m_Layout.Edges[approach.Approach];
            DynamicBuffer<LaneConnectionRule> rules = EntityManager.GetBuffer<LaneConnectionRule>(m_Node, true);
            for (int i = 0; i < rules.Length; i++)
            {
                if (rules[i].FromEdge == edge)
                    own.Add(rules[i]);
            }
            return own;
        }

        /// <summary>
        /// The lanes the autopilot would give an approach for its measured
        /// traffic (LaneArrows.Choose), with the share by which that lowers
        /// the busiest lane's load; null where they are the draft's already,
        /// or nothing is measured.
        /// </summary>
        private LaneUse[] Suggestion(ApproachLanes approach, out float gain)
        {
            gain = 0f;
            var volumes = new float[approach.Targets.Count];
            bool any = false;
            JunctionModel model = m_Layout.Model;
            for (int m = 0; m < volumes.Length; m++)
            {
                for (int i = 0; i < model.Movements.Count; i++)
                {
                    Movement mv = model.Movements[i];
                    if (mv.Source == approach.Approach && mv.Target == approach.Targets[m] && !mv.IsPedestrian && mv.Kind != MovementKind.Track && m_Peak != null)
                        volumes[m] = Math.Max(0f, m_Peak[i]);
                }
                any |= volumes[m] > 0f;
            }
            if (!any)
                return null;
            LaneUse[] now = UsesOf(approach);
            LaneUse[] best = LaneArrows.Choose(now, volumes, approach.Far, approach.Straight, approach.Receiving, true, out float before, out float after);
            if (ReferenceEquals(best, now) || Same(best, now))
                return null;
            gain = before > 0f && before < float.MaxValue ? 1f - after / before : 0f;
            return best;
        }

        /// <summary>
        /// Writes the draft's lane arrows as the player's rules of their
        /// approaches, replacing the rules each had. Returns whether any
        /// changed; the junction must then be rebuilt to build its lanes.
        /// </summary>
        private bool ApplyLanes()
        {
            if (m_Draft.Lanes.Count == 0)
                return false;
            DynamicBuffer<LaneConnectionRule> buffer = EntityManager.HasBuffer<LaneConnectionRule>(m_Node)
                ? EntityManager.GetBuffer<LaneConnectionRule>(m_Node)
                : EntityManager.AddBuffer<LaneConnectionRule>(m_Node);
            var kept = new List<LaneConnectionRule>();
            var edges = new HashSet<Entity>();
            foreach (int approach in m_Draft.Lanes.Keys)
                edges.Add(m_Layout.Edges[approach]);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (!edges.Contains(buffer[i].FromEdge))
                    kept.Add(buffer[i]);
            }
            foreach (KeyValuePair<int, LaneUse[]> lanes in m_Draft.Lanes)
            {
                ApproachLanes approach = m_Approaches.Find(a => a.Approach == lanes.Key);
                if (approach == null)
                    continue;
                kept.AddRange(approach.Rules(m_Ends, m_Links, lanes.Value, RulesOf(approach)));
            }
            buffer = EntityManager.GetBuffer<LaneConnectionRule>(m_Node);
            buffer.Clear();
            foreach (LaneConnectionRule rule in kept)
                buffer.Add(rule);
            return true;
        }

        private void WriteLanes(IJsonWriter writer)
        {
            writer.PropertyName("lanes");
            writer.ArrayBegin((uint)m_Approaches.Count);
            foreach (ApproachLanes approach in m_Approaches)
            {
                LaneUse[] uses = UsesOf(approach);
                writer.TypeBegin("tll.ApproachLanes");
                writer.PropertyName("approach");
                writer.Write(approach.Approach);
                writer.PropertyName("targets");
                writer.ArrayBegin((uint)approach.Targets.Count);
                foreach (int t in approach.Targets)
                    writer.Write(t);
                writer.ArrayEnd();
                writer.PropertyName("uses");
                WriteUses(writer, uses);
                writer.PropertyName("changed");
                writer.Write(m_Draft.Lanes.ContainsKey(approach.Approach));
                writer.PropertyName("ruled");
                writer.Write(RulesOf(approach).Count > 0);
                // For each lane and target, what a click would do: 0 where it
                // goes through, else why not (LaneEdit).
                writer.PropertyName("options");
                writer.ArrayBegin((uint)approach.Lanes.Count);
                for (int j = 0; j < approach.Lanes.Count; j++)
                {
                    writer.ArrayBegin((uint)approach.Targets.Count);
                    for (int m = 0; m < approach.Targets.Count; m++)
                        writer.Write((int)LaneEditing.Toggle(uses, j, m, approach.Receiving, out _));
                    writer.ArrayEnd();
                }
                writer.ArrayEnd();
                LaneUse[] suggestion = Suggestion(approach, out float gain);
                writer.PropertyName("suggestion");
                if (suggestion != null)
                    WriteUses(writer, suggestion);
                else
                    writer.WriteNull();
                writer.PropertyName("gain");
                writer.Write(gain);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }

        private static void WriteUses(IJsonWriter writer, LaneUse[] uses)
        {
            writer.ArrayBegin((uint)uses.Length);
            foreach (LaneUse use in uses)
            {
                writer.TypeBegin("tll.LaneUse");
                writer.PropertyName("first");
                writer.Write(use.First);
                writer.PropertyName("last");
                writer.Write(use.Last);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }

        private static bool Same(LaneUse[] a, LaneUse[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }
            return true;
        }
    }
}
