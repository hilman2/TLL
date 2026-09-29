using Unity.Entities;

namespace TLL.Components
{
    /// <summary>
    /// A neighbour of a junction in its cluster (TLL.Core.Coordination.Clusters),
    /// one element per road between them too short for the queue of a red.
    /// Written by the coordination round for every member of a cluster and
    /// removed when the junction leaves it. Not saved: the first round after
    /// a load writes it again.
    ///
    /// The control job's exchange (SignalControlSystem.ClusterJob) reads it
    /// every step to hold greens into a full road and to ask the neighbour
    /// to empty it. Roads are kept as edges, not as movements: a rebuild
    /// renumbers the movements, and the edges stay until the road changes,
    /// which makes the next round write the links anew.
    /// </summary>
    [InternalBufferCapacity(2)]
    public struct ClusterLink : IBufferElementData
    {
        /// <summary>The junction at the other end of the road.</summary>
        public Entity Neighbor;

        /// <summary>The road's edge at this junction.</summary>
        public Entity Edge;

        /// <summary>The road's edge at the neighbour; the same as <see cref="Edge"/> without a plain node between them.</summary>
        public Entity NeighborEdge;

        /// <summary>Vehicles the road holds standing towards the neighbour, over all its lanes.</summary>
        public float CapacityOut;

        /// <summary>Likewise towards this junction.</summary>
        public float CapacityIn;
    }
}
