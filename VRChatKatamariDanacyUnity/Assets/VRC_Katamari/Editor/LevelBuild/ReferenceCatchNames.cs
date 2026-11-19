using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// 参考关「吸到的东西」的三语名字：本机参考表，按 def 查。只在导入参考关时读，
    /// 结果只写进 _RefUnlicensed 场景里的序列化数组，不进 git。
    /// </summary>
    /// <remarks>
    /// 表的位置与 <see cref="ReferenceAbsorbAudio"/> 相同约定：用户目录下的 KatamariRef。
    /// 列为 def、zh_hans、wlk_en、japanese、english（由 WLK REROLL+ 名称表对到初代物件，
    /// 见 Docs/Design/BallLayers/CatchName/CATCH_NAME_HUD.md）。缺表或缺某格时退回 original_defs.json 的 english。
    /// </remarks>
    internal static class ReferenceCatchNames
    {
        internal const string TableRelativePath = "KatamariRef/wlkrr/tables/cancer_to_wlk.tsv";

        internal sealed class Names
        {
            public string Zh = "";
            public string En = "";
            public string Ja = "";
        }

        internal static string TablePath()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(root, TableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>def → 名字。表缺失时返回只有英文的结果并给出 note。</summary>
        internal static Dictionary<string, Names> ByDefinition(string placementPath, out string note)
        {
            note = null;
            var result = new Dictionary<string, Names>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in EnglishByDefinition(placementPath))
                result[pair.Key] = new Names { En = pair.Value };

            string path = TablePath();
            if (!File.Exists(path))
            {
                note = "local " + TableRelativePath + " missing; catch names fall back to English";
                return result;
            }
            using (var reader = File.OpenText(path))
            {
                string[] headers = (reader.ReadLine() ?? "").Split('\t');
                int def = Array.IndexOf(headers, "def");
                int zh = Array.IndexOf(headers, "zh_hans");
                int ja = Array.IndexOf(headers, "japanese");
                int en = Array.IndexOf(headers, "english");
                if (def < 0 || zh < 0 || ja < 0 || en < 0)
                    throw new FormatException(TableRelativePath + " lacks def/zh_hans/japanese/english");
                int width = Math.Max(Math.Max(def, zh), Math.Max(ja, en));
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] cells = line.Split('\t');
                    if (cells.Length <= width || cells[def].Length == 0) continue;
                    if (!result.TryGetValue(cells[def], out Names names))
                    {
                        names = new Names();
                        result[cells[def]] = names;
                    }
                    if (names.Zh.Length == 0) names.Zh = cells[zh].Trim();
                    if (names.Ja.Length == 0) names.Ja = cells[ja].Trim();
                    // english 列是初代英文名，与 original_defs.json 一致；这里只补 defs 里缺的。
                    if (names.En.Length == 0) names.En = cells[en].Trim();
                }
            }
            return result;
        }

        private static Dictionary<string, string> EnglishByDefinition(string placementPath)
        {
            var english = new Dictionary<string, string>(StringComparer.Ordinal);
            string defsPath = Path.Combine(Path.GetDirectoryName(placementPath), "original_defs.json");
            if (!File.Exists(defsPath)) return english;
            var defs = JsonUtility.FromJson<OriginalDefinitions>(File.ReadAllText(defsPath));
            if (defs == null || defs.defs == null) return english;
            foreach (OriginalDefinition d in defs.defs)
                if (d != null && !string.IsNullOrEmpty(d.id) && !string.IsNullOrEmpty(d.english))
                    english[d.id] = d.english;
            return english;
        }

        [Serializable]
        private sealed class OriginalDefinitions { public OriginalDefinition[] defs; }

        [Serializable]
        private sealed class OriginalDefinition { public string id; public string english; }
    }
}
