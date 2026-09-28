using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace TLL.Metrics
{
    /// <summary>
    /// The metrics log, for measuring how well the control works: one folder
    /// per game session in the game's user data, under ModsData/TLL/Metrics,
    /// with one JSON Lines file per table and one line per record. Every
    /// record carries the session, and the session record the commit the mod
    /// was built from, so builds can be compared. tools/metrics/import.py
    /// loads the folders into SQLite.
    /// </summary>
    /// <remarks>
    /// Written as text rather than into SQLite directly: the game ships no
    /// SQLite, and the mod would have to bring a native library for every
    /// platform. A line is complete when written, so a crash loses at most
    /// the records not yet flushed.
    /// </remarks>
    internal static class MetricsLog
    {
        private static string s_Session;
        private static string s_Folder;
        private static readonly Dictionary<string, StreamWriter> s_Files = new Dictionary<string, StreamWriter>();

        /// <summary>Whether records are written: the setting WriteMetrics.</summary>
        public static bool Enabled => Mod.Settings != null && Mod.Settings.WriteMetrics;

        /// <summary>Ends the session; the next record starts a new one. Called when a city has loaded.</summary>
        public static void NewSession()
        {
            Close();
            s_Session = null;
        }

        /// <summary>
        /// A record for <paramref name="table"/>, with the session and the
        /// simulation frame filled in; null while the log is off. Pass it to
        /// <see cref="Write"/> when complete.
        /// </summary>
        public static MetricsRow Row(string table, uint frame)
        {
            if (!Enabled)
                return null;
            if (s_Session == null && !StartSession(frame))
                return null;
            return new MetricsRow(table).Add("session", s_Session).Add("frame", frame);
        }

        public static void Write(MetricsRow row)
        {
            if (row == null || s_Folder == null)
                return;
            try
            {
                if (!s_Files.TryGetValue(row.Table, out StreamWriter file))
                {
                    file = new StreamWriter(Path.Combine(s_Folder, row.Table + ".jsonl"), true, new UTF8Encoding(false));
                    s_Files[row.Table] = file;
                }
                file.WriteLine(row.Finish());
            }
            catch (Exception e)
            {
                // A full disk or a locked file must not stop the simulation.
                Mod.Log.Error(e, $"Metrics: writing {row.Table} failed; the log stops for this session.");
                Close();
                s_Folder = null;
            }
        }

        /// <summary>Writes what is buffered to disk; called once per autopilot round.</summary>
        public static void Flush()
        {
            foreach (StreamWriter file in s_Files.Values)
                file.Flush();
        }

        public static void Close()
        {
            foreach (StreamWriter file in s_Files.Values)
            {
                try
                {
                    file.Dispose();
                }
                catch (Exception)
                {
                    // Nothing left to do with a file that cannot be closed.
                }
            }
            s_Files.Clear();
        }

        private static bool StartSession(uint frame)
        {
            try
            {
                s_Session = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                s_Folder = Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", "TLL", "Metrics", s_Session);
                Directory.CreateDirectory(s_Folder);
            }
            catch (Exception e)
            {
                Mod.Log.Error(e, "Metrics: the folder could not be created; no metrics this session.");
                s_Folder = null;
                return false;
            }
            Setting s = Mod.Settings;
            Write(new MetricsRow("sessions")
                .Add("session", s_Session)
                .Add("frame", frame)
                .Add("started", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                .Add("commit", BuildInfo.Commit)
                .Add("version", typeof(Mod).Assembly.GetName().Version.ToString())
                .Add("auto_manage", s.AutoManageAll)
                .Add("auto_mode", s.AutoMode.ToString())
                .Add("auto_layout", s.AutoLayout.ToString())
                .Add("auto_green_waves", s.AutoGreenWaves)
                .Add("auto_flash", s.AutoFlash)
                .Add("turn_on_red", s.TurnOnRed)
                .Add("keep_clear", s.KeepClear));
            Mod.Log.Info($"Metrics: session {s_Session} in {s_Folder}.");
            return true;
        }
    }

    /// <summary>One record of the metrics log, built field by field.</summary>
    internal sealed class MetricsRow
    {
        private readonly StringBuilder m_Text = new StringBuilder(256);

        public MetricsRow(string table)
        {
            Table = table;
            m_Text.Append('{');
        }

        public string Table { get; }

        public MetricsRow Add(string name, string value)
        {
            Name(name).Append(value == null ? "null" : JsonConvert.ToString(value));
            return this;
        }

        public MetricsRow Add(string name, long value)
        {
            Name(name).Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public MetricsRow Add(string name, float value)
        {
            // Non-numbers have no JSON form; they come from divisions by an
            // empty measurement and mean "not known".
            if (float.IsNaN(value) || float.IsInfinity(value))
                Name(name).Append("null");
            else
                Name(name).Append(value.ToString("0.####", CultureInfo.InvariantCulture));
            return this;
        }

        public MetricsRow Add(string name, bool value)
        {
            Name(name).Append(value ? "true" : "false");
            return this;
        }

        internal string Finish()
        {
            return m_Text.Append('}').ToString();
        }

        private StringBuilder Name(string name)
        {
            if (m_Text.Length > 1)
                m_Text.Append(',');
            return m_Text.Append('"').Append(name).Append("\":");
        }
    }
}
