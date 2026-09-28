using System;
using System.Collections.Generic;
using Game.Net;
using TLL.Components;
using TLL.Core.Planning;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Systems
{
    /// <summary>
    /// The lanes of one road leading into a junction as the lane arrows see
    /// them (LaneArrows): the lanes from the kerb outwards, the roads they
    /// lead into in the same order, and which lane serves which. For the
    /// planner, which edits them, and the rules that make the junction's
    /// lanes follow an edit.
    /// </summary>
    internal sealed class ApproachLanes
    {
        /// <summary>The road, as an index into the junction's roads.</summary>
        public int Approach;

        /// <summary>The lanes leading in, as indices into the lane ends, from the kerb outwards.</summary>
        public List<int> Lanes;

        /// <summary>The roads they lead into, U-turns aside, from the kerb side outwards.</summary>
        public List<int> Targets;

        /// <summary>For each lane, the targets it serves now.</summary>
        public LaneUse[] Uses;

        /// <summary>Per target: whether the turn goes across the oncoming traffic, whether it goes straight on, and the lanes of the road it leads into.</summary>
        public bool[] Far;
        public bool[] Straight;
        public int[] Receiving;

        /// <summary>Per target, the lane ends leading out of the junction into it, from the kerb outwards.</summary>
        public List<int>[] Exits;

        /// <summary>A pair of lane ends connected through the junction: in, out.</summary>
        public struct Link : IEquatable<Link>
        {
            public int From;
            public int To;

            public bool Equals(Link other) => From == other.From && To == other.To;

            public override int GetHashCode() => From * 397 ^ To;
        }

        /// <summary>
        /// Every road leading into the junction whose lanes the arrows can
        /// describe: two or more lanes, each leading somewhere other than
        /// back. Roads with one lane have no choice to make.
        /// </summary>
        public static List<ApproachLanes> Read(EntityManager em, Entity node, List<Entity> edges, List<LaneEnd> ends, HashSet<Link> current, bool leftHandTraffic)
        {
            var result = new List<ApproachLanes>();
            for (int a = 0; a < edges.Count; a++)
            {
                ApproachLanes lanes = ReadOne(edges, a, ends, current, leftHandTraffic);
                if (lanes != null)
                    result.Add(lanes);
            }
            return result;
        }

        private static ApproachLanes ReadOne(List<Entity> edges, int approach, List<LaneEnd> ends, HashSet<Link> current, bool leftHandTraffic)
        {
            Entity edge = edges[approach];
            List<int> lanes = Sorted(ends, i => ends[i].Incoming && ends[i].Edge == edge, leftHandTraffic);
            if (lanes.Count < 2 || lanes.Count > LaneArrows.MaxLanes)
                return null;
            float3 heading = Mean(ends, lanes);
            var targets = new List<int>();
            foreach (Link link in current)
            {
                int target = edges.IndexOf(ends[link.To].Edge);
                if (lanes.Contains(link.From) && target != approach && target >= 0 && !targets.Contains(target))
                    targets.Add(target);
            }
            if (targets.Count < 1 || targets.Count > LaneArrows.MaxMovements)
                return null;
            var angles = new Dictionary<int, float>();
            var exits = new Dictionary<int, List<int>>();
            foreach (int t in targets)
            {
                List<int> outgoing = Sorted(ends, i => !ends[i].Incoming && ends[i].Edge == edges[t], leftHandTraffic);
                exits[t] = outgoing;
                angles[t] = Angle(heading, Mean(ends, outgoing));
            }
            // A kerb-side turn has the lowest angle in right-hand traffic
            // (to the right), the highest in left-hand traffic.
            targets.Sort((x, y) => leftHandTraffic ? angles[y].CompareTo(angles[x]) : angles[x].CompareTo(angles[y]));

            int n = targets.Count;
            var result = new ApproachLanes
            {
                Approach = approach,
                Lanes = lanes,
                Targets = targets,
                Uses = new LaneUse[lanes.Count],
                Far = new bool[n],
                Straight = new bool[n],
                Receiving = new int[n],
                Exits = new List<int>[n],
            };
            for (int m = 0; m < n; m++)
            {
                result.Straight[m] = math.abs(angles[targets[m]]) < 30f;
                result.Far[m] = !result.Straight[m] && (leftHandTraffic ? angles[targets[m]] < 0f : angles[targets[m]] > 0f);
                result.Exits[m] = exits[targets[m]];
                result.Receiving[m] = exits[targets[m]].Count;
            }
            for (int j = 0; j < lanes.Count; j++)
            {
                int first = int.MaxValue;
                int last = -1;
                for (int m = 0; m < n; m++)
                {
                    if (!Serves(current, lanes[j], result.Exits[m]))
                        continue;
                    first = Math.Min(first, m);
                    last = Math.Max(last, m);
                }
                // A lane that only turns back, or leads nowhere: not a road
                // the arrows describe.
                if (last < 0)
                    return null;
                result.Uses[j] = new LaneUse(first, last);
            }
            return result;
        }

        /// <summary>
        /// The rules that give this road's lanes <paramref name="chosen"/>:
        /// the difference between the connections the game builds by itself
        /// and those the lanes are to have. A lane keeps its connections for
        /// a target it still serves; one that gains a target is connected to
        /// the lane of that road on the same side. <paramref name="own"/> are
        /// the rules the road has now, which the result replaces; without
        /// them the junction's lanes are the game's own.
        /// </summary>
        public List<LaneConnectionRule> Rules(List<LaneEnd> ends, HashSet<Link> current, LaneUse[] chosen, List<LaneConnectionRule> own)
        {
            int n = Targets.Count;
            var wanted = new HashSet<Link>();
            foreach (Link link in current)
            {
                if (!Lanes.Contains(link.From))
                    continue;
                int m = TargetOf(link.To);
                if (m < 0 || chosen[Lanes.IndexOf(link.From)].Serves(m))
                    wanted.Add(link);
            }
            for (int m = 0; m < n; m++)
            {
                var serving = new List<int>();
                for (int j = 0; j < Lanes.Count; j++)
                {
                    if (chosen[j].Serves(m))
                        serving.Add(j);
                }
                for (int k = 0; k < serving.Count; k++)
                {
                    int lane = Lanes[serving[k]];
                    if (Serves(wanted, lane, Exits[m]))
                        continue;
                    int exit = FreeExit(wanted, Exits[m], ExitFor(k, serving.Count, Exits[m].Count, Far[m]));
                    wanted.Add(new Link { From = lane, To = Exits[m][exit] });
                }
            }

            HashSet<Link> game = GameLinks(ends, current, own);
            var result = new List<LaneConnectionRule>();
            foreach (Link link in game)
            {
                if (Lanes.Contains(link.From) && !wanted.Contains(link) && TargetOf(link.To) >= 0)
                    result.Add(Rule(ends, link, LaneConnectionChange.Removed));
            }
            foreach (Link link in wanted)
            {
                if (!game.Contains(link))
                    result.Add(Rule(ends, link, LaneConnectionChange.Added));
            }
            return result;
        }

        /// <summary>The game's own connections: what the junction has now, without what the road's rules <paramref name="own"/> did to it.</summary>
        public static HashSet<Link> GameLinks(List<LaneEnd> ends, HashSet<Link> current, List<LaneConnectionRule> own)
        {
            var game = new HashSet<Link>(current);
            foreach (LaneConnectionRule rule in own)
            {
                int from = LaneEnds.Find(ends, rule.FromEdge, rule.FromLane, incoming: true);
                int to = LaneEnds.Find(ends, rule.ToEdge, rule.ToLane, incoming: false);
                if (from < 0 || to < 0)
                    continue;
                var link = new Link { From = from, To = to };
                if (rule.Change == LaneConnectionChange.Added)
                    game.Remove(link);
                else
                    game.Add(link);
            }
            return game;
        }

        /// <summary>For each lane, the targets it serves with the connections <paramref name="links"/>; null if a lane would serve none.</summary>
        public LaneUse[] UsesFrom(HashSet<Link> links)
        {
            var uses = new LaneUse[Lanes.Count];
            for (int j = 0; j < Lanes.Count; j++)
            {
                int first = int.MaxValue;
                int last = -1;
                for (int m = 0; m < Targets.Count; m++)
                {
                    if (!Serves(links, Lanes[j], Exits[m]))
                        continue;
                    first = Math.Min(first, m);
                    last = Math.Max(last, m);
                }
                if (last < 0)
                    return null;
                uses[j] = new LaneUse(first, last);
            }
            return uses;
        }

        /// <summary>The target a lane end leading out belongs to, or -1 (a U-turn, or a road the lanes do not lead into).</summary>
        private int TargetOf(int exit)
        {
            for (int m = 0; m < Exits.Length; m++)
            {
                if (Exits[m].Contains(exit))
                    return m;
            }
            return -1;
        }

        /// <summary>The connections the junction's lanes make now, as pairs of lane ends; lanes of forbidden turns left out.</summary>
        public static HashSet<Link> Current(EntityManager em, Entity node, List<LaneEnd> ends)
        {
            var links = new HashSet<Link>();
            DynamicBuffer<SubLane> lanes = em.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!em.HasComponent<CarLane>(lane) || em.HasComponent<MasterLane>(lane)
                    || (em.GetComponentData<CarLane>(lane).m_Flags & CarLaneFlags.Forbidden) != 0)
                    continue;
                Lane path = em.GetComponentData<Lane>(lane);
                int from = LaneEnds.Find(ends, path.m_StartNode, incoming: true);
                int to = LaneEnds.Find(ends, path.m_EndNode, incoming: false);
                if (from >= 0 && to >= 0)
                    links.Add(new Link { From = from, To = to });
            }
            return links;
        }

        private static LaneConnectionRule Rule(List<LaneEnd> ends, Link link, LaneConnectionChange change)
        {
            return new LaneConnectionRule
            {
                FromEdge = ends[link.From].Edge,
                FromLane = ends[link.From].Index,
                ToEdge = ends[link.To].Edge,
                ToLane = ends[link.To].Index,
                Change = change,
            };
        }

        /// <summary>
        /// Which lane of the road it leads into the k-th of <paramref name="count"/>
        /// lanes of a movement takes, both counted from the kerb. A turn
        /// across the oncoming traffic keeps to the far side, the others to
        /// the kerb side, so the paths of one movement stay side by side.
        /// </summary>
        private static int ExitFor(int k, int count, int exits, bool far)
        {
            int index = far ? exits - count + k : k;
            return math.clamp(index, 0, exits - 1);
        }

        /// <summary>
        /// The exit lane nearest to <paramref name="preferred"/> that no
        /// wanted connection leads into yet, or the preferred one if all are
        /// taken: two lanes into one would have to merge inside the junction.
        /// </summary>
        private static int FreeExit(HashSet<Link> wanted, List<int> exits, int preferred)
        {
            for (int distance = 0; distance < exits.Count; distance++)
            {
                foreach (int index in new[] { preferred - distance, preferred + distance })
                {
                    if (index < 0 || index >= exits.Count)
                        continue;
                    bool taken = false;
                    foreach (Link link in wanted)
                        taken |= link.To == exits[index];
                    if (!taken)
                        return index;
                }
            }
            return preferred;
        }

        private static bool Serves(HashSet<Link> links, int lane, List<int> exits)
        {
            foreach (int exit in exits)
            {
                if (links.Contains(new Link { From = lane, To = exit }))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The lane ends that match, from the kerb side outwards: ordered
        /// across their direction of travel, the kerb on the right in
        /// right-hand traffic.
        /// </summary>
        private static List<int> Sorted(List<LaneEnd> ends, Predicate<int> match, bool leftHandTraffic)
        {
            var result = new List<int>();
            for (int i = 0; i < ends.Count; i++)
            {
                if (match(i))
                    result.Add(i);
            }
            if (result.Count == 0)
                return result;
            float3 heading = Mean(ends, result);
            // To the right of the heading, seen from above (x east, z north).
            float2 right = new float2(heading.z, -heading.x);
            if (leftHandTraffic)
                right = -right;
            result.Sort((a, b) => math.dot(ends[b].Position.xz, right).CompareTo(math.dot(ends[a].Position.xz, right)));
            return result;
        }

        private static float3 Mean(List<LaneEnd> ends, List<int> which)
        {
            float3 sum = float3.zero;
            foreach (int i in which)
                sum += ends[i].Direction;
            return math.normalizesafe(sum);
        }

        /// <summary>Angle from one heading to another in degrees, positive to the left, seen from above.</summary>
        private static float Angle(float3 from, float3 to)
        {
            float2 a = math.normalizesafe(from.xz);
            float2 b = math.normalizesafe(to.xz);
            return math.degrees(math.atan2(a.x * b.y - a.y * b.x, math.dot(a, b)));
        }
    }
}
