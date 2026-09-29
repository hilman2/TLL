using TLL.Components;
using TLL.Core.Coordination;
using TLL.Core.Planning;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Systems
{
    public partial class SignalControlSystem
    {
        /// <summary>
        /// The exchange between the neighbours of a cluster, once per step
        /// after the control job: for each road between two members, how
        /// much room it has left, counted from the queue at its far end.
        ///
        /// - Into a road without room, a junction holds its green
        ///   (JunctionRuntime.HeldMovements): its side road waits instead
        ///   of turning into a queue.
        /// - Out of a road its neighbour waits to feed, a junction gives
        ///   green first (JunctionRuntime.FlushMovements). That neighbour's
        ///   own roads further on do the same for it, so a full main road is
        ///   emptied from its front, along the whole cluster.
        ///
        /// Each junction writes only its own runtime and reads its
        /// neighbours' queues from the step just done, so the members run in
        /// parallel and see each other one step late, a quarter second.
        /// </summary>
        [BurstCompile]
        private struct ClusterJob : IJobChunk
        {
            [ReadOnly] public BufferTypeHandle<ClusterLink> LinkType;
            [ReadOnly] public BufferTypeHandle<JunctionMovement> MovementType;
            [ReadOnly] public BufferTypeHandle<MovementCounter> CounterType;
            public ComponentTypeHandle<JunctionRuntime> RuntimeType;

            [ReadOnly] public BufferLookup<JunctionMovement> Movements;
            [ReadOnly] public BufferLookup<MovementCounter> Counters;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                BufferAccessor<ClusterLink> linkBuffers = chunk.GetBufferAccessor(ref LinkType);
                BufferAccessor<JunctionMovement> movementBuffers = chunk.GetBufferAccessor(ref MovementType);
                BufferAccessor<MovementCounter> counterBuffers = chunk.GetBufferAccessor(ref CounterType);
                NativeArray<JunctionRuntime> runtimes = chunk.GetNativeArray(ref RuntimeType);
                for (int i = 0; i < chunk.Count; i++)
                {
                    DynamicBuffer<JunctionMovement> movements = movementBuffers[i];
                    DynamicBuffer<MovementCounter> counters = counterBuffers[i];
                    ulong held = 0UL;
                    ulong flush = 0UL;
                    if (counters.Length == movements.Length && movements.Length <= 64)
                    {
                        DynamicBuffer<ClusterLink> links = linkBuffers[i];
                        for (int l = 0; l < links.Length; l++)
                            Exchange(links[l], movements, counters, ref held, ref flush);
                    }
                    JunctionRuntime runtime = runtimes[i];
                    runtime.HeldMovements = held;
                    runtime.FlushMovements = flush;
                    runtimes[i] = runtime;
                }
            }

            private void Exchange(ClusterLink link, DynamicBuffer<JunctionMovement> movements, DynamicBuffer<MovementCounter> counters,
                ref ulong held, ref ulong flush)
            {
                if (!Movements.TryGetBuffer(link.Neighbor, out DynamicBuffer<JunctionMovement> theirs)
                    || !Counters.TryGetBuffer(link.Neighbor, out DynamicBuffer<MovementCounter> theirCounters)
                    || theirs.Length != theirCounters.Length)
                    return;

                // Towards the neighbour: its queue from this road is what
                // stands on it. Away from it: what waits there to come here.
                float queueThere = 0f;
                float waitingThere = 0f;
                for (int k = 0; k < theirs.Length; k++)
                {
                    if (!Car(theirs[k]))
                        continue;
                    if (theirs[k].Source == link.NeighborEdge)
                        queueThere += theirCounters[k].Waiting;
                    if (theirs[k].Target == link.NeighborEdge)
                        waitingThere += theirCounters[k].Waiting;
                }
                float queueHere = 0f;
                for (int m = 0; m < movements.Length; m++)
                {
                    if (Car(movements[m]) && movements[m].Source == link.Edge)
                        queueHere += counters[m].Waiting;
                }

                bool fullOut = link.CapacityOut - queueThere < Clusters.MinRoom;
                // Room for what waits there, or at least for a few: less, and
                // this junction empties the road first.
                float roomIn = link.CapacityIn - queueHere;
                bool neededIn = waitingThere >= 1f && roomIn < math.max(Clusters.MinRoom, waitingThere);
                for (int m = 0; m < movements.Length; m++)
                {
                    if (!Car(movements[m]))
                        continue;
                    if (fullOut && movements[m].Target == link.Edge)
                        held |= 1UL << m;
                    if (neededIn && movements[m].Source == link.Edge)
                        flush |= 1UL << m;
                }
            }

            /// <summary>Cars fill the road between the junctions; people and trams on their own track do not.</summary>
            private static bool Car(JunctionMovement movement)
            {
                return movement.Kind != MovementKind.Pedestrian && movement.Kind != MovementKind.Track;
            }
        }
    }
}
