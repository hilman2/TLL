using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Net;
using Game.Simulation;
using TLL.Localization;
using TLL.Systems;

namespace TLL
{
    /// <summary>
    /// Entry point. The game creates this class without running its
    /// constructor or field initialisers, so all state is static and set in
    /// <see cref="OnLoad"/>. The game also skips mods that take more than two
    /// seconds to load, so nothing expensive happens here.
    /// </summary>
    public class Mod : IMod
    {
        public static ILog Log { get; } = LogManager.GetLogger("TLL").SetShowsErrorsInUI(false);

        public static Setting Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"Loading TLL {typeof(Mod).Assembly.GetName().Version}");

            Settings = new Setting(this);
            Settings.RegisterKeyBindings();
            Settings.RegisterInOptionsUI();
            LocaleSource.RegisterAll(Settings);
            AssetDatabase.global.LoadSettings("TLL", Settings, new Setting(this));

            updateSystem.UpdateBefore<RebuildRequestSystem>(SystemUpdatePhase.Modification1);
            // On the lanes the game has just built, before anything reads
            // them. See LaneRuleSystem.
            updateSystem.UpdateBefore<LaneRuleSystem, LaneReferencesSystem>(SystemUpdatePhase.Modification4B);
            // Right after the game has written its own signal groups, before
            // the signal poles are derived from them. See JunctionInitSystem.
            updateSystem.UpdateAfter<JunctionInitSystem, TrafficLightInitializationSystem>(SystemUpdatePhase.Modification4B);
            updateSystem.UpdateBefore<SignalControlSystem, TrafficLightSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<AutoManageSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<OptimizerSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<AutopilotSystem, OptimizerSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<CoordinationSystem, AutopilotSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<PriorityAutopilotSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<UI.TllUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<UI.JunctionToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<UI.MapOverlaySystem>(SystemUpdatePhase.ToolUpdate);
        }

        public void OnDispose()
        {
            Log.Info("Unloading TLL");
            Metrics.MetricsLog.Close();
            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
