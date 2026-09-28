using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TLL.Core.Planning;

namespace TLL.UI.Planner
{
    /// <summary>
    /// The player's presets: one text file each (PlanPreset.ToText) in the
    /// game's user data, under ModsData/TLL/Presets, next to the metrics
    /// log. They live outside the save, so every city sees the same
    /// library, and a preset shared as a file is used by dropping it there.
    /// </summary>
    internal sealed class PresetStore
    {
        public const string Extension = ".tllpreset";

        /// <summary>How often the folder is looked at again, for files added or removed by hand.</summary>
        private static readonly TimeSpan kRescan = TimeSpan.FromSeconds(3);

        public sealed class Entry
        {
            /// <summary>The file name without its extension; stable while the file exists.</summary>
            public string Id;
            public PlanPreset Preset;
        }

        private readonly List<Entry> m_Entries = new List<Entry>();
        private readonly Dictionary<string, DateTime> m_Stamps = new Dictionary<string, DateTime>();
        private readonly HashSet<string> m_Reported = new HashSet<string>();
        private DateTime m_Scanned;
        private string m_Folder;

        /// <summary>Changes with every change to the list, so a caller can tell it needs to look again.</summary>
        public int Version { get; private set; }

        private string Folder
        {
            get
            {
                if (m_Folder == null)
                    m_Folder = Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", "TLL", "Presets");
                return m_Folder;
            }
        }

        /// <summary>Every readable preset, sorted by name. Files that do not read are left out and logged once.</summary>
        public IReadOnlyList<Entry> All()
        {
            if (DateTime.UtcNow - m_Scanned >= kRescan)
                Scan();
            return m_Entries;
        }

        public Entry Find(string id)
        {
            foreach (Entry e in All())
            {
                if (e.Id == id)
                    return e;
            }
            return null;
        }

        /// <summary>Writes a preset to a new file named after it.</summary>
        /// <returns>The new preset's id, or null if it could not be written.</returns>
        public string Save(PlanPreset preset)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string stem = FileStem(preset.Name);
                string id = stem;
                for (int n = 2; File.Exists(PathOf(id)); n++)
                    id = $"{stem} ({n})";
                File.WriteAllText(PathOf(id), preset.ToText(), new UTF8Encoding(false));
                m_Scanned = default;
                return id;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Mod.Log.Warn($"Planner: the preset \"{preset.Name}\" could not be saved: {e.Message}");
                return null;
            }
        }

        public bool Delete(string id)
        {
            try
            {
                string path = PathOf(id);
                if (!File.Exists(path))
                    return false;
                File.Delete(path);
                m_Scanned = default;
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Mod.Log.Warn($"Planner: the preset \"{id}\" could not be deleted: {e.Message}");
                return false;
            }
        }

        private string PathOf(string id)
        {
            return Path.Combine(Folder, id + Extension);
        }

        /// <summary>A file name from a preset's name: characters a file name cannot hold become spaces.</summary>
        private static string FileStem(string name)
        {
            var stem = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name ?? "")
                stem.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
            string result = stem.ToString().Trim();
            if (result.Length > 60)
                result = result.Substring(0, 60).Trim();
            return result.Length > 0 ? result : "Preset";
        }

        /// <summary>Reads files that are new or changed since the last look, and forgets those that are gone.</summary>
        private void Scan()
        {
            m_Scanned = DateTime.UtcNow;
            string[] files;
            try
            {
                files = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*" + Extension) : new string[0];
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }
            bool changed = false;
            var present = new HashSet<string>();
            foreach (string file in files)
            {
                string id = Path.GetFileNameWithoutExtension(file);
                present.Add(id);
                DateTime stamp;
                try
                {
                    stamp = File.GetLastWriteTimeUtc(file);
                }
                catch (IOException)
                {
                    continue;
                }
                if (m_Stamps.TryGetValue(id, out DateTime known) && known == stamp)
                    continue;
                m_Stamps[id] = stamp;
                m_Entries.RemoveAll(e => e.Id == id);
                changed = true;
                PlanPreset preset = null;
                string error;
                try
                {
                    preset = PlanPreset.Parse(File.ReadAllText(file), out error);
                }
                catch (IOException e)
                {
                    error = e.Message;
                }
                if (preset == null)
                {
                    if (m_Reported.Add(id))
                        Mod.Log.Warn($"Planner: the preset file \"{id}{Extension}\" is left out: {error}.");
                    continue;
                }
                m_Entries.Add(new Entry { Id = id, Preset = preset });
            }
            changed |= m_Entries.RemoveAll(e => !present.Contains(e.Id)) > 0;
            foreach (string id in new List<string>(m_Stamps.Keys))
            {
                if (!present.Contains(id))
                    m_Stamps.Remove(id);
            }
            if (changed)
            {
                m_Entries.Sort((a, b) => string.Compare(a.Preset.Name, b.Preset.Name, StringComparison.CurrentCultureIgnoreCase));
                Version++;
            }
        }
    }
}
