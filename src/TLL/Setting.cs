using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using TLL.Core.Control;
using TLL.Core.Planning;

namespace TLL
{
    /// <summary>Control modes offered for the city-wide automation. Flashing and coordination are set per junction.</summary>
    public enum AutoControlMode
    {
        Adaptive,
        Actuated,
        FixedTime,
    }

    [FileLocation("ModsSettings/TLL/TLL")]
    [SettingsUIGroupOrder(kAutomation, kRules, kMaintenance, kDebug)]
    [SettingsUIShowGroupName(kAutomation, kRules, kMaintenance, kDebug)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kAutomation = "Automation";
        public const string kRules = "Rules";
        public const string kMaintenance = "Maintenance";
        public const string kDebug = "Debug";

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(kSection, kAutomation)]
        public bool AutoManageAll { get; set; }

        [SettingsUISection(kSection, kAutomation)]
        public AutoControlMode AutoMode { get; set; }

        [SettingsUISection(kSection, kAutomation)]
        public PlanStrategy AutoStrategy { get; set; }

        [SettingsUISection(kSection, kRules)]
        public bool TurnOnRed { get; set; }

        [SettingsUISection(kSection, kRules)]
        public bool KeepClear { get; set; }

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kMaintenance)]
        public bool ReleaseAll
        {
            set { Requests.ReleaseAll = true; }
        }

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kMaintenance)]
        public bool RebuildVanilla
        {
            set { Requests.RebuildVanilla = true; }
        }

        [SettingsUISection(kSection, kDebug)]
        public bool VerboseLogging { get; set; }

        public ControlMode AutoControl
        {
            get
            {
                switch (AutoMode)
                {
                    case AutoControlMode.Actuated:
                        return ControlMode.Actuated;
                    case AutoControlMode.FixedTime:
                        return ControlMode.FixedTime;
                    default:
                        return ControlMode.Adaptive;
                }
            }
        }

        public override void SetDefaults()
        {
            AutoManageAll = false;
            AutoMode = AutoControlMode.Adaptive;
            AutoStrategy = PlanStrategy.Permissive;
            TurnOnRed = false;
            KeepClear = true;
            VerboseLogging = false;
        }
    }

    /// <summary>
    /// One-off actions asked for in the options screen. The options UI runs
    /// outside the simulation; the systems pick the requests up on their next
    /// update and clear them.
    /// </summary>
    public static class Requests
    {
        public static volatile bool ReleaseAll;
        public static volatile bool RebuildVanilla;
    }
}
