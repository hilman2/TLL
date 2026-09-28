using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TLL.Core.Control;

namespace TLL.Core.Planning
{
    /// <summary>One phase of a preset, with its timing in seconds.</summary>
    public struct PresetPhase
    {
        /// <summary>Movements with green, as a mask over <see cref="PlanPreset.Movements"/>.</summary>
        public ulong Green;

        public float MinGreen;
        public float MaxGreen;
        public float GreenTime;

        /// <summary>Whether this is the pedestrians' own phase, which runs on request (PhaseFlags.Scramble).</summary>
        public bool Scramble;
    }

    /// <summary>Which roads one lane of an arm leads into.</summary>
    public struct PresetLane
    {
        public int Arm;

        /// <summary>The lane, counted from the kerb outwards.</summary>
        public int Lane;

        /// <summary>The arms it leads into, as a bit mask.</summary>
        public int Targets;
    }

    /// <summary>Lane changes forbidden on the approach of one arm (solid lines).</summary>
    public struct PresetSolidLines
    {
        public int Arm;

        /// <summary>Road pieces back from the stop line.</summary>
        public int Pieces;

        /// <summary>Bit k: the line between lane k and lane k + 1, from the kerb, is solid.</summary>
        public int Lines;
    }

    /// <summary>
    /// A plan saved to be used at other junctions: the arms of the junction
    /// it was made at, its movements and phases, and what else the player
    /// chose to keep with it. Arms are numbered counter-clockwise from the
    /// first, whose direction is 0 degrees; movements refer to them by
    /// number, so the preset carries no entity of any city.
    /// </summary>
    public sealed class PlanPreset
    {
        /// <summary>Layout of the text form. Raise it when the meaning of a line changes; new lines need no raise, unknown ones are skipped.</summary>
        public const int Version = 1;

        public string Name = "";

        /// <summary>The city the preset was made in drives on the left. A preset is mirrored for a city on the other side.</summary>
        public bool LeftHandTraffic;

        /// <summary>Direction of each arm in degrees, counter-clockwise, the first at 0.</summary>
        public float[] Angles = new float[0];

        /// <summary>The movements, Source and Target as arm numbers; Target -1 for a crosswalk. LaneCount is not kept.</summary>
        public readonly List<Movement> Movements = new List<Movement>();

        public readonly List<PresetPhase> Phases = new List<PresetPhase>();

        /// <summary>The preset carries the timing; without it the junction keeps its own mode and greens.</summary>
        public bool HasTiming;

        public ControlMode Mode = ControlMode.Adaptive;
        public bool AutoTiming = true;
        public bool TurnOnRed;
        public bool Scramble;

        /// <summary>The preset carries lane arrows; empty otherwise.</summary>
        public readonly List<PresetLane> Lanes = new List<PresetLane>();

        /// <summary>The preset carries turn bans, even if the list is empty: then it allows every turn.</summary>
        public bool HasTurnBans;

        /// <summary>Forbidden turns, as pairs of arms.</summary>
        public readonly List<(int From, int To)> ForbiddenTurns = new List<(int From, int To)>();

        public readonly List<PresetSolidLines> SolidLines = new List<PresetSolidLines>();

        /// <summary>The preset as text, for its file and for sharing: one fact per line.</summary>
        public string ToText()
        {
            var text = new StringBuilder();
            text.Append("TLL preset ").Append(Version).Append('\n');
            text.Append("name ").Append(Name.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            text.Append("traffic ").Append(LeftHandTraffic ? "left" : "right").Append('\n');
            text.Append("arms");
            foreach (float a in Angles)
                text.Append(' ').Append(Number(a));
            text.Append('\n');
            foreach (Movement m in Movements)
                text.Append("move ").Append(m.Source).Append(' ').Append(m.Target).Append(' ').Append(m.Kind).Append('\n');
            foreach (PresetPhase p in Phases)
            {
                text.Append("phase ").Append(p.Green.ToString("x", CultureInfo.InvariantCulture))
                    .Append(' ').Append(Number(p.MinGreen)).Append(' ').Append(Number(p.MaxGreen)).Append(' ').Append(Number(p.GreenTime));
                if (p.Scramble)
                    text.Append(" scramble");
                text.Append('\n');
            }
            if (HasTiming)
            {
                text.Append("timing ").Append(Mode);
                if (AutoTiming)
                    text.Append(" auto");
                if (TurnOnRed)
                    text.Append(" turnonred");
                if (Scramble)
                    text.Append(" scramble");
                text.Append('\n');
            }
            foreach (PresetLane lane in Lanes)
            {
                text.Append("lane ").Append(lane.Arm).Append(' ').Append(lane.Lane).Append(' ');
                bool first = true;
                for (int arm = 0; arm < 31; arm++)
                {
                    if ((lane.Targets & (1 << arm)) == 0)
                        continue;
                    if (!first)
                        text.Append(',');
                    text.Append(arm);
                    first = false;
                }
                text.Append('\n');
            }
            if (HasTurnBans)
            {
                text.Append("turnbans\n");
                foreach ((int from, int to) in ForbiddenTurns)
                    text.Append("forbid ").Append(from).Append(' ').Append(to).Append('\n');
            }
            foreach (PresetSolidLines s in SolidLines)
                text.Append("solid ").Append(s.Arm).Append(' ').Append(s.Pieces).Append(' ').Append(s.Lines.ToString("x", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("end\n");
            return text.ToString();
        }

        /// <summary>
        /// Reads a preset from its text form, as written by <see cref="ToText"/>.
        /// Text around it, as when pasted from a chat, is skipped; so are
        /// lines of a later version this one does not know.
        /// </summary>
        /// <param name="error">Why the text is no preset; null on success.</param>
        /// <returns>The preset, or null.</returns>
        public static PlanPreset Parse(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text))
            {
                error = "empty";
                return null;
            }
            string[] lines = text.Replace("\r", "").Split('\n');
            int start = -1;
            for (int i = 0; i < lines.Length && start < 0; i++)
            {
                if (lines[i].Trim().StartsWith("TLL preset ", StringComparison.Ordinal))
                    start = i;
            }
            if (start < 0)
            {
                error = "no TLL preset in the text";
                return null;
            }
            string[] head = Split(lines[start]);
            if (head.Length < 3 || !int.TryParse(head[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) || version < 1)
            {
                error = "unknown preset version";
                return null;
            }
            if (version > Version)
            {
                error = $"made by a newer TLL (version {version})";
                return null;
            }

            var preset = new PlanPreset();
            bool ended = false;
            try
            {
                for (int i = start + 1; i < lines.Length && !ended; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0)
                        continue;
                    string[] f = Split(line);
                    switch (f[0])
                    {
                        case "name":
                            preset.Name = line.Length > 5 ? line.Substring(5).Trim() : "";
                            break;
                        case "traffic":
                            preset.LeftHandTraffic = f.Length > 1 && f[1] == "left";
                            break;
                        case "arms":
                            preset.Angles = new float[f.Length - 1];
                            for (int k = 1; k < f.Length; k++)
                                preset.Angles[k - 1] = ParseFloat(f[k]);
                            break;
                        case "move":
                            preset.Movements.Add(new Movement(ParseInt(f[1]), ParseInt(f[2]), (MovementKind)Enum.Parse(typeof(MovementKind), f[3])));
                            break;
                        case "phase":
                            preset.Phases.Add(new PresetPhase
                            {
                                Green = ulong.Parse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                                MinGreen = ParseFloat(f[2]),
                                MaxGreen = ParseFloat(f[3]),
                                GreenTime = ParseFloat(f[4]),
                                Scramble = Array.IndexOf(f, "scramble", 5) >= 0,
                            });
                            break;
                        case "timing":
                            preset.HasTiming = true;
                            preset.Mode = (ControlMode)Enum.Parse(typeof(ControlMode), f[1]);
                            preset.AutoTiming = Array.IndexOf(f, "auto", 2) >= 0;
                            preset.TurnOnRed = Array.IndexOf(f, "turnonred", 2) >= 0;
                            preset.Scramble = Array.IndexOf(f, "scramble", 2) >= 0;
                            break;
                        case "lane":
                            int targets = 0;
                            if (f.Length > 3)
                            {
                                foreach (string arm in f[3].Split(','))
                                    targets |= 1 << ParseInt(arm);
                            }
                            preset.Lanes.Add(new PresetLane { Arm = ParseInt(f[1]), Lane = ParseInt(f[2]), Targets = targets });
                            break;
                        case "turnbans":
                            preset.HasTurnBans = true;
                            break;
                        case "forbid":
                            preset.HasTurnBans = true;
                            preset.ForbiddenTurns.Add((ParseInt(f[1]), ParseInt(f[2])));
                            break;
                        case "solid":
                            preset.SolidLines.Add(new PresetSolidLines
                            {
                                Arm = ParseInt(f[1]),
                                Pieces = ParseInt(f[2]),
                                Lines = int.Parse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            });
                            break;
                        case "end":
                            ended = true;
                            break;
                    }
                }
            }
            catch (Exception e) when (e is FormatException || e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException)
            {
                error = "a line of the preset cannot be read";
                return null;
            }
            error = preset.Validate();
            return error == null ? preset : null;
        }

        /// <summary>Why the preset cannot be used, or null if it can.</summary>
        public string Validate()
        {
            int arms = Angles.Length;
            if (arms < 2 || arms > 16)
                return "it has no junction shape";
            if (Movements.Count == 0 || Movements.Count > 64)
                return "it has no movements";
            foreach (Movement m in Movements)
            {
                bool crosswalk = m.Kind == MovementKind.Pedestrian;
                if (m.Source < 0 || m.Source >= arms || m.Target >= arms || (m.Target < 0) != crosswalk)
                    return "a movement leads nowhere";
            }
            if (Phases.Count == 0 || Phases.Count > PhasePlanner.MaxPhases)
                return "it has no usable phases";
            ulong all = Movements.Count >= 64 ? ulong.MaxValue : (1UL << Movements.Count) - 1UL;
            foreach (PresetPhase p in Phases)
            {
                if ((p.Green & ~all) != 0UL)
                    return "a phase names a movement it does not have";
            }
            foreach (PresetLane lane in Lanes)
            {
                if (lane.Arm < 0 || lane.Arm >= arms || lane.Lane < 0 || lane.Targets == 0 || (lane.Targets >> arms) != 0)
                    return "a lane leads nowhere";
            }
            foreach ((int from, int to) in ForbiddenTurns)
            {
                if (from < 0 || from >= arms || to < 0 || to >= arms)
                    return "a turn ban names a road it does not have";
            }
            foreach (PresetSolidLines s in SolidLines)
            {
                if (s.Arm < 0 || s.Arm >= arms || s.Pieces < 0)
                    return "solid lines on a road it does not have";
            }
            return null;
        }

        /// <summary>The phases' masks, for PlanTransfer.</summary>
        public List<ulong> Greens()
        {
            var result = new List<ulong>();
            foreach (PresetPhase p in Phases)
                result.Add(p.Green);
            return result;
        }

        /// <summary>
        /// The preset for a city driving on <paramref name="leftHandTraffic"/>:
        /// itself, or a mirrored copy for the other side, with left and right
        /// turns and the order of the arms swapped (PlanTransfer.Mirror).
        /// Lanes are counted from the kerb and stay as they are.
        /// </summary>
        public PlanPreset For(bool leftHandTraffic)
        {
            if (leftHandTraffic == LeftHandTraffic)
                return this;
            var movements = new List<Movement>(Movements);
            float[] angles = PlanTransfer.Mirror(Angles, movements);
            var result = new PlanPreset
            {
                Name = Name,
                LeftHandTraffic = leftHandTraffic,
                Angles = angles,
                HasTiming = HasTiming,
                Mode = Mode,
                AutoTiming = AutoTiming,
                TurnOnRed = TurnOnRed,
                Scramble = Scramble,
                HasTurnBans = HasTurnBans,
            };
            result.Movements.AddRange(movements);
            result.Phases.AddRange(Phases);
            result.Lanes.AddRange(Lanes);
            result.ForbiddenTurns.AddRange(ForbiddenTurns);
            result.SolidLines.AddRange(SolidLines);
            return result;
        }

        private static string[] Split(string line)
        {
            return line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string Number(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static float ParseFloat(string s)
        {
            return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static int ParseInt(string s)
        {
            return int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
    }
}
