using Colossal.IO.AssetDatabase;
using Game.Input;
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

        /// <summary>Every green runs until its queue has left, at most 90 s (ControlMode.Drain).</summary>
        Drain,
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
    [SettingsUIGroupOrder(kAutomation, kRules, kMap, kKeys, kMaintenance, kDebug)]
    [SettingsUIShowGroupName(kAutomation, kRules, kMap, kKeys, kMaintenance, kDebug)]
    [SettingsUIKeyboardAction(kTogglePanel, Usages.kDefaultUsage, Usages.kToolUsage)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kAutomation = "Automation";
        public const string kRules = "Rules";
        public const string kMap = "Map";
        public const string kKeys = "Keys";

        /// <summary>Input action that opens and closes the TLL panel.</summary>
        public const string kTogglePanel = "TogglePanel";
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

        /// <summary>The autopilot forbids turns that cost a junction more than their detour (TurnReview).</summary>
        [SettingsUISection(kSection, kAutomation)]
        public bool AutoTurnBans { get; set; }

        /// <summary>The autopilot fits the lane arrows to the traffic (LaneArrowReview).</summary>
        [SettingsUISection(kSection, kAutomation)]
        public bool AutoLaneArrows { get; set; }

        /// <summary>The autopilot puts priority signs on junctions without signals (PriorityAutopilotSystem).</summary>
        [SettingsUISection(kSection, kAutomation)]
        public bool AutoPrioritySigns { get; set; }

        [SettingsUISection(kSection, kRules)]
        public bool TurnOnRed { get; set; }

        [SettingsUISection(kSection, kRules)]
        public bool KeepClear { get; set; }

        /// <summary>Emergency vehicles get green along their route ahead of them (EmergencyRouteSystem).</summary>
        [SettingsUISection(kSection, kRules)]
        public bool EmergencyGreenWave { get; set; }

        [SettingsUISection(kSection, kMap)]
        public bool ShowProblems { get; set; }

        [SettingsUISection(kSection, kMap)]
        public bool ShowCongestion { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.T, kTogglePanel, alt: true)]
        [SettingsUISection(kSection, kKeys)]
        public ProxyBinding TogglePanelBinding { get; set; }

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kMaintenance)]
        public bool ResetAllToAutomatic
        {
            set { Requests.ResetAllToAutomatic = true; }
        }

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
        public bool ResetLanes
        {
            set { Requests.ResetLanes = true; }
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

        /// <summary>Writes the metrics log (Metrics.MetricsLog).</summary>
        [SettingsUISection(kSection, kDebug)]
        public bool WriteMetrics { get; set; }

        /// <summary>The mode automatic junctions run in when neither a green wave nor flashing applies.</summary>
        public ControlMode AutoControl()
        {
            switch (AutoMode)
            {
                case AutoControlMode.Actuated:
                    return ControlMode.Actuated;
                case AutoControlMode.FixedTime:
                    return ControlMode.FixedTime;
                case AutoControlMode.Drain:
                    return ControlMode.Drain;
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
                default:
                    return PlanStrategy.Permissive;
            }
        }

        /// <summary>
        /// Whether that layout has a scramble: only with the choice
        /// "pedestrian scramble", which is the permissive layout with one.
        /// With the automatic layout the autopilot decides later.
        /// </summary>
        public bool InitialScramble()
        {
            return AutoLayout == AutoLayout.ExclusivePedestrian;
        }

        public override void SetDefaults()
        {
            AutoManageAll = false;
            AutoMode = AutoControlMode.Automatic;
            AutoLayout = AutoLayout.Automatic;
            AutoGreenWaves = true;
            AutoFlash = true;
            AutoTurnBans = true;
            AutoPrioritySigns = true;
            AutoLaneArrows = true;
            TurnOnRed = false;
            KeepClear = true;
            EmergencyGreenWave = true;
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
        public static volatile bool ResetAllToAutomatic;
        public static volatile bool ResetLanes;
    }
}
