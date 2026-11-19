using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>供参考关导入和合集装配共用的本机音效清单。</summary>
    internal static class ReferenceAbsorbAudio
    {
        public const string ClipAssetFolder = "Assets/_RefUnlicensed/Audio/";

        [Serializable]
        internal sealed class Manifest
        {
            public string schema;
            public string defaultRollInGroup;
            public RollInGroup[] rollInGroups;
            public Scream[] screams;
            public Level[] levels;
        }

        [Serializable]
        internal sealed class RollInGroup
        {
            public string id;
            public string[] wavs;
        }

        [Serializable]
        internal sealed class Scream
        {
            public int screamId;
            public string[] wavs;
        }

        [Serializable]
        internal sealed class Level
        {
            public string levelId;
            public string rollInGroup;
            public PropScream[] propScreams;
        }

        [Serializable]
        internal sealed class PropScream
        {
            public string def;
            public int screamId;
        }

        internal static Manifest Load(out string note)
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string path = Path.Combine(root, "KatamariRef", "audio", "ref_audio_map.json");
            note = null;
            if (!File.Exists(path))
            {
                note = "local ref_audio_map.json missing; no reference clips";
                return null;
            }
            try
            {
                Manifest map = JsonUtility.FromJson<Manifest>(File.ReadAllText(path));
                Validate(map);
                return map;
            }
            catch (Exception ex)
            {
                note = "local ref_audio_map.json ignored: " + ex.Message + "; no reference clips";
                return null;
            }
        }

        private static void Validate(Manifest map)
        {
            if (map == null || map.schema != "katamari.ref-absorb-audio/2" || map.rollInGroups == null
                || map.screams == null || map.levels == null)
                throw new FormatException("expected schema katamari.ref-absorb-audio/2 and all three tables");

            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (RollInGroup group in map.rollInGroups)
            {
                if (group == null || string.IsNullOrWhiteSpace(group.id) || !groups.Add(group.id))
                    throw new FormatException("invalid or duplicate roll-in group id");
                ValidateWavs(group.wavs);
            }
            if (!groups.Contains(map.defaultRollInGroup))
                throw new FormatException("defaultRollInGroup must name a roll-in group");

            var screams = new HashSet<int>();
            foreach (Scream scream in map.screams)
            {
                if (scream == null || scream.screamId <= 0 || scream.screamId > ushort.MaxValue
                    || !screams.Add(scream.screamId))
                    throw new FormatException("invalid or duplicate screamId");
                ValidateWavs(scream.wavs);
            }

            var levels = new HashSet<string>(StringComparer.Ordinal);
            foreach (Level level in map.levels)
            {
                if (level == null || string.IsNullOrWhiteSpace(level.levelId) || !levels.Add(level.levelId)
                    || !groups.Contains(level.rollInGroup) || level.propScreams == null)
                    throw new FormatException("invalid level id, roll-in group or propScreams table");
                var defs = new HashSet<string>(StringComparer.Ordinal);
                foreach (PropScream prop in level.propScreams)
                    if (prop == null || string.IsNullOrWhiteSpace(prop.def) || !defs.Add(prop.def)
                        || !screams.Contains(prop.screamId))
                        throw new FormatException("invalid def or screamId in " + level.levelId);
            }
        }

        private static void ValidateWavs(string[] wavs)
        {
            if (wavs == null || wavs.Length == 0) throw new FormatException("empty WAV list");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string wav in wavs)
                if (string.IsNullOrWhiteSpace(wav) || wav.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
                    || !wav.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !names.Add(wav))
                    throw new FormatException("use distinct WAV basenames within a list");
        }

        internal static Level FindLevel(Manifest map, string levelId)
        {
            foreach (Level level in map.levels)
                if (string.Equals(level.levelId, levelId, StringComparison.Ordinal)) return level;
            return null;
        }

        // 保留原导入接口及序列化列；其中的正整数现在是真正的物件叫声编号。
        internal static Dictionary<string, int> ClassesByDefinition(string placementPath, Manifest map)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            string levelId = Path.GetFileName(Path.GetDirectoryName(placementPath));
            Level level = FindLevel(map, levelId);
            if (level != null)
                foreach (PropScream prop in level.propScreams) result.Add(prop.def, prop.screamId);
            return result;
        }
    }
}
