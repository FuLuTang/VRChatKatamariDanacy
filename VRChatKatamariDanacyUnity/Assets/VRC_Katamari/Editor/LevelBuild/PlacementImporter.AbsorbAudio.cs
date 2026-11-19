using System;
using System.Collections.Generic;
using System.Linq;
using UdonSharpEditor;
using UnityEditor;
using VRCKatamari.Editor.Hub;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.LevelBuild
{
    public static partial class PlacementImporter
    {
        // PickupFieldSync owns the base rows. This optional scream-id column (0 = no scream; the
        // serialized name propAbsorbSoundClasses is kept) joins only after that
        // sync succeeds, by item ID in the freshly written row order (never document order).
        private static void SyncAbsorbAudio(PickupFieldHost host, PlacementDocument doc,
            string referencePlacementPath, PlacementImportReport report)
        {
            var byItem = new Dictionary<string, int>(StringComparer.Ordinal);
            if (referencePlacementPath != null)
            {
                var map = ReferenceAbsorbAudio.Load(out string note);
                if (note != null) report.Note(note);
                if (map != null)
                {
                    try
                    {
                        var byDef = ReferenceAbsorbAudio.ClassesByDefinition(referencePlacementPath, map);
                        foreach (PropEntry prop in doc.props)
                            if (prop != null && !string.IsNullOrEmpty(prop.itemId) && prop.def != null
                                && byDef.TryGetValue(prop.def, out int screamId))
                                byItem[prop.itemId] = screamId;
                    }
                    catch (Exception ex)
                    {
                        byItem.Clear();
                        report.Note("reference absorb data ignored: " + ex.Message + "; no object screams");
                    }
                }
            }
            var so = new SerializedObject(host);
            SerializedProperty ids = so.FindProperty("itemIds");
            SerializedProperty column = so.FindProperty("propAbsorbSoundClasses");
            if (ids == null || column == null)
            {
                report.Error("absorb audio: itemIds or propAbsorbSoundClasses missing on " + host.name);
                return;
            }
            int[] expected = new int[ids.arraySize];
            string[] expectedIds = new string[ids.arraySize];
            int screamRows = 0;
            column.arraySize = expected.Length;
            for (int i = 0; i < expected.Length; i++)
            {
                expectedIds[i] = ids.GetArrayElementAtIndex(i).stringValue;
                byItem.TryGetValue(expectedIds[i], out expected[i]);
                column.GetArrayElementAtIndex(i).intValue = expected[i];
                if (expected[i] > 0) screamRows++;
            }
            so.ApplyModifiedProperties();
            try
            {
                UdonSharpEditorUtility.CopyProxyToUdon(host);
            }
            catch (Exception ex)
            {
                report.Error("absorb audio: Udon copy failed on " + host.name + ": " + ex.Message
                    + "; recompile U# and re-run the import");
                return;
            }
            if (!UdonVariableAccess.TryRead(host, "propAbsorbSoundClasses", out object read)
                || !ArrayMatches(expected, read)
                || !UdonVariableAccess.TryRead(host, "itemIds", out object readIds)
                || !ArrayMatches(expectedIds, readIds))
            {
                report.Error("absorb audio: Udon read-back mismatch on " + host.name + " (" + expected.Length + " rows)");
                return;
            }
            report.Note("propAbsorbSoundClasses on '" + host.name + "': " + screamRows + "/" + expected.Length
                + " rows with scream IDs, Udon read-back verified");
        }

        // Udon may surface an empty serialized array as a null variable value.
        private static bool ArrayMatches<T>(T[] expected, object read)
        {
            if (expected.Length == 0) return read == null || (read is T[] none && none.Length == 0);
            return read is T[] actual && expected.SequenceEqual(actual);
        }
    }
}
