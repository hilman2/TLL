using System;
using Game;

namespace TLL.Systems
{
    /// <summary>
    /// Base of all TLL systems. An exception that leaves a system's update
    /// reaches the game's update loop, which reports it as a critical error
    /// again on every frame and makes the game unplayable. Here it is logged
    /// once and only the failing system is switched off. A system the
    /// junctions cannot run without hands them back to the game in
    /// <see cref="OnSwitchedOff"/>.
    /// </summary>
    public abstract partial class TllSystemBase : GameSystemBase
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
                try
                {
                    OnSwitchedOff();
                }
                catch (Exception cleanup)
                {
                    Mod.Log.Critical(cleanup, $"{GetType().Name} could not hand its junctions back to the game.");
                }
            }
        }

        protected abstract void OnSafeUpdate();

        /// <summary>Called once after the system was switched off by an error.</summary>
        protected virtual void OnSwitchedOff()
        {
        }
    }
}
