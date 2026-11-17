using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRCKatamari.BallLayers;
using VRCKatamari.BallMotor;
using VRCKatamari.Labs.PhysicsTestField;
using VRCKatamari.Story;
using UdonSharp;
using UdonSharpEditor;
using Object = UnityEngine.Object;

namespace VRCKatamari.Editor.PhysicsTestField
{
    /// <summary>
    /// Bakes the field's criteria file into the scene: one <see cref="PhysicsTestCell"/> per wired
    /// cell, its arena, its start pose, its activation rect, its drive timeline and its criteria as
    /// parallel arrays, plus the driver's cell list and preset diameters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the editor half of the "one authoring copy" rule
    /// (<c>Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md</c> section 7.3): the Udon runtime cannot read
    /// disk JSON, and a human must not retype 9 cells x 60 checks by hand, so exactly one tool reads
    /// <c>physics_test_field.json</c> and writes the serialized arrays.
    /// </para>
    /// <para>
    /// What it does <b>not</b> do: create U# components (the one object it does create is each
    /// cell's sandbox sign, a plain <c>TextMesh</c>). A UdonSharpBehaviour in a scene needs its
    /// program asset, its backing <c>UdonBehaviour</c> and the proxy-to-Udon copy, which is the
    /// seven-step sequence in <c>Labs/README.md</c>; doing it from a script and getting it wrong
    /// would produce a component that looks wired and runs nothing. So the operator adds
    /// <see cref="PhysicsTestCell"/> / <see cref="PhysicsTestFieldDriver"/> in the Inspector once,
    /// and this tool fills them (and says which are missing).
    /// </para>
    /// <para>
    /// It also does not save the scene: the import leaves it dirty so the operator can inspect and
    /// undo, exactly like <c>PlacementImporter</c>.
    /// </para>
    /// </remarks>
    public static class PhysicsTestFieldCriteriaImporter
    {
        public const string MenuPath = "VRC Katamari/Level/Import Physics Test Field Criteria";
        public const string FieldJsonRepoRelative = "Docs/Design/BallMotor/physics_test_field.json";
        public const string ManagedRootName = "LevelContent";

        // ---- DTOs (JsonUtility: public fields, no dictionaries, no jagged arrays) ----

        [Serializable]
        public class FieldDocument
        {
            public int schema_version;
            public string id;
            public string status;
            public DiameterEntry[] diameters;
            public DriverEntry driver;
            public SandboxEntry sandbox;
            public CellEntry[] cells;
        }

        /// <summary>
        /// The manual sandbox (PHYSICS_TEST_FIELD section 0.2): the frame arena's id (its
        /// geometry is that cell's placement, not this file) and where the ball is put.
        /// </summary>
        [Serializable]
        public class SandboxEntry
        {
            public string arena_id;
            public float[] spawn;
            public float spawn_heading_deg;
        }

        [Serializable]
        public class DiameterEntry
        {
            public string label;
            public float diameter_m;
            public float radius_m;
            public float step_limit_m;
        }

        [Serializable]
        public class DriverEntry
        {
            public string component;
            public string[] kind_whitelist;
        }

        [Serializable]
        public class CellEntry
        {
            public string id;
            public string slug;
            public string placement;
            public bool wired;
            public float nominal_m;
            public float stop_seconds;
            // The sandbox's world-space sign at this cell's start mark (ASCII: the built-in font
            // has no CJK glyphs); absent = no sign.
            public string sandbox_label;
            // Which preset diameters the cell is driven at, in diameters[] order; absent = all.
            public bool[] run_at_d;
            public StartPoseEntry start_pose;
            public FootprintEntry footprint;
            public DriveEntry[] drive;
            public CheckEntry[] checks;
        }

        [Serializable]
        public class StartPoseEntry
        {
            public float[] center;
            public float heading_deg;
        }

        [Serializable]
        public class FootprintEntry
        {
            public float[] center;
            public float[] size;
        }

        [Serializable]
        public class DriveEntry
        {
            public float t;
            public float forward;
            public float turn;
            public float lateral;
        }

        [Serializable]
        public class CheckEntry
        {
            public string id;
            public string metric;
            public string op;
            public float limit;
            public string unit;
            public string readout;
            public string gate;
            public string[] expect;
            public bool[] applies_d;
            public V2Entry v2;
        }

        /// <summary>
        /// One criterion's v2 override (design PHYSICS_TEST_FIELD section 5.6). Absent means the
        /// base row above is the criterion.
        /// </summary>
        /// <remarks>
        /// JsonUtility cannot represent absence for this field: a check whose JSON has no
        /// <c>v2</c> key still deserializes to an all-default instance. Presence is therefore a
        /// content question - use <see cref="HasV2Override"/>, never <c>v2 != null</c>.
        /// <para>
        /// Only the fields that actually differ are carried. <c>op</c> always carries <c>limit</c>
        /// (one decision); <c>threshold</c> is the symbolic alternative to a baked limit and is
        /// derived by the driver at run time from ContactTuning. <c>not_applicable</c> is the
        /// shorthand for "n/a at every diameter" and is written as an all-zero expect mask, which
        /// the driver already reports as SKIPPED_N_A.
        /// </para>
        /// </remarks>
        [Serializable]
        public class V2Entry
        {
            public string[] expect;
            public string metric;
            public string op;
            public float limit;
            public string threshold;
            public bool not_applicable;
            public string note;
        }

        /// <summary>Only the placement keys this tool reads; JsonUtility ignores the rest.</summary>
        [Serializable]
        public class PlacementDocument
        {
            public PlacementStructureEntry[] structures;
        }

        [Serializable]
        public class PlacementStructureEntry
        {
            public string id;
            public string kind;
            public string role;
        }

        /// <summary>
        /// What the cell's placement says about ramps, resolved against the scene's own arena:
        /// the placement names the structures, the arena's serialized colliders are the geometry
        /// the motor will actually see (the same rule as approach_m).
        /// </summary>
        private sealed class CellGeometry
        {
            public bool HasRamp;
            public float RampFootM;
            public bool HasProbe;
            public Vector3 ProbeCenter;
            public Vector3 ProbeNormal;
            public Vector3 ProbeRight;
            public float ProbeHalfWidth;
            public float ProbeHalfRun;
        }

        // ---- report ------------------------------------------------------------

        /// <summary>
        /// What one import did. Public because it is the tool's structured answer (the importer
        /// also returns the text) and because the EditMode tests drive the encoders directly -
        /// an editor tool has no other seam without a scene.
        /// </summary>
        public sealed class Report
        {
            public int CellsWritten;
            public int ChecksWritten;
            public int V2Rows;
            public int CellsMissing;
            public int ArenasMissing;
            public int DriverFields;
            public int LabelsWritten;
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Notes = new List<string>();

            public void Error(string message)
            {
                Errors.Add(message);
            }

            public void Note(string message)
            {
                Notes.Add(message);
            }

            public bool Ok
            {
                get { return Errors.Count == 0; }
            }

            public string Build()
            {
                var text = new StringBuilder();
                text.Append("[ptf] criteria: cells=").Append(CellsWritten)
                    .Append(" checks=").Append(ChecksWritten)
                    .Append(" v2Rows=").Append(V2Rows)
                    .Append(" missingCells=").Append(CellsMissing)
                    .Append(" missingArenas=").Append(ArenasMissing)
                    .Append(" driverFields=").Append(DriverFields)
                    .Append(" labels=").Append(LabelsWritten)
                    .Append(" errors=").Append(Errors.Count);
                for (int i = 0; i < Notes.Count; i++) text.AppendLine().Append("  note: ").Append(Notes[i]);
                for (int i = 0; i < Errors.Count; i++) text.AppendLine().Append("  error: ").Append(Errors[i]);
                if (Errors.Count == 0)
                {
                    text.AppendLine()
                        .Append("  the scene is left dirty on purpose: this tool never saves");
                }

                return text.ToString();
            }
        }

        // ---- menu / entry points ------------------------------------------------

        [MenuItem(MenuPath, false, 201)]
        public static void ImportFromMenu()
        {
            Import();
        }

        /// <summary>Runs the bake and returns the report text (also logged).</summary>
        public static string Import()
        {
            var report = new Report();
            string path = ResolveFieldPath();
            if (path == null)
            {
                report.Error(FieldJsonRepoRelative + " not found above the Unity project; the tool " +
                             "expects the docs checkout next to VRChatKatamariDanacyUnity");
                Debug.LogError(report.Build());
                return report.Build();
            }

            FieldDocument doc;
            try
            {
                doc = JsonUtility.FromJson<FieldDocument>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                report.Error("cannot read " + path + ": " + exception.Message);
                Debug.LogError(report.Build());
                return report.Build();
            }

            if (doc == null || doc.cells == null)
            {
                report.Error(path + " has no cells[]; is it the field criteria file?");
                Debug.LogError(report.Build());
                return report.Build();
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                report.Error("exit Play Mode first; the import writes serialized scene data");
                Debug.LogError(report.Build());
                return report.Build();
            }

            PhysicsTestFieldDriver driver = Object.FindObjectOfType<PhysicsTestFieldDriver>();
            if (driver == null)
            {
                report.Note("no PhysicsTestFieldDriver in the scene: the cells are filled but the " +
                            "driver's cells[] / presets were not");
            }

            var written = new List<PhysicsTestCell>();
            for (int i = 0; i < doc.cells.Length; i++)
            {
                CellEntry cell = doc.cells[i];
                if (cell == null || string.IsNullOrEmpty(cell.id)) continue;
                if (!cell.wired)
                {
                    report.Note("cell " + cell.id + " is not wired (no placement yet); skipped");
                    continue;
                }

                PhysicsTestCell component = FindCell(cell.id);
                if (component == null)
                {
                    report.CellsMissing++;
                    report.Note("no PhysicsTestCell with cellId '" + cell.id + "' in the scene: add " +
                                "one (an empty object under Cells/), set its cellId in the Inspector, " +
                                "then import again");
                    continue;
                }

                LevelArenaHost arena = FindArena(cell.id);
                if (arena == null)
                {
                    report.ArenasMissing++;
                    report.Note("no LevelArenaHost on " + ManagedRootName + "/" + cell.id +
                                ": run the placement import for that cell, add the component to that " +
                                "object, then import again");
                }

                Vector2 deckCenter;
                Vector2 deckHalf;
                float approach = ComputeApproach(arena, cell, out deckCenter, out deckHalf);
                CellGeometry geometry = ReadCellGeometry(path, arena, cell, report);
                int checks = WriteCell(component, cell, arena, approach, deckCenter, deckHalf,
                    geometry, report);
                if (checks >= 0)
                {
                    EnsureSandboxLabel(component, cell, report);
                    report.CellsWritten++;
                    report.ChecksWritten += checks;
                    written.Add(component);
                }
            }

            if (driver != null)
            {
                report.DriverFields = WriteDriver(driver, doc, written, report);
                report.DriverFields += WriteSandbox(driver, doc, report);
            }

            if (report.V2Rows > 0)
            {
                report.Note("override rows: " + report.V2Rows + " criteria carry an override block; "
                    + "the driver judges the override instead of the base row (design section 5.6)");
            }

            LabBallSizeSetter setter = Object.FindObjectOfType<LabBallSizeSetter>();
            if (setter != null)
            {
                report.DriverFields += WriteBallSize(setter, doc, report);
                report.DriverFields += WriteSizeModel(setter, driver, report);
            }
            else if (written.Count > 0)
            {
                report.Note("no LabBallSizeSetter in the scene: the presets were not filled, so the " +
                            "driver can only run the baseline diameter (it reports BLOCKED_UNTIL_setter " +
                            "for the others)");
            }

            // Copy last: every serialized field this tool writes lives on the proxy, and only the
            // copy onto the backing UdonBehaviour makes it exist at run time.
            CopyProxies(written, driver, report);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            string text = report.Build();
            if (report.Ok) Debug.Log(text);
            else Debug.LogError(text);
            return text;
        }

        // ---- cell writing -------------------------------------------------------

        private static int WriteCell(PhysicsTestCell component, CellEntry cell, LevelArenaHost arena,
            float approachM, Vector2 deckCenter, Vector2 deckHalf, CellGeometry geometry, Report report)
        {
            var serialized = new SerializedObject(component);
            int checkCount = cell.checks == null ? 0 : cell.checks.Length;

            if (!SetString(serialized, "cellId", cell.id, report)) return -1;
            SetObject(serialized, "arena", arena, report);
            SetVector3(serialized, "startCenter", ToVector3(cell.start_pose == null ? null : cell.start_pose.center,
                new Vector3(0f, 0.15f, 0f)), report);
            SetFloat(serialized, "startHeadingDegrees", cell.start_pose == null ? 0f : cell.start_pose.heading_deg,
                report);
            SetVector2(serialized, "footprintCenter",
                ToVector2(cell.footprint == null ? null : cell.footprint.center, Vector2.zero), report);
            SetVector2(serialized, "footprintSize",
                ToVector2(cell.footprint == null ? null : cell.footprint.size, Vector2.zero), report);
            SetFloat(serialized, "nominalM", cell.nominal_m, report);
            SetFloat(serialized, "approachM", approachM, report);
            SetVector2(serialized, "deckCenterXZ", deckCenter, report);
            SetVector2(serialized, "deckHalfSizeM", deckHalf, report);
            SetBool(serialized, "rampClimb", geometry != null && geometry.HasRamp, report);
            SetFloat(serialized, "rampFootM", geometry == null ? 0f : geometry.RampFootM, report);
            SetBool(serialized, "rampProbe", geometry != null && geometry.HasProbe, report);
            if (geometry != null && geometry.HasProbe)
            {
                SetVector3(serialized, "rampProbeCenter", geometry.ProbeCenter, report);
                SetVector3(serialized, "rampProbeNormal", geometry.ProbeNormal, report);
                SetVector3(serialized, "rampProbeRight", geometry.ProbeRight, report);
                SetFloat(serialized, "rampProbeHalfWidth", geometry.ProbeHalfWidth, report);
                SetFloat(serialized, "rampProbeHalfRun", geometry.ProbeHalfRun, report);
            }
            SetFloat(serialized, "stopSeconds", cell.stop_seconds > 0f ? cell.stop_seconds : 1.5f, report);
            SetSkipDiameterMask(serialized, cell, report);

            int driveCount = cell.drive == null ? 0 : cell.drive.Length;
            SerializedProperty times = RequireProperty(serialized, "driveTimes", report);
            SerializedProperty forward = RequireProperty(serialized, "driveForward", report);
            SerializedProperty turn = RequireProperty(serialized, "driveTurn", report);
            SerializedProperty lateral = RequireProperty(serialized, "driveLateral", report);
            if (times == null || forward == null || turn == null || lateral == null) return -1;

            times.arraySize = driveCount;
            forward.arraySize = driveCount;
            turn.arraySize = driveCount;
            lateral.arraySize = driveCount;
            for (int i = 0; i < driveCount; i++)
            {
                DriveEntry row = cell.drive[i];
                if (row == null) continue;
                times.GetArrayElementAtIndex(i).floatValue = row.t;
                forward.GetArrayElementAtIndex(i).floatValue = row.forward;
                turn.GetArrayElementAtIndex(i).floatValue = row.turn;
                lateral.GetArrayElementAtIndex(i).floatValue = row.lateral;
            }

            if (!WriteChecks(serialized, cell, report)) return -1;

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(component);
            return checkCount;
        }

        private static bool WriteChecks(SerializedObject serialized, CellEntry cell, Report report)
        {
            SerializedProperty ids = RequireProperty(serialized, "checkIds", report);
            SerializedProperty metrics = RequireProperty(serialized, "checkMetrics", report);
            SerializedProperty ops = RequireProperty(serialized, "checkOps", report);
            SerializedProperty limits = RequireProperty(serialized, "checkLimits", report);
            SerializedProperty readouts = RequireProperty(serialized, "checkReadouts", report);
            SerializedProperty gates = RequireProperty(serialized, "checkGateStages", report);
            SerializedProperty masks = RequireProperty(serialized, "checkExpectMasks", report);
            SerializedProperty applies = RequireProperty(serialized, "checkAppliesMasks", report);
            SerializedProperty hasV2 = RequireProperty(serialized, "checkHasV2", report);
            SerializedProperty v2Metrics = RequireProperty(serialized, "checkV2Metrics", report);
            SerializedProperty v2Ops = RequireProperty(serialized, "checkV2Ops", report);
            SerializedProperty v2Limits = RequireProperty(serialized, "checkV2Limits", report);
            SerializedProperty v2Expects = RequireProperty(serialized, "checkV2ExpectMasks", report);
            SerializedProperty v2Thresholds = RequireProperty(serialized, "checkV2Thresholds", report);
            if (ids == null || metrics == null || ops == null || limits == null || readouts == null ||
                gates == null || masks == null || applies == null || hasV2 == null ||
                v2Metrics == null || v2Ops == null || v2Limits == null || v2Expects == null ||
                v2Thresholds == null)
            {
                return false;
            }

            int count = cell.checks == null ? 0 : cell.checks.Length;
            ids.arraySize = count;
            metrics.arraySize = count;
            ops.arraySize = count;
            limits.arraySize = count;
            readouts.arraySize = count;
            gates.arraySize = count;
            masks.arraySize = count;
            applies.arraySize = count;
            hasV2.arraySize = count;
            v2Metrics.arraySize = count;
            v2Ops.arraySize = count;
            v2Limits.arraySize = count;
            v2Expects.arraySize = count;
            v2Thresholds.arraySize = count;

            for (int i = 0; i < count; i++)
            {
                CheckEntry check = cell.checks[i];
                if (check == null)
                {
                    report.Error(cell.id + " checks[" + i + "] is null");
                    return false;
                }

                ids.GetArrayElementAtIndex(i).stringValue = check.id;
                metrics.GetArrayElementAtIndex(i).stringValue = check.metric;
                ops.GetArrayElementAtIndex(i).intValue = OpCode(check.op);
                limits.GetArrayElementAtIndex(i).floatValue = check.limit;
                readouts.GetArrayElementAtIndex(i).intValue = ReadoutCode(check.readout);
                gates.GetArrayElementAtIndex(i).intValue = GateCode(check.gate);
                masks.GetArrayElementAtIndex(i).intValue = ExpectMask(cell, check, report);
                applies.GetArrayElementAtIndex(i).intValue = AppliesMask(cell, check, report);

                // The v2 row (design section 5.6). Its sentinels are chosen so "no override" and
                // "an override whose value happens to be 0" stay different: "" / -1 inherit, and a
                // 0 expect mask is a real answer (n/a at every diameter). HasV2Override, not
                // "check.v2 != null", because JsonUtility materialises a default V2Entry for
                // every check (see its remarks: the pass10 defect tagged all 59 rows).
                bool hasOverride = HasV2Override(check.v2);
                hasV2.GetArrayElementAtIndex(i).intValue = hasOverride ? 1 : 0;
                v2Metrics.GetArrayElementAtIndex(i).stringValue =
                    hasOverride && check.v2.metric != null ? check.v2.metric : "";
                v2Ops.GetArrayElementAtIndex(i).intValue =
                    hasOverride ? V2OpCode(cell, check, report) : -1;
                v2Limits.GetArrayElementAtIndex(i).floatValue = hasOverride ? check.v2.limit : 0f;
                v2Expects.GetArrayElementAtIndex(i).intValue =
                    hasOverride ? V2ExpectMask(cell, check, report) : -1;
                v2Thresholds.GetArrayElementAtIndex(i).intValue =
                    hasOverride ? V2ThresholdCode(cell, check, report) : 0;
                if (hasOverride) report.V2Rows++;
            }

            return true;
        }

        // ---- driver -------------------------------------------------------------

        private static int WriteDriver(PhysicsTestFieldDriver driver, FieldDocument doc,
            List<PhysicsTestCell> cells, Report report)
        {
            var serialized = new SerializedObject(driver);
            int written = 0;

            SerializedProperty cellArray = RequireProperty(serialized, "cells", report);
            if (cellArray != null)
            {
                cellArray.arraySize = cells.Count;
                for (int i = 0; i < cells.Count; i++)
                {
                    cellArray.GetArrayElementAtIndex(i).objectReferenceValue = cells[i];
                }

                written++;
            }

            SerializedProperty presets = RequireProperty(serialized, "presetDiametersM", report);
            if (presets != null && doc.diameters != null)
            {
                presets.arraySize = doc.diameters.Length;
                for (int i = 0; i < doc.diameters.Length; i++)
                {
                    presets.GetArrayElementAtIndex(i).floatValue = doc.diameters[i].diameter_m;
                }

                written++;
            }

            SerializedProperty kinds = RequireProperty(serialized, "kindWhitelist", report);
            if (kinds != null && doc.driver != null && doc.driver.kind_whitelist != null)
            {
                kinds.arraySize = doc.driver.kind_whitelist.Length;
                for (int i = 0; i < doc.driver.kind_whitelist.Length; i++)
                {
                    kinds.GetArrayElementAtIndex(i).stringValue = doc.driver.kind_whitelist[i];
                }

                written++;
            }

            if (written > 0)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(driver);
            }

            return written;
        }

        /// <summary>
        /// Links the driver to the sandbox frame arena (<c>LevelContent/&lt;sandbox.arena_id&gt;</c>)
        /// and bakes the spawn pose. The frame is an arena like any cell's: its geometry (the big
        /// floor, the 4 outer planes) is <c>Levels/physics_test_field/SANDBOX/placement.json</c>.
        /// </summary>
        private static int WriteSandbox(PhysicsTestFieldDriver driver, FieldDocument doc, Report report)
        {
            if (doc.sandbox == null || string.IsNullOrEmpty(doc.sandbox.arena_id))
            {
                report.Note("the criteria file has no sandbox block: the driver's sandbox frame and " +
                            "spawn were not written");
                return 0;
            }

            var serialized = new SerializedObject(driver);
            int written = 0;
            LevelArenaHost frame = FindArena(doc.sandbox.arena_id);
            if (frame == null)
            {
                report.Note("no LevelArenaHost on " + ManagedRootName + "/" + doc.sandbox.arena_id +
                            ": import that placement, add the component to that object, then import " +
                            "again (the sandbox opens without an outer wall until then)");
            }

            SerializedProperty frameProperty = RequireProperty(serialized, "sandboxFrame", report);
            if (frameProperty != null)
            {
                frameProperty.objectReferenceValue = frame;
                written++;
            }

            SetVector3(serialized, "sandboxSpawn",
                ToVector3(doc.sandbox.spawn, new Vector3(0f, 0.15f, 0f)), report);
            SetFloat(serialized, "sandboxSpawnHeadingDegrees", doc.sandbox.spawn_heading_deg, report);
            written += 2;

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(driver);
            return written;
        }

        private const string SandboxLabelName = "SandboxLabel";
        private const float SandboxLabelHeightM = 1.4f;
        // fontSize 64 at characterSize 1 is about 4 world units tall (the greybox builder's
        // measurement); 0.1 makes a 0.4 m line.
        private const float SandboxLabelScale = 0.1f;

        /// <summary>
        /// Creates or refreshes the cell's world-space sign: a legacy <c>TextMesh</c> (the same
        /// component and built-in font the greybox builder's labels use) standing upright at the
        /// cell's start mark, readable from the south, where the sandbox spawn is. Position is
        /// derived (footprint x, start z), text is the criteria file's.
        /// </summary>
        private static void EnsureSandboxLabel(PhysicsTestCell component, CellEntry cell, Report report)
        {
            if (string.IsNullOrEmpty(cell.sandbox_label)) return;
            Font font = null;
            try
            {
                font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch (Exception)
            {
                font = null;
            }

            if (font == null)
            {
                report.Note("built-in font 'LegacyRuntime.ttf' unavailable; the sandbox signs were skipped");
                return;
            }

            Transform existing = component.transform.Find(SandboxLabelName);
            GameObject go;
            if (existing == null)
            {
                go = new GameObject(SandboxLabelName);
                Undo.RegisterCreatedObjectUndo(go, "Create sandbox label");
                go.transform.SetParent(component.transform, false);
            }
            else
            {
                go = existing.gameObject;
            }

            Vector3 start = ToVector3(cell.start_pose == null ? null : cell.start_pose.center, Vector3.zero);
            Vector2 center = ToVector2(cell.footprint == null ? null : cell.footprint.center, Vector2.zero);
            go.transform.position = new Vector3(center.x, SandboxLabelHeightM, start.z);
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one * SandboxLabelScale;

            TextMesh mesh = go.GetComponent<TextMesh>();
            if (mesh == null) mesh = go.AddComponent<TextMesh>();
            mesh.font = font;
            mesh.fontSize = 64;
            mesh.characterSize = 1f;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.text = cell.sandbox_label;
            mesh.color = Color.white;
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null && font.material != null) renderer.sharedMaterial = font.material;
            report.LabelsWritten++;
        }

        /// <summary>
        /// Fills the setter's preset arrays. They are the same numbers as the driver's, from the same
        /// file: the label is what a uGUI button passes, and comparing text avoids parsing a float in
        /// Udon.
        /// </summary>
        private static int WriteBallSize(LabBallSizeSetter setter, FieldDocument doc, Report report)
        {
            if (doc.diameters == null) return 0;
            var serialized = new SerializedObject(setter);
            int written = 0;

            SerializedProperty diameters = RequireProperty(serialized, "presetDiametersM", report);
            if (diameters != null)
            {
                diameters.arraySize = doc.diameters.Length;
                for (int i = 0; i < doc.diameters.Length; i++)
                {
                    diameters.GetArrayElementAtIndex(i).floatValue = doc.diameters[i].diameter_m;
                }

                written++;
            }

            SerializedProperty labels = RequireProperty(serialized, "presetLabels", report);
            if (labels != null)
            {
                labels.arraySize = doc.diameters.Length;
                for (int i = 0; i < doc.diameters.Length; i++)
                {
                    labels.GetArrayElementAtIndex(i).stringValue = doc.diameters[i].label;
                }

                written++;
            }

            if (written > 0)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(setter);
            }

            return written;
        }

        /// <summary>
        /// Mirrors the one motor number the setter's analytic diameter quantum needs.
        /// </summary>
        /// <remarks>
        /// The quantum is <c>D / (3 * totalMm3)</c>: <c>D^3</c> is linear in the total volume, so
        /// the sphere constant and the semantic/core ratio cancel and the START volume is the
        /// only input left. Since steps C+D that input is <c>startVolumeMm3</c> (fixed-point
        /// mm^3), and a level's <c>startDiameterWorldM</c> is the authoritative per-level form:
        /// this reads the diameter when it is set and otherwise the serialized start volume, so
        /// one conversion (the size model's) decides the baked number. When the driver has no
        /// motor link, the setter is left at 0 and the driver reports <c>size_accuracy</c> as
        /// unavailable with <c>reason=sizeModelMissing</c> instead of dividing by a guess.
        /// </remarks>
        private static int WriteSizeModel(LabBallSizeSetter setter, PhysicsTestFieldDriver driver,
            Report report)
        {
            // The driver and the setter both link the motor; either is a fine source, and the
            // driver's is preferred because that is the one the run reads.
            KatamariBallMotor motor = ReadMotorLink(driver);
            if (motor == null) motor = ReadMotorLink(setter);
            if (motor == null)
            {
                report.Note("neither the driver nor the size console links a motor: the size model " +
                            "was not baked, so size_accuracy reports reason=sizeModelMissing");
                return 0;
            }

            var motorSerialized = new SerializedObject(motor);
            SerializedProperty startDiameter = motorSerialized.FindProperty("startDiameterWorldM");
            SerializedProperty startVolume = motorSerialized.FindProperty("startVolumeMm3");
            long startVolumeMm3 = 0L;
            float diameter = startDiameter == null ? 0f : startDiameter.floatValue;
            if (diameter > 0f)
            {
                if (!SizeModelMath.TrySphereVolumeMm3(diameter, 1f, out startVolumeMm3))
                {
                    startVolumeMm3 = 0L;
                }
            }
            else if (startVolume != null)
            {
                startVolumeMm3 = startVolume.longValue;
            }
            if (startVolumeMm3 <= 0L)
            {
                report.Note("cannot read a positive start volume (startDiameterWorldM / " +
                            "startVolumeMm3) from '" + motor.name + "': the size model was not " +
                            "baked, so size_accuracy reports reason=sizeModelMissing");
                return 0;
            }

            var serialized = new SerializedObject(setter);
            SerializedProperty target = RequireProperty(serialized, "sizeModelStartVolumeMm3",
                report);
            if (target == null) return 0;
            target.longValue = startVolumeMm3;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(setter);
            report.Note("size model: startVolumeMm3=" + startVolumeMm3 +
                        " (read from the scene motor" +
                        (diameter > 0f ? " startDiameterWorldM=" + diameter.ToString("0.####") : "") +
                        "); re-import after changing the motor's start size or the global cap");
            return 1;
        }

        /// <summary>The component's serialized <c>motor</c> link, or null when it has none.</summary>
        private static KatamariBallMotor ReadMotorLink(Object component)
        {
            if (component == null) return null;
            SerializedProperty property = new SerializedObject(component).FindProperty("motor");
            if (property == null) return null;
            return property.objectReferenceValue as KatamariBallMotor;
        }

        private static bool CopyProxies(List<PhysicsTestCell> cells, PhysicsTestFieldDriver driver,
            Report report)
        {
            LabBallSizeSetter setter = Object.FindObjectOfType<LabBallSizeSetter>();
            if (cells.Count == 0 && driver == null && setter == null) return false;
            try
            {
                // One compile for the whole bake: CompileAllCsPrograms is the expensive step and
                // copying before it finishes would push stale bytecode into the backing behaviour.
                UdonSharpProgramAsset.CompileAllCsPrograms(false, false);
                for (int i = 0; i < cells.Count; i++)
                {
                    UdonSharpEditorUtility.CopyProxyToUdon(cells[i]);
                }

                if (driver != null) UdonSharpEditorUtility.CopyProxyToUdon(driver);
                if (setter != null) UdonSharpEditorUtility.CopyProxyToUdon(setter);
                return true;
            }
            catch (Exception exception)
            {
                report.Error("CopyProxyToUdon threw (" + exception.Message + "). The fields are set " +
                             "on the proxy but not on the backing UdonBehaviour; import again once " +
                             "the UdonSharp compile has finished");
                return false;
            }
        }

        // ---- scene lookups ------------------------------------------------------

        /// <summary>
        /// Finds the component for a cell: first by its serialized cellId, then by the object's name.
        /// </summary>
        /// <remarks>
        /// The name fallback exists because the id is what this tool writes: requiring the operator to
        /// type it before the first bake would be a chicken-and-egg step for every new cell. Naming the
        /// object after the cell is something they do anyway (<c>Cells/C14</c>).
        /// </remarks>
        private static PhysicsTestCell FindCell(string cellId)
        {
            PhysicsTestCell[] found = Object.FindObjectsOfType<PhysicsTestCell>();
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] == null) continue;
                if (string.Equals(found[i].GetCellId(), cellId, StringComparison.Ordinal))
                {
                    return found[i];
                }
            }

            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] == null) continue;
                if (string.Equals(found[i].gameObject.name, cellId, StringComparison.Ordinal))
                {
                    return found[i];
                }
            }

            return null;
        }

        private static LevelArenaHost FindArena(string cellId)
        {
            GameObject root = FindRoot(ManagedRootName);
            if (root == null) return null;
            Transform cell = root.transform.Find(cellId);
            if (cell == null) return null;
            return cell.GetComponent<LevelArenaHost>();
        }

        private static GameObject FindRoot(string name)
        {
            GameObject[] roots = EditorSceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, name, StringComparison.Ordinal)) return roots[i];
            }

            return null;
        }

        /// <summary>
        /// Distance from the start, along the authored heading, to the first static box the ball
        /// would meet, plus that box's world XZ footprint (its centre and half extents).
        /// </summary>
        /// <remarks>
        /// Read from the ARENA's own <c>staticBoxes</c> (what the motor bakes), not from the JSON,
        /// so these numbers describe the geometry that will actually be solid. The footprint is the
        /// deck the v2 lift criterion needs (design CONTACT_MODEL_V2 7.7.4): the sampling gate is
        /// this rect grown by one collision radius, so neither the plate's position nor its size is
        /// retyped in the criteria file or the driver.
        /// </remarks>
        private static float ComputeApproach(LevelArenaHost arena, CellEntry cell,
            out Vector2 deckCenter, out Vector2 deckHalf)
        {
            deckCenter = Vector2.zero;
            deckHalf = Vector2.zero;
            if (arena == null || cell.start_pose == null) return 0f;
            float[] start = cell.start_pose.center;
            if (start == null || start.Length < 3) return 0f;

            float heading = cell.start_pose.heading_deg * Mathf.Deg2Rad;
            // heading 0 = +Z, 90 = +X (the field's own convention, same as the motor's forward).
            float dirX = Mathf.Sin(heading);
            float dirZ = Mathf.Cos(heading);
            if (Mathf.Abs(dirX) < 1e-5f && Mathf.Abs(dirZ) < 1e-5f) return 0f;

            var serialized = new SerializedObject(arena);
            SerializedProperty boxes = serialized.FindProperty("staticBoxes");
            if (boxes == null || !boxes.isArray) return 0f;

            float best = -1f;
            Bounds deck = new Bounds();
            for (int i = 0; i < boxes.arraySize; i++)
            {
                BoxCollider box = boxes.GetArrayElementAtIndex(i).objectReferenceValue as BoxCollider;
                if (box == null) continue;
                Bounds bounds = box.bounds;
                float distance;
                if (!RayEnters(start[0], start[2], dirX, dirZ, bounds.min.x, bounds.max.x,
                    bounds.min.z, bounds.max.z, out distance))
                {
                    continue;
                }

                if (best < 0f || distance < best)
                {
                    best = distance;
                    deck = bounds;
                }
            }

            if (best < 0f) return 0f;

            // The deck rect is the box's world AABB in XZ. Its top is what nominal_m already is.
            deckCenter = new Vector2((deck.min.x + deck.max.x) * 0.5f,
                (deck.min.z + deck.max.z) * 0.5f);
            deckHalf = new Vector2((deck.max.x - deck.min.x) * 0.5f,
                (deck.max.z - deck.min.z) * 0.5f);
            return best;
        }

        /// <summary>
        /// Reads the parts of the cell's placement the runner needs that are NOT a second copy of
        /// the geometry: whether a ramp is the climb, where its footprint starts, and whether a
        /// <c>kind=ramp_probe</c> board has to be put in front of the motor's slope guard. The
        /// geometry itself is read from the ARENA's colliders, exactly like
        /// <see cref="ComputeApproach"/>: the placement names the structure, the scene owns the
        /// transform the motor will bake.
        /// </summary>
        private static CellGeometry ReadCellGeometry(string fieldPath, LevelArenaHost arena,
            CellEntry cell, Report report)
        {
            var geometry = new CellGeometry();
            PlacementDocument placement = LoadPlacement(fieldPath, cell, report);
            if (placement == null || placement.structures == null) return geometry;

            string probeId = null;
            for (int i = 0; i < placement.structures.Length; i++)
            {
                PlacementStructureEntry structure = placement.structures[i];
                if (structure == null) continue;
                if (string.Equals(structure.role, "ramp", StringComparison.Ordinal))
                {
                    geometry.HasRamp = true;
                }

                if (string.Equals(structure.kind, "ramp_probe", StringComparison.Ordinal))
                {
                    probeId = structure.id;
                }
            }

            if (geometry.HasRamp) geometry.RampFootM = ComputeRampFoot(arena, cell);
            if (!string.IsNullOrEmpty(probeId))
            {
                BakeRampProbe(arena, probeId, geometry, report);
            }

            return geometry;
        }

        private static PlacementDocument LoadPlacement(string fieldPath, CellEntry cell, Report report)
        {
            if (string.IsNullOrEmpty(cell.placement))
            {
                report.Note(cell.id + " has no placement path; its ramp data was not read");
                return null;
            }

            string repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fieldPath) ?? ".",
                "..", "..", ".."));
            string path = Path.Combine(repoRoot, "Docs", "Design", "Levels", cell.placement);
            if (!File.Exists(path))
            {
                report.Note(cell.id + ": placement not found at " + path + "; ramp data was not read");
                return null;
            }

            try
            {
                return JsonUtility.FromJson<PlacementDocument>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                report.Note(cell.id + ": cannot read " + path + " (" + exception.Message + ")");
                return null;
            }
        }

        /// <summary>Distance from the start, along the heading, to the first ramp footprint.</summary>
        private static float ComputeRampFoot(LevelArenaHost arena, CellEntry cell)
        {
            if (arena == null || cell.start_pose == null) return 0f;
            float[] start = cell.start_pose.center;
            if (start == null || start.Length < 3) return 0f;

            float heading = cell.start_pose.heading_deg * Mathf.Deg2Rad;
            float dirX = Mathf.Sin(heading);
            float dirZ = Mathf.Cos(heading);
            if (Mathf.Abs(dirX) < 1e-5f && Mathf.Abs(dirZ) < 1e-5f) return 0f;

            var serialized = new SerializedObject(arena);
            SerializedProperty ramps = serialized.FindProperty("rampBoxes");
            if (ramps == null || !ramps.isArray) return 0f;

            float best = -1f;
            for (int i = 0; i < ramps.arraySize; i++)
            {
                BoxCollider box = ramps.GetArrayElementAtIndex(i).objectReferenceValue as BoxCollider;
                if (box == null) continue;
                Bounds bounds = box.bounds;
                float distance;
                if (!RayEnters(start[0], start[2], dirX, dirZ, bounds.min.x, bounds.max.x,
                    bounds.min.z, bounds.max.z, out distance))
                {
                    continue;
                }

                if (best < 0f || distance < best) best = distance;
            }

            return best < 0f ? 0f : best;
        }

        /// <summary>
        /// Bakes the probe board's +Y face into the plane the driver hands to
        /// <c>motor.AddRamp</c>. The bake uses a permissive ceiling on purpose: the point is to
        /// obtain the over-limit plane unchanged and let the MOTOR refuse it at run time, so the
        /// refusal getters carry the reason. (LevelArenaHost's own bake would refuse it here, in
        /// the editor, and the motor would never see it.)
        /// </summary>
        private static void BakeRampProbe(LevelArenaHost arena, string probeId, CellGeometry geometry,
            Report report)
        {
            BoxCollider box = FindArenaBox(arena, "staticBoxes", probeId);
            if (box == null)
            {
                report.Note("ramp probe '" + probeId + "' has no BoxCollider in the arena's " +
                            "staticBoxes; the refusal check will read 0");
                return;
            }

            Transform owner = box.transform;
            Vector3 normal = owner.rotation * new Vector3(0f, 1f, 0f);
            Vector3 right = owner.rotation * new Vector3(1f, 0f, 0f);
            Vector3 scale = owner.lossyScale;
            Vector3 localCenter = box.center;
            Vector3 localSize = box.size;
            Vector3 boxCenter = owner.position + owner.rotation * new Vector3(
                localCenter.x * scale.x, localCenter.y * scale.y, localCenter.z * scale.z);
            Vector3 worldSize = new Vector3(Mathf.Abs(scale.x) * localSize.x,
                Mathf.Abs(scale.y) * localSize.y, Mathf.Abs(scale.z) * localSize.z);

            Vector3 walkingCenter;
            Vector3 rampNormal;
            Vector3 rampRight;
            float halfWidth;
            float halfRun;
            float slopeDegrees;
            int refusalCode;
            if (!SupportSurfaceMath.TryBakeRamp(boxCenter, normal, right, worldSize, 89.5f,
                out walkingCenter, out rampNormal, out rampRight, out halfWidth, out halfRun,
                out slopeDegrees, out refusalCode))
            {
                report.Note("ramp probe '" + probeId + "' could not be baked: "
                            + SupportSurfaceMath.RampRefusalReason(refusalCode));
                return;
            }

            geometry.HasProbe = true;
            geometry.ProbeCenter = walkingCenter;
            geometry.ProbeNormal = rampNormal;
            geometry.ProbeRight = rampRight;
            geometry.ProbeHalfWidth = halfWidth;
            geometry.ProbeHalfRun = halfRun;
            report.Note("ramp probe '" + probeId + "' baked at " + slopeDegrees.ToString("0.0") +
                        " deg; the motor must refuse it at run time");
        }

        private static BoxCollider FindArenaBox(LevelArenaHost arena, string fieldName, string objectName)
        {
            if (arena == null) return null;
            var serialized = new SerializedObject(arena);
            SerializedProperty boxes = serialized.FindProperty(fieldName);
            if (boxes == null || !boxes.isArray) return null;
            for (int i = 0; i < boxes.arraySize; i++)
            {
                BoxCollider box = boxes.GetArrayElementAtIndex(i).objectReferenceValue as BoxCollider;
                if (box == null) continue;
                if (string.Equals(box.gameObject.name, objectName, StringComparison.Ordinal)) return box;
            }

            return null;
        }

        /// <summary>2D slab test: distance along (dirX, dirZ) from (x, z) to the box, or false.</summary>
        private static bool RayEnters(float x, float z, float dirX, float dirZ, float minX, float maxX,
            float minZ, float maxZ, out float distance)
        {
            distance = 0f;
            float enter = float.NegativeInfinity;
            float exit = float.PositiveInfinity;

            if (!Slab(x, dirX, minX, maxX, ref enter, ref exit)) return false;
            if (!Slab(z, dirZ, minZ, maxZ, ref enter, ref exit)) return false;
            if (exit < 0f || enter > exit) return false;
            distance = enter > 0f ? enter : 0f;
            return true;
        }

        private static bool Slab(float origin, float direction, float minimum, float maximum,
            ref float enter, ref float exit)
        {
            if (Mathf.Abs(direction) < 1e-6f)
            {
                return origin >= minimum && origin <= maximum;
            }

            float t1 = (minimum - origin) / direction;
            float t2 = (maximum - origin) / direction;
            if (t1 > t2)
            {
                float swap = t1;
                t1 = t2;
                t2 = swap;
            }

            if (t1 > enter) enter = t1;
            if (t2 < exit) exit = t2;
            return true;
        }

        // ---- conversions --------------------------------------------------------

        private static int OpCode(string op)
        {
            if (op == "abs<=") return 0;
            if (op == "<=") return 1;
            if (op == ">=") return 2;
            if (op == "==") return 3;
            if (op == "abs>=") return 4;
            if (op == "record") return 5;
            return 0;
        }

        /// <summary>
        /// One bit per preset diameter the cell is NOT driven at (<c>run_at_d</c> false). Absent
        /// or empty = every diameter runs, so data without the key bakes to 0.
        /// </summary>
        private static void SetSkipDiameterMask(SerializedObject serialized, CellEntry cell,
            Report report)
        {
            int mask = 0;
            if (cell.run_at_d != null && cell.run_at_d.Length > 0)
            {
                if (cell.run_at_d.Length != 4)
                {
                    report.Note(cell.id + " has " + cell.run_at_d.Length + " run_at_d entries; the " +
                                "field has 4 preset diameters");
                }

                for (int i = 0; i < cell.run_at_d.Length && i < 4; i++)
                {
                    if (!cell.run_at_d[i]) mask |= 1 << i;
                }
            }

            SerializedProperty property = RequireProperty(serialized, "skipDiameterMask", report);
            if (property != null) property.intValue = mask;
        }

        private static int ReadoutCode(string readout)
        {
            if (readout == "trace") return 1;
            if (readout == "counter") return 2;
            return 0;
        }

        private static int GateCode(string gate)
        {
            if (gate == "P1") return 1;
            if (gate == "P2") return 2;
            if (gate == "P3") return 3;
            return 0;
        }

        /// <summary>Two bits per preset diameter, in <c>diameters[]</c> order: 0 n/a, 1 pass, 2 fail.</summary>
        /// <summary>
        /// One bit per preset diameter: does this criterion's scenario exist at that diameter?
        /// </summary>
        /// <remarks>
        /// The data carries an explicit <c>applies_d</c> row (design section 4.1.4) so the rule and
        /// its numbers are reviewable in one place; this tool only encodes it. Missing = applicable
        /// everywhere (bit set), which keeps old data from turning checks into silent skips.
        /// </remarks>
        private static int AppliesMask(CellEntry cell, CheckEntry check, Report report)
        {
            int all = 15;
            if (check.applies_d == null || check.applies_d.Length == 0) return all;
            if (check.applies_d.Length != 4)
            {
                report.Note(cell.id + " check '" + check.id + "' has " + check.applies_d.Length +
                            " applies_d entries; the field has 4 preset diameters");
            }

            int mask = 0;
            for (int i = 0; i < check.applies_d.Length && i < 4; i++)
            {
                if (check.applies_d[i]) mask |= 1 << i;
            }

            // A row that excludes every diameter would silently retire the criterion; that is a
            // data mistake, not a decision this tool takes.
            if (mask == 0)
            {
                report.Error(cell.id + " check '" + check.id + "' is not applicable at any " +
                             "diameter; remove the check instead of excluding all four");
            }

            return mask;
        }

        private static int ExpectMask(CellEntry cell, CheckEntry check, Report report)
        {
            if (check.expect == null || check.expect.Length == 0) return 0;
            int mask = 0;
            for (int i = 0; i < check.expect.Length && i < 15; i++)
            {
                int bits = 0;
                if (check.expect[i] == "pass") bits = 1;
                else if (check.expect[i] == "fail") bits = 2;
                mask |= bits << (i * 2);
            }

            if (check.expect.Length != 4)
            {
                report.Note(cell.id + " check '" + check.id + "' has " + check.expect.Length +
                            " expect entries; the field has 4 preset diameters");
            }

            return mask;
        }

        // ---- the v2 override row (design PHYSICS_TEST_FIELD section 5.6) ---------

        /// <summary>
        /// True when a deserialized <c>v2</c> entry actually carries an override; false for the
        /// all-default entry Unity invents for a check whose JSON has no <c>v2</c> key.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>v2 != null</c> is NOT the presence test, and using it as one is the pass10 defect
        /// (2026-11-15): JsonUtility fills a nested <c>[Serializable]</c> class field with a
        /// default instance even when the key is absent (a plain class field has no null), so
        /// every check read back non-null and the bake tagged all 59 checks as v2 rows when the
        /// file has 11 blocks. The null test is indistinguishable from "the file has no v2 row"
        /// by construction, so the presence test must be content: a real block always sets at
        /// least one of the documented <c>criteria_models.override_keys</c> - <c>expect</c>,
        /// <c>metric</c>, <c>op</c>, <c>threshold</c> or <c>not_applicable</c>.
        /// </para>
        /// <para>
        /// A block that carries only <c>note</c> (or only <c>limit</c> without <c>op</c>, which
        /// the encoder would drop anyway) means "inherit the base row", exactly like no block, so
        /// it is not an override and must not be counted. This is the ONE implementation of the
        /// test: <c>WriteChecks</c> bakes <c>checkHasV2[]</c> with it and
        /// <c>CriteriaModelOverrideTests</c> calls it for the real file. The JSON guard
        /// (<c>Docs/tests/levelbuild/test_physics_test_field_cells.py</c>) owns the file's
        /// vocabulary and its closed 11-block count.
        /// </para>
        /// </remarks>
        public static bool HasV2Override(V2Entry v2)
        {
            if (v2 == null) return false;
            if (v2.not_applicable) return true;
            if (!string.IsNullOrEmpty(v2.metric)) return true;
            if (!string.IsNullOrEmpty(v2.op)) return true;
            if (!string.IsNullOrEmpty(v2.threshold)) return true;
            return v2.expect != null && v2.expect.Length > 0;
        }

        /// <summary>
        /// The override expect mask, or -1 for "inherit the base row".
        /// </summary>
        /// <remarks>
        /// A <c>not_applicable</c> override becomes 0 - every 2-bit field is 0, i.e. n/a at all
        /// four diameters - which is exactly what the driver already reports as
        /// <c>SKIPPED_N_A</c>. It must not also carry a pass/fail expectation: the two would
        /// disagree and whichever the encoder saw last would silently win.
        /// </remarks>
        public static int V2ExpectMask(CellEntry cell, CheckEntry check, Report report)
        {
            if (check == null || check.v2 == null) return -1;
            string where = Where(cell, check) + " v2";
            if (check.v2.not_applicable)
            {
                if (check.v2.expect != null)
                {
                    for (int i = 0; i < check.v2.expect.Length; i++)
                    {
                        if (check.v2.expect[i] == "pass" || check.v2.expect[i] == "fail")
                        {
                            report.Error(where + ".not_applicable is set but expect[" + i +
                                         "]=" + check.v2.expect[i] + "; keep one of the two");
                            break;
                        }
                    }
                }

                return 0;
            }

            if (check.v2.expect == null || check.v2.expect.Length == 0) return -1;
            if (check.v2.expect.Length != 4)
            {
                report.Note(where + " has " + check.v2.expect.Length +
                            " expect entries; the field has 4 preset diameters");
            }

            int mask = 0;
            for (int i = 0; i < check.v2.expect.Length && i < 15; i++)
            {
                int bits = 0;
                if (check.v2.expect[i] == "pass") bits = 1;
                else if (check.v2.expect[i] == "fail") bits = 2;
                else if (check.v2.expect[i] != "n/a")
                {
                    report.Error(where + ".expect[" + i + "]='" + check.v2.expect[i] +
                                 "' is not pass / fail / n/a");
                }

                mask |= bits << (i * 2);
            }

            return mask;
        }

        /// <summary>The override op code, or -1 for "inherit the base op AND its limit".</summary>
        public static int V2OpCode(CellEntry cell, CheckEntry check, Report report)
        {
            if (check == null || check.v2 == null || string.IsNullOrEmpty(check.v2.op)) return -1;
            if (check.v2.op == "abs<=") return 0;
            if (check.v2.op == "<=") return 1;
            if (check.v2.op == ">=") return 2;
            if (check.v2.op == "==") return 3;
            if (check.v2.op == "abs>=") return 4;
            report.Error(Where(cell, check) + " v2.op='" + check.v2.op +
                         "' is not one of abs<= / <= / >= / == / abs>=");
            return -1;
        }

        /// <summary>
        /// The v2 threshold code: 0 = the baked limit, 1 = <c>climb_cap_bound</c> (derived by the
        /// driver from ContactTuning at run time), -1 = a name this build does not know.
        /// </summary>
        public static int V2ThresholdCode(CellEntry cell, CheckEntry check, Report report)
        {
            if (check == null || check.v2 == null) return 0;
            if (string.IsNullOrEmpty(check.v2.threshold)) return 0;
            if (check.v2.threshold == "climb_cap_bound") return 1;
            report.Error(Where(cell, check) + " v2.threshold='" + check.v2.threshold +
                         "' is not a known symbolic threshold (climb_cap_bound)");
            return -1;
        }

        private static string Where(CellEntry cell, CheckEntry check)
        {
            string cellId = cell == null || cell.id == null ? "?" : cell.id;
            string checkId = check == null || check.id == null ? "?" : check.id;
            return cellId + " check '" + checkId + "'";
        }

        private static Vector3 ToVector3(float[] values, Vector3 fallback)
        {
            if (values == null || values.Length < 3) return fallback;
            return new Vector3(values[0], values[1], values[2]);
        }

        private static Vector2 ToVector2(float[] values, Vector2 fallback)
        {
            if (values == null || values.Length < 2) return fallback;
            return new Vector2(values[0], values[1]);
        }

        // ---- SerializedObject helpers ------------------------------------------

        private static SerializedProperty RequireProperty(SerializedObject serialized, string name,
            Report report)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null)
            {
                report.Error(serialized.targetObject.GetType().Name + " has no serialized field '" +
                             name + "'; the component and this tool disagree about the schema");
            }

            return property;
        }

        private static bool SetString(SerializedObject serialized, string name, string value, Report report)
        {
            SerializedProperty property = RequireProperty(serialized, name, report);
            if (property == null) return false;
            property.stringValue = value;
            return true;
        }

        private static void SetFloat(SerializedObject serialized, string name, float value, Report report)
        {
            SerializedProperty property = RequireProperty(serialized, name, report);
            if (property != null) property.floatValue = value;
        }

        private static void SetBool(SerializedObject serialized, string name, bool value, Report report)
        {
            SerializedProperty property = RequireProperty(serialized, name, report);
            if (property != null) property.boolValue = value;
        }

        private static void SetVector3(SerializedObject serialized, string name, Vector3 value,
            Report report)
        {
            SerializedProperty property = RequireProperty(serialized, name, report);
            if (property != null) property.vector3Value = value;
        }

        private static void SetVector2(SerializedObject serialized, string name, Vector2 value,
            Report report)
        {
            SerializedProperty property = RequireProperty(serialized, name, report);
            if (property != null) property.vector2Value = value;
        }

        private static void SetObject(SerializedObject serialized, string name, Object value, Report report)
        {
            SerializedProperty property = RequireProperty(serialized, name, report);
            if (property != null) property.objectReferenceValue = value;
        }

        private static string ResolveFieldPath()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string directory = projectRoot;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(directory); i++)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, "..", FieldJsonRepoRelative));
                if (File.Exists(candidate)) return candidate;

                candidate = Path.GetFullPath(Path.Combine(directory, FieldJsonRepoRelative));
                if (File.Exists(candidate)) return candidate;

                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }
    }
}
