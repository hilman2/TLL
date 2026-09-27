using Game;
using Game.Common;
using TLL.Components;
using Unity.Entities;

namespace TLL.Systems
{
    /// <summary>
    /// Turns <see cref="RebuildRequest"/> into the game's Updated tag.
    ///
    /// The game removes Updated at the end of every frame, and its
    /// modification phases only react to it within the frame it was added.
    /// TLL decides to release junctions in Modification4B or in the
    /// simulation, both too late for this frame's lane and signal rebuild,
    /// and the end-of-frame barrier that would carry the tag into the next
    /// frame refuses commands during the modification phases. So TLL parks
    /// the request on its own tag, and this system, running before anything
    /// else in Modification1, adds Updated where the whole modification pass
    /// of that frame sees it.
    /// </summary>
    public class RebuildRequestSystem : TllSystemBase
    {
        private EntityQuery m_Query;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(ComponentType.ReadOnly<RebuildRequest>());
            RequireForUpdate(m_Query);
        }

        protected override void OnSafeUpdate()
        {
            EntityManager.AddComponent<Updated>(m_Query);
            EntityManager.RemoveComponent<RebuildRequest>(m_Query);
        }
    }
}
