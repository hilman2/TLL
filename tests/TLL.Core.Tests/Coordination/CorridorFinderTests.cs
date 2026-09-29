using System;
using System.Collections.Generic;
using System.Linq;
using TLL.Core.Coordination;
using Xunit;

namespace TLL.Core.Tests.Coordination
{
    public class CorridorFinderTests
    {
        // Approaches of a four-way junction: 0 east, 1 north, 2 west, 3 south.
        private static readonly int[] Cross = { 2, 3, 0, 1 };

        /// <summary>
        /// A grid of four-way junctions, <paramref name="columns"/> wide and
        /// <paramref name="rows"/> high. East-west roads weigh
        /// <paramref name="eastWest"/>, north-south roads 1.
        /// </summary>
        private static SignalNetwork Grid(int columns, int rows, float eastWest, float spacing = 200f)
        {
            var net = new SignalNetwork();
            for (int i = 0; i < columns * rows; i++)
                net.OppositeOf.Add(Cross);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    int id = r * columns + c;
                    if (c + 1 < columns)
                        net.Links.Add(new SignalLink { A = id, ApproachA = 0, B = id + 1, ApproachB = 2, Length = spacing, Speed = 14f, Weight = eastWest });
                    if (r + 1 < rows)
                        net.Links.Add(new SignalLink { A = id, ApproachA = 1, B = id + columns, ApproachB = 3, Length = spacing, Speed = 14f, Weight = 1f });
                }
            }
            return net;
        }

        [Fact]
        public void HeavyRoadsBecomeFullCorridors()
        {
            SignalNetwork net = Grid(5, 3, eastWest: 3f);
            List<CorridorPath> corridors = CorridorFinder.Find(net, 800f);

            var rows = corridors.Where(c => c.Junctions.Count == 5).ToList();
            Assert.Equal(3, rows.Count);
            foreach (CorridorPath c in rows)
            {
                int row = c.Junctions[0] / 5;
                Assert.All(c.Junctions, j => Assert.Equal(row, j / 5));
            }
        }

        [Fact]
        public void AClusterTakesItsJunctionFromAHeavierRoadAcross()
        {
            // The middle row's roads are too short for a red; the middle
            // column's carry ten times the traffic. The junction where they
            // cross goes to the row.
            SignalNetwork net = Grid(3, 3, eastWest: 1f);
            for (int i = 0; i < net.Links.Count; i++)
            {
                SignalLink l = net.Links[i];
                if ((l.A == 1 && l.B == 4) || (l.A == 4 && l.B == 7))
                    l.Weight = 10f;
                if ((l.A == 3 && l.B == 4) || (l.A == 4 && l.B == 5))
                    l.Tight = true;
                net.Links[i] = l;
            }
            List<CorridorPath> corridors = CorridorFinder.Find(net, 800f);

            CorridorPath withMiddle = corridors.Single(c => c.Junctions.Contains(4));
            Assert.Equal(new[] { 3, 4, 5 }, withMiddle.Junctions.OrderBy(j => j));
        }

        [Fact]
        public void NoJunctionIsInTwoCorridors()
        {
            var random = new Random(3);
            for (int round = 0; round < 50; round++)
            {
                SignalNetwork net = Grid(2 + random.Next(6), 2 + random.Next(6), eastWest: (float)random.NextDouble() * 3f);
                for (int i = 0; i < net.Links.Count; i++)
                {
                    SignalLink l = net.Links[i];
                    l.Weight = (float)random.NextDouble();
                    net.Links[i] = l;
                }
                List<CorridorPath> corridors = CorridorFinder.Find(net, 800f);
                var all = corridors.SelectMany(c => c.Junctions).ToList();
                Assert.Equal(all.Count, all.Distinct().Count());
            }
        }

        [Fact]
        public void CorridorGoesStraightThroughEveryJunction()
        {
            var random = new Random(9);
            for (int round = 0; round < 50; round++)
            {
                SignalNetwork net = Grid(2 + random.Next(6), 2 + random.Next(6), eastWest: (float)random.NextDouble() * 3f);
                foreach (CorridorPath c in CorridorFinder.Find(net, 800f))
                {
                    for (int i = 1; i < c.Junctions.Count - 1; i++)
                        Assert.Equal(net.OppositeOf[c.Junctions[i]][c.ApproachBack[i]], c.ApproachAhead[i]);
                    for (int i = 0; i < c.Links.Count; i++)
                    {
                        Assert.Equal(c.Junctions[i], c.Links[i].A);
                        Assert.Equal(c.Junctions[i + 1], c.Links[i].B);
                        Assert.Equal(c.ApproachAhead[i], c.Links[i].ApproachA);
                        Assert.Equal(c.ApproachBack[i + 1], c.Links[i].ApproachB);
                    }
                }
            }
        }

        [Fact]
        public void LinksBeyondReachAreNotCoordinated()
        {
            SignalNetwork net = Grid(4, 1, eastWest: 1f, spacing: 1200f);
            Assert.Empty(CorridorFinder.Find(net, 800f));
        }

        [Fact]
        public void CorridorStopsWhereTheRoadBends()
        {
            // Three junctions in a row, but at the middle one the road leaves
            // by the north approach instead of going straight on.
            var net = new SignalNetwork();
            net.OppositeOf.Add(Cross);
            net.OppositeOf.Add(Cross);
            net.OppositeOf.Add(Cross);
            net.Links.Add(new SignalLink { A = 0, ApproachA = 0, B = 1, ApproachB = 2, Length = 200f, Speed = 14f, Weight = 5f });
            net.Links.Add(new SignalLink { A = 1, ApproachA = 1, B = 2, ApproachB = 3, Length = 200f, Speed = 14f, Weight = 1f });
            List<CorridorPath> corridors = CorridorFinder.Find(net, 800f);
            Assert.Single(corridors);
            Assert.Equal(new[] { 0, 1 }, corridors[0].Junctions.OrderBy(j => j));
        }
    }
}
