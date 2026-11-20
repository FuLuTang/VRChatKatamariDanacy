using System;
using System.Collections.Generic;
using UdonSharpEditor;
using UnityEditor;
using VRCKatamari.Editor.Hub;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.LevelBuild
{
    public static partial class PlacementImporter
    {
        private static void SyncAreaCollision(PickupFieldHost host, PlacementDocument doc,
            ISet<string> exemptFromField, PlacementImportReport report)
        {
            const string field = "areaCollisionOffItemIds";
            var offIds = new List<string>();
            foreach (PropEntry prop in doc.props)
                if (prop != null && prop.noBallCollision &&
                    (exemptFromField == null || !exemptFromField.Contains(prop.itemId)))
                    offIds.Add(prop.itemId);
            offIds.Sort(StringComparer.Ordinal);
            var serialized = new SerializedObject(host);
            SerializedProperty column = serialized.FindProperty(field);
            if (column == null)
            {
                report.Error("[area_collision] missing " + field + " on " + host.name);
                return;
            }

            string[] expected = offIds.ToArray();
            bool changed = column.arraySize != expected.Length;
            for (int i = 0; !changed && i < expected.Length; i++)
                changed = column.GetArrayElementAtIndex(i).stringValue != expected[i];
            if (changed)
            {
                column.arraySize = expected.Length;
                for (int i = 0; i < expected.Length; i++)
                    column.GetArrayElementAtIndex(i).stringValue = expected[i];
                serialized.ApplyModifiedProperties();
            }

            try
            {
                if (changed) UdonSharpEditorUtility.CopyProxyToUdon(host);
            }
            catch (Exception exception)
            {
                report.Error("[area_collision] Udon copy failed on " + host.name + ": " + exception.Message);
                return;
            }

            if (expected.Length == 0 && !changed) return;
            if (!UdonVariableAccess.TryRead(host, field, out object read) || !ArrayMatches(expected, read) ||
                !UdonVariableAccess.TryRead(host, "itemIds", out object rowsValue) || !(rowsValue is string[] rows))
            {
                report.Error("[area_collision] Udon read-back mismatch on " + host.name);
                return;
            }
            var rowIds = new HashSet<string>(rows, StringComparer.Ordinal);
            foreach (string id in expected)
                if (!rowIds.Contains(id)) report.Error("[area_collision] unresolved pickup row: " + id);
        }
    }
}
