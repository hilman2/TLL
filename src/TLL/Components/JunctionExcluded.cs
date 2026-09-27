using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TLL.Components
{
    /// <summary>
    /// Tag: the city-wide automation must not take over this junction, either
    /// because the player said so or because TLL cannot control it (level
    /// crossings, movable bridges, layouts the planner rejected). Saved, so
    /// the automation does not retry after every load.
    /// </summary>
    public struct JunctionExcluded : IComponentData, IEmptySerializable
    {
    }
}
