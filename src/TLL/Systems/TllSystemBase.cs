using System;
using Game;

namespace TLL.Systems
{
    /// <summary>
    /// Base of all TLL systems. An exception that leaves a system's update
    /// reaches the game's update loop, which reports it as a critical error
    /// again on every frame and makes the game unplayable. Here it is logged
    /// once and only the failing system is switched off; the junctions it
    /// served keep their last signal state or return to the game.
    /// </summary>
    public abstract class TllSystemBase : GameSystemBase
    {
        protected sealed override void OnUpdate()
        {
            try
            {
                OnSafeUpdate();
            }
            catch (Exception e)
            {
                Mod.Log.Critical(e, $"{GetType().Name} failed and was switched off until the game is restarted.");
                Enabled = false;
            }
        }

        protected abstract void OnSafeUpdate();
    }
}
