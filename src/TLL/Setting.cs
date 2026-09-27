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
        /// <summary>The autopilot decides; isolated junctions run adaptive.</summary>
        Automatic,
        Adaptive,
        Actuated,
        FixedTime,
    }

    /// <summary>Phase layouts offered for the city-wide automation.</summary>
    public enum AutoLayout
    {
        /// <summary>The autopilot picks the layout per junction from its traffic.</summary>
        Automatic,
        Permissive,
        ProtectedTurns,
        Split,
        ExclusivePedestrian,
    }

    [FileLocation("ModsSettings/TLL/TLL")]
    [SettingsUIGroupOrder(kAutomation, kRules, kMap, kMaintenance, kDebug)]
    [SettingsUIShowGroupName(kAutomation, kRules, kMap, kMaintenance, kDebug)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kAutomation = "Automation";
        public const string kRules = "Rules";
        public const string kMap = "Map";
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
        public AutoLayout AutoLayout { get; set; }

        [SettingsUISection(kSection, kAutomation)]
        public bool AutoGreenWaves { get; set; }

        [SettingsUISection(kSection, kAutomation)]
        public bool AutoFlash { get; set; }

        [SettingsUISection(kSection, kRules)]
        public bool TurnOnRed { get; set; }

        [SettingsUISection(kSection, kRules)]
        public bool KeepClear { get; set; }

        [SettingsUISection(kSection, kMap)]
        public bool ShowProblems { get; set; }

        [SettingsUISection(kSection, kMap)]
        public bool ShowCongestion { get; set; }

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

        /// <summary>The mode automatic junctions run in when neither a green wave nor flashing applies.</summary>
        public ControlMode AutoControl()
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

        /// <summary>
        /// The layout a junction starts with when the automation takes it
        /// over. With the automatic layout it is the permissive one, which the
        /// autopilot then changes where the traffic calls for it.
        /// </summary>
        public PlanStrategy InitialStrategy()
        {
            switch (AutoLayout)
            {
                case AutoLayout.ProtectedTurns:
                    return PlanStrategy.ProtectedTurns;
                case AutoLayout.Split:
                    return PlanStrategy.Split;
                case AutoLayout.ExclusivePedestrian:
                    return PlanStrategy.ExclusivePedestrian;
                default:
                    return PlanStrategy.Permissive;
            }
        }

        public override void SetDefaults()
        {
            AutoManageAll = false;
            AutoMode = AutoControlMode.Automatic;
            AutoLayout = AutoLayout.Automatic;
            AutoGreenWaves = true;
            AutoFlash = true;
            TurnOnRed = false;
            KeepClear = true;
            ShowProblems = false;
            ShowCongestion = false;
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
        public static volatile bool RebuildGreenWaves;
    }
}
