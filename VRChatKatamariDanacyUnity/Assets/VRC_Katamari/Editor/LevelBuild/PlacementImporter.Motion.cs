using System;
using System.Collections.Generic;
using System.Globalization;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// The motion half of the v2 import: turns <c>motions[]</c>, <c>paths[]</c> and the props'
    /// <c>motion</c>/<c>phase</c>/<c>parent</c>/<c>inherit</c> into the serialized columns of
    /// <see cref="MotionHost"/> (<c>LevelContent/&lt;id&gt;/MotionHost</c>).
    /// </summary>
    /// <remarks>
    /// Which props get a row and every number in it is decided by
    /// <see cref="PlacementMotionRows"/> (pure, tested); this file only finds the scene objects
    /// and writes the columns. Idempotent: every column is compared first and written only when
    /// it differs, so a second import reports <c>writes=0</c>; the host component is created once
    /// and never destroyed or re-created. The Animator column is read from the scene (the
    /// skeletal assembler adds the Animators), so running the assembler and importing again is
    /// what wires them.
    /// </remarks>
    public static partial class PlacementImporter
    {
        public const string MotionHostObjectName = "MotionHost";

        private static void ApplyMotionHost(PlacementV2Document doc, Transform levelRoot,
            Transform propsRoot, PlacementImportReport report)
        {
            Dictionary<string, GameObject> index = IndexProps(propsRoot, null);

            var animated = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, GameObject> pair in index)
            {
                if (pair.Value.GetComponentInChildren<Animator>(true) != null) animated.Add(pair.Key);
            }

            MotionRowTable table = PlacementMotionRows.Build(doc, animated);
            for (int i = 0; i < table.Errors.Count; i++) report.Error(V2Tag + table.Errors[i]);
            for (int i = 0; i < table.Notes.Count; i++) report.Note(V2Tag + table.Notes[i]);

            report.V2MotionRows = table.RowCount;
            report.V2MotionPaths = table.PathPointStart.Length;
            SyncPropMobility(levelRoot, table, report);

            MotionHost host = levelRoot.GetComponentInChildren<MotionHost>(true);
            if (table.RowCount == 0)
            {
                report.Note(V2Tag + "no moving, riding or animated props: no MotionHost rows" +
                            (host != null ? " (the existing host keeps its old rows; clear it by hand)" : ""));
                return;
            }

            var transforms = new Transform[table.RowCount];
            var animators = new Animator[table.RowCount];
            for (int r = 0; r < table.RowCount; r++)
            {
                GameObject go;
                if (!index.TryGetValue(table.ItemIds[r], out go))
                {
                    report.Error(V2Tag + "motion row '" + table.ItemIds[r] + "' has no object under " +
                                 HierarchyPath(propsRoot) + "; the MotionHost was not written");
                    return;
                }
                transforms[r] = go.transform;
                animators[r] = go.GetComponentInChildren<Animator>(true);
                if (animators[r] != null) report.V2MotionAnimators++;
            }

            if (host == null)
            {
                var go = new GameObject(MotionHostObjectName);
                Undo.RegisterCreatedObjectUndo(go, UndoLabel);
                Undo.SetTransformParent(go.transform, levelRoot, UndoLabel);
                try
                {
                    host = UdonSharpUndo.AddComponent<MotionHost>(go);
                    report.V2MotionHostWrites++;
                }
                catch (Exception exception)
                {
                    report.Error(V2Tag + "could not add MotionHost to '" + go.name + "': " +
                                 exception.Message + " (does MotionHost.asset, its UdonSharp program " +
                                 "asset, exist next to MotionHost.cs? Unity generates it on import)");
                    return;
                }
            }

            var serialized = new SerializedObject(host);
            int writes = 0;
            writes += WriteObject(serialized, "motor",
                UnityEngine.Object.FindObjectOfType<KatamariBallMotor>(), true, report);
            writes += WriteObjects(serialized, "rowTransforms", transforms, report);
            writes += WriteStrings(serialized, "rowItemIds", table.ItemIds, report);
            writes += WriteInts(serialized, "rowParentRow", table.ParentRow, report);
            writes += WriteInts(serialized, "rowInherit", table.Inherit, report);
            writes += WriteInts(serialized, "rowMotionClass", table.MotionClass, report);
            writes += WriteInts(serialized, "rowPhaseSeed", table.PhaseSeed, report);
            writes += WriteFloats(serialized, "rowUpdatesPerSecond", table.UpdatesPerSecond, report);
            writes += WriteFloats(serialized, "rowHalfHeightM", table.HalfHeightM, report);
            writes += WriteFloats(serialized, "rowSpeedMps", table.SpeedMps, report);
            writes += WriteInts(serialized, "rowSizeGroup", table.SizeGroup, report);
            writes += WriteFloats(serialized, "rowTriggerRadiusM", table.TriggerRadiusM, report);
            writes += WriteInts(serialized, "rowMotionIdCode", table.MotionIdCode, report);
            writes += WriteBools(serialized, "rowLoop", table.Loop, report);
            writes += WriteBools(serialized, "rowSwayTranslation", table.SwayTranslation, report);
            writes += WriteInts(serialized, "rowPathIndex", table.PathIndex, report);
            writes += WriteObjects(serialized, "rowAnimators", animators, report);
            writes += WriteInts(serialized, "pathPointStart", table.PathPointStart, report);
            writes += WriteInts(serialized, "pathPointCount", table.PathPointCount, report);
            writes += WriteBools(serialized, "pathClosed", table.PathClosed, report);
            writes += WriteFloats(serialized, "pathPoints", table.PathPoints, report);

            report.V2MotionHostWrites += writes;
            if (writes > 0)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(host);
            }

            if (writes > 0 || report.V2MotionHostWrites > 0)
            {
                try
                {
                    UdonSharpProgramAsset.CompileAllCsPrograms(false, false);
                    UdonSharpEditorUtility.CopyProxyToUdon(host);
                }
                catch (Exception exception)
                {
                    report.Error(V2Tag + "CopyProxyToUdon threw for MotionHost: " + exception.Message +
                                 ". The columns are on the proxy but not on the backing UdonBehaviour; " +
                                 "re-run the import once the compile has finished");
                }
            }

            report.Note(V2Tag + "motion host: rows=" + table.RowCount + " (own=" + table.OwnProgramRows +
                        ", riders=" + table.RiderRows + ", animation-only=" + table.AnimationOnlyRows +
                        "), paths=" + table.PathPointStart.Length + ", animators=" + report.V2MotionAnimators +
                        ", column writes=" + writes.ToString(CultureInfo.InvariantCulture));
        }

        // ---- column writers: compare first, write only a difference; return columns changed ----

        private static SerializedProperty Resize(SerializedObject serialized, string name, int size,
            PlacementImportReport report, out bool resized)
        {
            resized = false;
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null || !property.isArray)
            {
                report.Error(V2Tag + "MotionHost has no array field '" + name + "'; column not written");
                return null;
            }
            if (property.arraySize != size)
            {
                property.arraySize = size;
                resized = true;
            }
            return property;
        }

        private static int WriteInts(SerializedObject serialized, string name, int[] values,
            PlacementImportReport report)
        {
            bool changed;
            SerializedProperty property = Resize(serialized, name, values.Length, report, out changed);
            if (property == null) return 0;
            for (int i = 0; i < values.Length; i++)
            {
                SerializedProperty element = property.GetArrayElementAtIndex(i);
                if (element.intValue != values[i]) { element.intValue = values[i]; changed = true; }
            }
            return changed ? 1 : 0;
        }

        private static int WriteFloats(SerializedObject serialized, string name, float[] values,
            PlacementImportReport report)
        {
            bool changed;
            SerializedProperty property = Resize(serialized, name, values.Length, report, out changed);
            if (property == null) return 0;
            for (int i = 0; i < values.Length; i++)
            {
                SerializedProperty element = property.GetArrayElementAtIndex(i);
                if (Mathf.Abs(element.floatValue - values[i]) > 1e-6f) { element.floatValue = values[i]; changed = true; }
            }
            return changed ? 1 : 0;
        }

        private static int WriteBools(SerializedObject serialized, string name, bool[] values,
            PlacementImportReport report)
        {
            bool changed;
            SerializedProperty property = Resize(serialized, name, values.Length, report, out changed);
            if (property == null) return 0;
            for (int i = 0; i < values.Length; i++)
            {
                SerializedProperty element = property.GetArrayElementAtIndex(i);
                if (element.boolValue != values[i]) { element.boolValue = values[i]; changed = true; }
            }
            return changed ? 1 : 0;
        }

        private static int WriteStrings(SerializedObject serialized, string name, string[] values,
            PlacementImportReport report)
        {
            bool changed;
            SerializedProperty property = Resize(serialized, name, values.Length, report, out changed);
            if (property == null) return 0;
            for (int i = 0; i < values.Length; i++)
            {
                SerializedProperty element = property.GetArrayElementAtIndex(i);
                if (!string.Equals(element.stringValue, values[i], StringComparison.Ordinal))
                {
                    element.stringValue = values[i];
                    changed = true;
                }
            }
            return changed ? 1 : 0;
        }

        private static int WriteObjects(SerializedObject serialized, string name,
            UnityEngine.Object[] values, PlacementImportReport report)
        {
            bool changed;
            SerializedProperty property = Resize(serialized, name, values.Length, report, out changed);
            if (property == null) return 0;
            for (int i = 0; i < values.Length; i++)
            {
                SerializedProperty element = property.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue != values[i])
                {
                    element.objectReferenceValue = values[i];
                    changed = true;
                }
            }
            return changed ? 1 : 0;
        }

        // Single object field; onlyIfEmpty keeps a reference the operator set by hand.
        private static int WriteObject(SerializedObject serialized, string name,
            UnityEngine.Object value, bool onlyIfEmpty, PlacementImportReport report)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null)
            {
                report.Error(V2Tag + serialized.targetObject.GetType().Name + " has no field '" + name + "'");
                return 0;
            }
            if (value == null) return 0;
            if (onlyIfEmpty && property.objectReferenceValue != null) return 0;
            if (property.objectReferenceValue == value) return 0;
            property.objectReferenceValue = value;
            return 1;
        }
    }
}
