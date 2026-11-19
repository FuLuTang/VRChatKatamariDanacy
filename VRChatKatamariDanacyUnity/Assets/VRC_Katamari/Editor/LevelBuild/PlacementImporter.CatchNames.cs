using System;
using System.Collections.Generic;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRCKatamari.Editor.Hub;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.LevelBuild
{
    public static partial class PlacementImporter
    {
        internal const string CatchNamesZhField = "propCatchNamesZh";
        internal const string CatchNamesEnField = "propCatchNamesEn";
        internal const string CatchNamesJaField = "propCatchNamesJa";

        // 与 SyncAbsorbAudio 同一时机：PickupFieldSync 写完基础行之后，按新行序的 itemId 对齐。
        // 名字只来自本机参考表，所以只有参考关（referencePlacementPath 非空）才有值；
        // 自制关卡写空串，HUD 只显示小模型。
        private static void SyncCatchNames(PickupFieldHost host, PlacementDocument doc,
            string referencePlacementPath, PlacementImportReport report)
        {
            var byItem = new Dictionary<string, ReferenceCatchNames.Names>(StringComparer.Ordinal);
            if (referencePlacementPath != null)
            {
                try
                {
                    Dictionary<string, ReferenceCatchNames.Names> byDef =
                        ReferenceCatchNames.ByDefinition(referencePlacementPath, out string note);
                    if (note != null) report.Note(note);
                    foreach (PropEntry prop in doc.props)
                        if (prop != null && !string.IsNullOrEmpty(prop.itemId) && prop.def != null
                            && byDef.TryGetValue(prop.def, out ReferenceCatchNames.Names names))
                            byItem[prop.itemId] = names;
                }
                catch (Exception ex)
                {
                    byItem.Clear();
                    report.Note("reference catch names ignored: " + ex.Message + "; names left empty");
                }
            }

            var so = new SerializedObject(host);
            SerializedProperty ids = so.FindProperty("itemIds");
            SerializedProperty zh = so.FindProperty(CatchNamesZhField);
            SerializedProperty en = so.FindProperty(CatchNamesEnField);
            SerializedProperty ja = so.FindProperty(CatchNamesJaField);
            if (ids == null || zh == null || en == null || ja == null)
            {
                report.Error("catch names: itemIds or " + CatchNamesZhField + "/" + CatchNamesEnField
                    + "/" + CatchNamesJaField + " missing on " + host.name);
                return;
            }

            int rows = ids.arraySize;
            var expectedZh = new string[rows];
            var expectedEn = new string[rows];
            var expectedJa = new string[rows];
            int withZh = 0, withJa = 0, englishOnly = 0, none = 0;
            zh.arraySize = rows;
            en.arraySize = rows;
            ja.arraySize = rows;
            for (int i = 0; i < rows; i++)
            {
                string itemId = ids.GetArrayElementAtIndex(i).stringValue;
                byItem.TryGetValue(itemId, out ReferenceCatchNames.Names names);
                expectedZh[i] = names != null ? names.Zh : "";
                expectedEn[i] = names != null ? names.En : "";
                expectedJa[i] = names != null ? names.Ja : "";
                zh.GetArrayElementAtIndex(i).stringValue = expectedZh[i];
                en.GetArrayElementAtIndex(i).stringValue = expectedEn[i];
                ja.GetArrayElementAtIndex(i).stringValue = expectedJa[i];
                if (expectedZh[i].Length > 0) withZh++;
                if (expectedJa[i].Length > 0) withJa++;
                if (expectedZh[i].Length == 0 && expectedEn[i].Length > 0) englishOnly++;
                if (expectedZh[i].Length == 0 && expectedEn[i].Length == 0 && expectedJa[i].Length == 0) none++;
            }
            so.ApplyModifiedProperties();
            try
            {
                UdonSharpEditorUtility.CopyProxyToUdon(host);
            }
            catch (Exception ex)
            {
                report.Error("catch names: Udon copy failed on " + host.name + ": " + ex.Message
                    + "; recompile U# and re-run the import");
                return;
            }
            if (!ReadBackMatches(host, CatchNamesZhField, expectedZh)
                || !ReadBackMatches(host, CatchNamesEnField, expectedEn)
                || !ReadBackMatches(host, CatchNamesJaField, expectedJa))
            {
                report.Error("catch names: Udon read-back mismatch on " + host.name + " (" + rows + " rows)");
                return;
            }
            string summary = "catch names on '" + host.name + "': zh " + withZh + "/" + rows
                + ", ja " + withJa + "/" + rows + ", English fallback " + englishOnly
                + ", empty " + none + "; Udon read-back verified";
            report.Note(summary);
            Debug.Log("[catch-names] " + summary);
        }

        private static bool ReadBackMatches(PickupFieldHost host, string field, string[] expected)
        {
            return UdonVariableAccess.TryRead(host, field, out object read) && ArrayMatches(expected, read);
        }
    }
}
