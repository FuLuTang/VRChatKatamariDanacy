using System;
using System.Collections.Generic;
using System.Linq;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.LevelBuild
{
    public static class ReferenceParityMath
    {
        public const int Tolerance = 0;
        public const int SampleLimit = 10;

        public sealed class Comparison
        {
            public int Matched;
            public int Missing;
            public int Extra;
            public string[] MissingIds;
            public string[] ExtraIds;
        }

        // A duplicate is an extra even when it numerically cancels a missing instance.
        public static Comparison Compare(IEnumerable<string> expected, IEnumerable<string> actual)
        {
            var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string id in expected)
                remaining[id] = remaining.TryGetValue(id, out int count) ? count + 1 : 1;

            var result = new Comparison();
            var extra = new List<string>();
            foreach (string id in actual)
            {
                if (remaining.TryGetValue(id, out int count) && count > 0)
                {
                    remaining[id] = count - 1;
                    result.Matched++;
                }
                else extra.Add(id);
            }
            string[] missing = remaining.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToArray();
            result.Missing = remaining.Values.Sum();
            result.Extra = extra.Count;
            result.MissingIds = Sample(missing);
            result.ExtraIds = Sample(extra);
            return result;
        }

        public static string[] Sample(IEnumerable<string> names) => names.Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).Take(SampleLimit).ToArray();

        public static bool ExpectsVisible(PlacementV2Def def) =>
            def != null && !string.IsNullOrWhiteSpace(def.Model);

        public static bool DeclaresMotion(PlacementV2Prop prop) =>
            prop.EffectiveBehaviour == "motion" || prop.EffectiveBehaviour == "attached" ||
            prop.EffectiveBehaviour == "ambient";

        public static IEnumerable<string> ExpectedStaticBoxes(PlacementV2Document document)
        {
            foreach (PlacementV2Solid solid in document.Solids)
            {
                if (solid == null || solid.Role != "box" || solid.Shape == PlacementV2Rules.ShapePlane ||
                    !AreaCollisionMath.CollidesInArea(solid.HitOnAreaNo, document.Meta.AreaNo)) continue;
                for (int i = 0; i < PlacementV2Rules.SolidStaticObbCount(solid); i++) yield return solid.Id;
            }
        }

        // This side table is optional, but a partial join must not turn unknown into false.
        public static HashSet<string> ReadAnimatedIds(PlacementV2Document document, string json)
        {
            LevelJsonNode root = LevelJsonParser.ParseText(json);
            LevelJsonNode instances = root.Get("instances");
            if (root.GetString("format") != "katamari.motion_table/1" || instances == null || !instances.IsArray)
                throw new FormatException("不是 katamari.motion_table/1 实例表");
            var indices = new Dictionary<int, bool>();
            foreach (LevelJsonNode row in instances.Items)
            {
                int index = row.GetInt("instanceIndex", -1);
                LevelJsonNode animated = row.Get("animated");
                if (index < 0 || animated == null || animated.Kind != LevelJsonKind.Bool || indices.ContainsKey(index))
                    throw new FormatException("原作实例索引无效、重复，或缺少布尔 animated");
                indices.Add(index, animated.BoolValue);
            }
            var result = new HashSet<string>(StringComparer.Ordinal);
            var used = new HashSet<int>();
            foreach (PlacementV2Prop prop in document.Props)
            {
                if (!prop.HasInstanceIndex || !used.Add(prop.InstanceIndex) ||
                    !indices.TryGetValue(prop.InstanceIndex, out bool animated))
                    throw new FormatException("原作实例表无法唯一匹配 " + prop.ItemId);
                if (animated) result.Add(prop.ItemId);
            }
            return result;
        }
    }
}
