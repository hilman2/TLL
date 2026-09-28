using System.Collections.Generic;
using System.Text;
using TLL.Components;
using TLL.Core.Advisor;
using TLL.Core.Planning;
using Unity.Entities;

namespace TLL.Systems
{
    /// <summary>
    /// The autopilot's review of a junction's turn rules (TurnAdvisor): which
    /// U-turns to forbid, from the day's peak traffic. The player's rules
    /// stay as they are.
    /// </summary>
    internal static class TurnReview
    {
        /// <summary>
        /// The rules the junction should have, or null to keep the ones it
        /// has. Rules for roads no longer at the junction are dropped.
        /// </summary>
        /// <param name="volumeOf">Per movement of the junction as the signals run it, the day's peak in vehicles per hour, keyed by source road, target road and kind.</param>
        /// <param name="summary">What changed and why, for the log.</param>
        public static List<TurnRule> Decide(EntityManager em, Entity node, bool leftHandTraffic, DelayParameters p,
            Dictionary<(Entity, Entity, MovementKind), float> volumeOf, out string summary)
        {
            summary = null;
            System.Type traffic = ModConflicts.TrafficConnections;
            if (traffic != null && em.HasComponent(node, ComponentType.ReadOnly(traffic)))
                return null;
            JunctionLayout full = JunctionAnalysis.Analyse(em, node, leftHandTraffic, includeForbidden: true);
            if (full == null || full.Edges.Count < 3)
                return null;
            var rules = new List<TurnRule>();
            if (em.HasBuffer<TurnRule>(node))
            {
                DynamicBuffer<TurnRule> stored = em.GetBuffer<TurnRule>(node, true);
                for (int i = 0; i < stored.Length; i++)
                    rules.Add(stored[i]);
            }
            int before = rules.Count;
            rules.RemoveAll(r => !full.Edges.Contains(r.From) || !full.Edges.Contains(r.To));
            bool pruned = rules.Count != before;

            JunctionModel model = full.Model;
            int n = model.Movements.Count;
            // Lanes a tram shares keep their flags (LaneRuleSystem), so their
            // turns cannot be forbidden.
            ulong tram = 0UL;
            foreach (LaneInfo lane in full.Lanes)
            {
                if (em.HasComponent<Game.Net.TrackLane>(lane.Lane))
                    tram |= 1UL << full.Keys.IndexOf(lane.Key);
            }
            var volumes = new float[n];
            ulong candidates = 0UL;
            ulong current = 0UL;
            ulong byPlayer = 0UL;
            for (int m = 0; m < n; m++)
            {
                Movement movement = model.Movements[m];
                if (movement.IsPedestrian)
                    continue;
                Entity from = full.Edges[movement.Source];
                Entity to = full.Edges[movement.Target];
                int rule = rules.FindIndex(r => r.From == from && r.To == to);
                bool forbidden = (full.Forbidden & (1UL << m)) != 0;
                bool ruled = rule >= 0 && rules[rule].Forbidden;
                if (forbidden || ruled)
                    volumes[m] = rule >= 0 ? rules[rule].Volume : 0f;
                else
                    volumes[m] = volumeOf.TryGetValue((from, to, movement.Kind), out float v) ? v : 0f;
                if (ruled && rules[rule].ByPlayer)
                    byPlayer |= 1UL << m;
                if (movement.Kind != MovementKind.UTurn || (rule >= 0 && rules[rule].ByPlayer) || (tram & (1UL << m)) != 0)
                    continue;
                // A turn the game's own road upgrades forbid, without a rule
                // of TLL, is not the autopilot's to allow.
                if (forbidden && rule < 0)
                    continue;
                candidates |= 1UL << m;
                // What the autopilot forbade, by its rules: the lanes follow
                // them only once the junction has been rebuilt.
                if (ruled)
                    current |= 1UL << m;
            }

            // Out whatever the autopilot says: what the game's upgrades and
            // the player forbid, also before the lanes follow the rule.
            ulong fixedOut = (full.Forbidden | byPlayer) & ~current;
            ulong chosen = TurnAdvisor.Choose(model, volumes, volumes, p, candidates, current, fixedOut);
            if (chosen == current && !pruned)
                return null;

            rules.RemoveAll(r => !r.ByPlayer);
            var text = new StringBuilder();
            for (int m = 0; m < n; m++)
            {
                bool now = (chosen & (1UL << m)) != 0;
                bool was = (current & (1UL << m)) != 0;
                if (!now && !was)
                    continue;
                Movement movement = model.Movements[m];
                if (now)
                {
                    rules.Add(new TurnRule
                    {
                        From = full.Edges[movement.Source],
                        To = full.Edges[movement.Target],
                        Flags = TurnRuleFlags.Forbidden,
                        Volume = volumes[m],
                    });
                }
                if (now != was)
                    text.Append(now ? " forbids " : " allows ").Append(movement).Append($" ({volumes[m]:0}/h),");
            }
            float costNow = TurnAdvisor.Cost(model, volumes, volumes, p, current, fixedOut, out float people);
            float costThen = TurnAdvisor.Cost(model, volumes, volumes, p, chosen, fixedOut, out _);
            summary = $"{text.ToString().TrimEnd(',')}{(pruned ? " (rules for removed roads dropped)" : "")};"
                + $" expected mean delay {costNow / System.Math.Max(1f, people):0.0} s -> {costThen / System.Math.Max(1f, people):0.0} s, detours included";
            return rules;
        }
    }
}
