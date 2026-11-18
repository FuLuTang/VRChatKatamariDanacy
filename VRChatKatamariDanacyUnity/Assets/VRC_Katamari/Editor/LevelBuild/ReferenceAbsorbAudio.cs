using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>Optional local-only audio manifest, shared by the placement and Hub assemblers.</summary>
    internal static class ReferenceAbsorbAudio
    {
        public const string ClipAssetFolder = "Assets/_RefUnlicensed/Audio/";

        [Serializable]
        internal sealed class Manifest
        {
            public string schema;
            public Group[] groups;
        }

        [Serializable]
        internal sealed class Group
        {
            public int soundClass;
            public string selectorHex;
            public string[] wavs;
        }

        private static string ReferenceRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "KatamariRef");
        }

        internal static Manifest Load(out string note)
        {
            string path = Path.Combine(ReferenceRoot(), "audio", "ref_audio_map.json");
            note = null;
            if (!File.Exists(path))
            {
                note = "local ref_audio_map.json missing; using class 0 / no reference clips";
                return null;
            }
            try
            {
                Manifest map = JsonUtility.FromJson<Manifest>(File.ReadAllText(path));
                if (map == null || map.schema != "katamari.ref-absorb-audio/1" || map.groups == null)
                    throw new FormatException("unexpected schema or missing groups");
                var classes = new HashSet<int>();
                var selectors = new HashSet<int>();
                foreach (Group group in map.groups)
                {
                    if (group == null || group.soundClass < 0 || !classes.Add(group.soundClass)
                        || group.wavs == null || group.wavs.Length == 0)
                        throw new FormatException("invalid or duplicate sound class, or empty wav group");
                    if (group.soundClass == 0 ? !string.IsNullOrEmpty(group.selectorHex)
                        : !TrySelector(group.selectorHex, out int selector) || selector == 0
                        || !selectors.Add(selector))
                        throw new FormatException("invalid or duplicate selectorHex");
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string wav in group.wavs)
                        if (string.IsNullOrEmpty(wav) || wav.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
                            || !wav.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !names.Add(wav))
                            throw new FormatException("use distinct WAV basenames within a group");
                }
                if (!classes.Contains(0)) throw new FormatException("missing default class 0");
                return map;
            }
            catch (Exception ex)
            {
                note = "local ref_audio_map.json ignored: " + ex.Message + "; using class 0 / no reference clips";
                return null;
            }
        }

        // The TSV exporter emits digit-only hex cells with a spreadsheet '.0' suffix.
        internal static bool TrySelector(string cell, out int selector)
        {
            selector = 0;
            if (string.IsNullOrEmpty(cell)) return false;
            if (cell.EndsWith(".0", StringComparison.Ordinal)) cell = cell.Substring(0, cell.Length - 2);
            return int.TryParse(cell, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out selector)
                && selector >= 0 && selector <= 255;
        }

        internal static Dictionary<string, int> ClassesByDefinition(string placementPath, Manifest map)
        {
            string defsPath = Path.Combine(Path.GetDirectoryName(placementPath), "original_defs.json");
            string tablePath = Path.Combine(ReferenceRoot(), "meta", "tables", "raw_reroll_dump__name_props_0x30.tsv");
            var defs = JsonUtility.FromJson<OriginalDefinitions>(File.ReadAllText(defsPath));
            if (defs == null || defs.defs == null) throw new FormatException("original_defs.json has no defs");
            var classBySelector = new Dictionary<int, int>();
            foreach (Group group in map.groups)
                if (group.soundClass > 0 && TrySelector(group.selectorHex, out int selector))
                    classBySelector.Add(selector, group.soundClass);

            var classByMono = new Dictionary<int, int>();
            using (var reader = File.OpenText(tablePath))
            {
                string[] headers = (reader.ReadLine() ?? "").Split('\t');
                int name = Array.IndexOf(headers, "nameIndex");
                int select = Array.IndexOf(headers, "0x19");
                if (name < 0 || select < 0) throw new FormatException("name_props TSV lacks nameIndex or 0x19");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] cells = line.Split('\t');
                    if (cells.Length > Math.Max(name, select) && TryMono(cells[name], out int mono)
                        && TrySelector(cells[select], out int selector)
                        && classBySelector.TryGetValue(selector, out int soundClass))
                        classByMono.Add(mono, soundClass);
                }
            }
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (OriginalDefinition def in defs.defs)
                if (def != null && !string.IsNullOrEmpty(def.id) && classByMono.TryGetValue(def.mono, out int soundClass))
                    result.Add(def.id, soundClass);
            return result;
        }

        private static bool TryMono(string cell, out int mono)
        {
            if (cell.EndsWith(".0", StringComparison.Ordinal)) cell = cell.Substring(0, cell.Length - 2);
            return int.TryParse(cell, NumberStyles.None, CultureInfo.InvariantCulture, out mono);
        }

        [Serializable]
        private sealed class OriginalDefinitions { public OriginalDefinition[] defs; }

        [Serializable]
        private sealed class OriginalDefinition { public string id; public int mono = -1; }
    }
}
