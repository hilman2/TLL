using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TLL.Components
{
    /// <summary>
    /// Tag: the player took this junction's signals away, and its plan of
    /// the player's waits on the node for them to come back
    /// (JunctionInitSystem.Sleep). It keeps ManagedJunction, its movements,
    /// phases, roads and statistics; everything that runs the signals is
    /// gone, so no system but the set-up sees it. Saved, like the plan.
    /// </summary>
    public struct JunctionDormant : IComponentData, IEmptySerializable
    {
    }
}
