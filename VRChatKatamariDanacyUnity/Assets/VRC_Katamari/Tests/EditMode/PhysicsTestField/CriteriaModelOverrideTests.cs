using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the criteria importer's override encoding, the cell fields it writes into, and
    /// the bake that must tag exactly the file's one override row (C06 no_climb), never all 90.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A criterion may carry an optional <c>v2</c> override block that replaces its base row
    /// (design <c>PHYSICS_TEST_FIELD.md</c> section 5.6); today only C06 no_climb has one. The JSON side is checked in
    /// <c>Docs/tests/levelbuild/test_physics_test_field_cells.py</c>; what only EditMode can
    /// check is the C# half - the cell really has the six arrays the importer writes through
    /// <c>SerializedObject</c>, and the encoder turns the JSON's v2 block into the masks the
    /// driver reads back.
    /// </para>
    /// <para>
    /// These calls go through reflection on purpose, exactly like
    /// <see cref="PlacementImporterCurveRampTests"/>: the importer lives in
    /// <c>Assets/VRC_Katamari/Editor/PhysicsTestField/</c> and this assembly references only
    /// <c>VRCKatamari.Runtime</c>, so it cannot name editor types. No scene file is created
    /// and nothing is saved; the bake test adds one transient cell object and destroys it.
    /// </para>
    /// <para>
    /// Mask arithmetic (the cell's encoding): two bits per preset diameter in preset order,
    /// 0 = n/a, 1 = pass, 2 = fail. So all-pass is 0b01010101 = 85, all-n/a is 0, and
    /// <c>-1</c> means "inherit the base row".
    /// </para>
    /// </remarks>
    public class CriteriaModelOverrideTests
    {
        private const string ImporterTypeName =
            "VRCKatamari.Editor.PhysicsTestField.PhysicsTestFieldCriteriaImporter";

        private const string CellTypeName = "VRCKatamari.Labs.PhysicsTestField.PhysicsTestCell";

        private const string FieldJsonRepoRelative = "Docs/Design/BallMotor/physics_test_field.json";

        private const int AllPass = 85;      // 1 | 1<<2 | 1<<4 | 1<<6

        // ---- the cell has the arrays the importer writes ------------------------

        [Test]
        public void TheCellSerializesTheV2RowAndTheDeckFootprint()
        {
            // The importer writes these through SerializedObject, so a missing field is a silent
            // "no override" - the worst possible failure (both models would look identical).
            // The type is resolved by name because the Labs code is NOT in VRCKatamari.Runtime:
            // this assembly cannot name it, exactly like the importer itself.
            Type cellType = CellType();
            string[] fields =
            {
                "checkHasV2", "checkV2Metrics", "checkV2Ops", "checkV2Limits",
                "checkV2ExpectMasks", "checkV2Thresholds", "deckCenterXZ", "deckHalfSizeM",
            };

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = cellType.GetField(fields[i],
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, CellTypeName + "." + fields[i] + " is missing");
                Assert.IsTrue(field.IsDefined(typeof(SerializeField), false),
                    CellTypeName + "." + fields[i] + " must be [SerializeField] or the importer " +
                    "cannot write it");
            }

            Assert.IsNotNull(cellType.GetMethod("GetCellRevision",
                    BindingFlags.Instance | BindingFlags.Public),
                CellTypeName + ".GetCellRevision is missing");
            Assert.IsNotNull(cellType.GetMethod("IsInDeckGate",
                    BindingFlags.Instance | BindingFlags.Public),
                CellTypeName + ".IsInDeckGate is missing (the v2 lift gate)");
            FieldInfo revision = cellType.GetField("CellRevision",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(revision, CellTypeName + ".CellRevision is missing");
            Assert.AreEqual("2026-11-17a", revision.GetRawConstantValue(),
                "the cell's build stamp must match the driver's (both 2026-11-17a)");
            Assert.IsNotNull(cellType.GetField("skipDiameterMask",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                CellTypeName + ".skipDiameterMask is missing (the per-diameter run mask)");
        }

        // ---- the real criteria file, encoded -----------------------------------

        [Test]
        public void EveryV2RowInTheCriteriaFileEncodesAsTheDriverExpects()
        {
            object doc = LoadFieldDocument();
            Type docType = doc.GetType();
            Array cells = (Array)Field(docType, doc, "cells");
            Assert.AreEqual(13, cells.Length, "the field has 13 wired cells");

            int rows = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                object cell = cells.GetValue(i);
                if (cell == null) continue;
                string cellId = (string)Field(cell.GetType(), cell, "id");
                Array checks = (Array)Field(cell.GetType(), cell, "checks");
                if (checks == null) continue;
                for (int c = 0; c < checks.Length; c++)
                {
                    object check = checks.GetValue(c);
                    object v2 = Field(check.GetType(), check, "v2");
                    if (!IsV2Override(v2)) continue;
                    rows++;
                    string checkId = (string)Field(check.GetType(), check, "id");
                    string where = cellId + "/" + checkId;

                    object report = NewReport();
                    int mask = (int)Invoke("V2ExpectMask", cell, check, report);
                    int opCode = (int)Invoke("V2OpCode", cell, check, report);
                    int threshold = (int)Invoke("V2ThresholdCode", cell, check, report);
                    Assert.AreEqual(0, Errors(report).Count, where + ": " + Errors(report));

                    bool notApplicable = (bool)Field(v2.GetType(), v2, "not_applicable");
                    if (notApplicable)
                    {
                        Assert.AreEqual(0, mask, where + " must be n/a at all four diameters");
                        continue;
                    }

                    Assert.IsTrue(mask == AllPass || mask == -1,
                        where + ": an override row is all-pass or inherits, got mask " + mask);
                    if (threshold == 1)
                    {
                        Assert.AreEqual(1, opCode,
                            where + ": climb_cap_bound is an upper bound, so op must be <=");
                    }
                }
            }

            // The file's own count is assertable now that the SAME predicate the importer bakes
            // with is what this test counts (see IsV2Override). It is the number the JSON guard
            // pins too: Docs/tests/levelbuild/test_physics_test_field_cells.py counts the one
            // real "v2" key in the file. If the importer ever regresses to `v2 != null` again,
            // TheImporterBakesExactlyTheFilesOverrideRows fails before this one does.
            const int DesignOverrideRowCount = 1;
            Assert.AreEqual(DesignOverrideRowCount, rows,
                "the criteria file's override rows changed: only C06 no_climb (the symbolic " +
                "climb_cap_bound threshold) carries one. The exact count is pinned by " +
                "Docs/tests/levelbuild/test_physics_test_field_cells.py");
        }

        /// <summary>
        /// The bake path itself, not just the predicate: the pass10 defect was at the call site
        /// (<c>bool hasOverride = check.v2 != null;</c>), so counting what the importer writes
        /// into <c>checkHasV2[]</c> is the only assertion that can see it.
        /// </summary>
        /// <remarks>
        /// A transient physics cell is added by <see cref="Type"/> only to give <c>WriteChecks</c>
        /// a <see cref="SerializedObject"/>; the arrays are read back from that unapplied copy,
        /// the host is destroyed again and nothing is saved.
        /// </remarks>
        [Test]
        public void TheImporterBakesExactlyTheFilesOverrideRows()
        {
            object doc = LoadFieldDocument();
            Array cells = (Array)Field(doc.GetType(), doc, "cells");
            Assert.AreEqual(13, cells.Length, "the field has 13 wired cells");

            var baked = new Dictionary<string, int>();
            int total = 0;
            int v2Rows = 0;
            var host = new GameObject("CriteriaV2RowProbe");
            try
            {
                Component component = host.AddComponent(CellType());
                Assert.IsNotNull(component, "AddComponent(" + CellTypeName + ") returned null");

                for (int i = 0; i < cells.Length; i++)
                {
                    object cell = cells.GetValue(i);
                    if (cell == null) continue;
                    // Mirror Import(): only a wired cell is baked, so only it is counted.
                    if (!(bool)Field(cell.GetType(), cell, "wired")) continue;
                    string cellId = (string)Field(cell.GetType(), cell, "id");
                    Array checks = (Array)Field(cell.GetType(), cell, "checks");
                    if (checks == null) continue;

                    var serialized = new SerializedObject(component);
                    object report = NewReport();
                    Assert.IsTrue((bool)InvokePrivate("WriteChecks", serialized, cell, report),
                        cellId + ": the importer refused to encode its checks");
                    Assert.AreEqual(0, Errors(report).Count, cellId + ": " + Errors(report));
                    SerializedProperty hasV2 = serialized.FindProperty("checkHasV2");
                    Assert.IsNotNull(hasV2, CellTypeName + ".checkHasV2 is missing");
                    Assert.AreEqual(checks.Length, hasV2.arraySize,
                        cellId + ": checkHasV2 must be one row per check");

                    for (int c = 0; c < checks.Length; c++)
                    {
                        object check = checks.GetValue(c);
                        string where = cellId + "/" + (string)Field(check.GetType(), check, "id");
                        int flag = hasV2.GetArrayElementAtIndex(c).intValue;
                        bool expected = IsV2Override(Field(check.GetType(), check, "v2"));
                        Assert.AreEqual(expected ? 1 : 0, flag, where +
                            ": the baked v2 flag must come from HasV2Override, not v2 != null " +
                            "(Unity invents a default V2Entry for every check)");
                        baked[where] = flag;
                        total++;
                        if (flag != 0) v2Rows++;
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }

            Assert.AreEqual(90, total,
                "the criteria file's wired check count moved; update this test");
            Assert.AreEqual(1, v2Rows, "the importer must bake the file's one override row; 90 " +
                "means every default V2Entry was tagged (the pass10 regression)");
            Assert.Less(v2Rows, total, "override rows sit on top of base rows: all rows tagged is the bug");

            // Rows without an override block must stay base rows: the driver derives the
            // ramp-entry threshold for the climb rows, and a wrongly tagged row would skip it.
            string[] baseRows =
            {
                "C08/on_step", "C09/on_step", "C10/on_step", "C14/lift_bounded",
                "C01/climbed", "C01/ramp_entered", "C01/ramp_registered",
                "C03/climbed", "C03/ramp_entered",
                "C05/climbed", "C05/ramp_entered",
                "C13/climbed", "C13/ramp_entered",
                "C25/climb_active_s", "C27/slides_after_rest",
            };
            for (int i = 0; i < baseRows.Length; i++)
            {
                Assert.IsTrue(baked.ContainsKey(baseRows[i]), baseRows[i] + " is not in the file");
                Assert.AreEqual(0, baked[baseRows[i]], baseRows[i] + " must stay a base row");
            }

            Assert.AreEqual(1, baked["C06/no_climb"], "C06/no_climb carries the climb_cap_bound " +
                "threshold override");
        }

        /// <summary>
        /// Delegates to the importer's one presence test; never reimplement it here.
        /// </summary>
        /// <remarks>
        /// <c>v2 != null</c> is NOT the test. JsonUtility deserializes a nested serializable
        /// class into an instance even when the JSON key is absent (Unity's serializer has no
        /// null for a class field without <c>[SerializeReference]</c>), so every check reads
        /// back with a non-null all-default <c>V2Entry</c>: the round-M run counted every "row"
        /// where the file has few blocks. The importer's <c>HasV2Override</c> is the content
        /// test (expect / metric / op / threshold / not_applicable); this wrapper is only the
        /// reflection plumbing that keeps this file honest about calling it.
        /// </remarks>
        private static bool IsV2Override(object v2)
        {
            return (bool)Invoke("HasV2Override", v2);
        }

        [Test]
        public void TheSymbolicClimbBoundIsEncodedAsWritten()
        {
            // The one override row: JSON -> encoder -> the mask/op/threshold the driver reads.
            // The block carries only the threshold and op, so the expectation is inherited (-1).
            AssertRow("C06", "no_climb", -1, 1, 1);                  // <= climb_cap_bound
        }

        [Test]
        public void TheFoldedRowsAreJudgedAsWritten()
        {
            // These rows used to be overrides; their values now live in the base row.
            object lift = FindCheck("C14", "lift_bounded");
            Assert.AreEqual("lift_over_nominal_in_deck_m", (string)Field(lift.GetType(), lift,
                "metric"), "the plate lift is only the plate's, so it names the gated metric");
            object cell = FindCell("C14");
            float nominal = (float)Field(cell.GetType(), cell, "nominal_m");
            Assert.Less(Math.Abs(nominal - 0.028f), 1e-6f,
                "nominal stays the plate top: the gate changes WHERE lift is sampled, not what");

            foreach (string id in new[] { "C01", "C03", "C05", "C13" })
            {
                object row = FindCheck(id, "ramp_registered");
                Assert.AreEqual("static_obb_count", (string)Field(row.GetType(), row, "metric"), id);
            }

            Assert.IsFalse(HasCheck("C06", "no_ramp_registered"),
                "the dead v1 row is gone");
        }

        // ---- the encoder's own rules -------------------------------------------

        [Test]
        public void AnOverrideWithoutExpectInheritsTheBaseRow()
        {
            object check = NewCheck("only_metric");
            object v2 = NewV2();
            Set(v2, "metric", "static_obb_count");
            Set(check, "v2", v2);
            object report = NewReport();
            Assert.AreEqual(-1, (int)Invoke("V2ExpectMask", NewCell("C01"), check, report),
                "an override that only replaces the metric must keep the base expectation");
            Assert.AreEqual(0, Errors(report).Count);
        }

        [Test]
        public void TheV2ExpectMaskPacksFourTiersInPresetOrder()
        {
            object check = NewCheck("mixed");
            object v2 = NewV2();
            Set(v2, "expect", new[] { "pass", "fail", "n/a", "pass" });
            Set(check, "v2", v2);
            object report = NewReport();
            // pass=1, fail=2, n/a=0, pass=1 -> 1 | 2<<2 | 0<<4 | 1<<6 = 1 + 8 + 64 = 73.
            Assert.AreEqual(73, (int)Invoke("V2ExpectMask", NewCell("C09"), check, report));
            Assert.AreEqual(0, Errors(report).Count);
        }

        [Test]
        public void NotApplicableIsAllNaAndAContradictionIsRefused()
        {
            object check = NewCheck("contradiction");
            object v2 = NewV2();
            Set(v2, "not_applicable", true);
            Set(check, "v2", v2);
            object report = NewReport();
            Assert.AreEqual(0, (int)Invoke("V2ExpectMask", NewCell("C06"), check, report));
            Assert.AreEqual(0, Errors(report).Count, "not_applicable alone is legitimate");

            Set(v2, "expect", new[] { "pass", "pass", "pass", "pass" });
            object contradiction = NewReport();
            Assert.AreEqual(0, (int)Invoke("V2ExpectMask", NewCell("C06"), check, contradiction),
                "not_applicable wins the encoding...");
            Assert.AreEqual(1, Errors(contradiction).Count,
                "...but the import must be refused, not silently re-interpreted");
        }

        [Test]
        public void TheOpAndThresholdVocabulariesAreClosed()
        {
            object cell = NewCell("C10");
            object check = NewCheck("vocab");
            object v2 = NewV2();
            Set(check, "v2", v2);

            Set(v2, "op", "<=");
            object ok = NewReport();
            Assert.AreEqual(1, (int)Invoke("V2OpCode", cell, check, ok));
            Assert.AreEqual(0, Errors(ok).Count);

            Set(v2, "op", "=<");
            object badOp = NewReport();
            Assert.AreEqual(-1, (int)Invoke("V2OpCode", cell, check, badOp));
            Assert.AreEqual(1, Errors(badOp).Count, "a typo'd op must fail the import");

            Set(v2, "op", null);
            Set(v2, "threshold", "climb_cap_bound");
            object threshold = NewReport();
            Assert.AreEqual(1, (int)Invoke("V2ThresholdCode", cell, check, threshold));
            Assert.AreEqual(0, Errors(threshold).Count);

            Set(v2, "threshold", "half_climb_cap");
            object badThreshold = NewReport();
            Assert.AreEqual(-1, (int)Invoke("V2ThresholdCode", cell, check, badThreshold));
            Assert.AreEqual(1, Errors(badThreshold).Count,
                "only climb_cap_bound is derived; a made-up threshold must not pass as a limit");
        }

        // ---- criteria-file lookups ---------------------------------------------

        private static void AssertRow(string cellId, string checkId, int expectMask, int opCode,
            int thresholdCode)
        {
            object cell = FindCell(cellId);
            object check = FindCheck(cellId, checkId);
            object report = NewReport();
            // Not just "v2 != null": JsonUtility hands out a default entry for every check
            // (see IsV2Override), so the row must be proved to carry the override itself.
            Assert.IsTrue(IsV2Override(Field(check.GetType(), check, "v2")),
                cellId + "/" + checkId + " has no v2 override");
            Assert.AreEqual(expectMask, (int)Invoke("V2ExpectMask", cell, check, report),
                cellId + "/" + checkId + " expect mask");
            Assert.AreEqual(opCode, (int)Invoke("V2OpCode", cell, check, report),
                cellId + "/" + checkId + " op code");
            Assert.AreEqual(thresholdCode, (int)Invoke("V2ThresholdCode", cell, check, report),
                cellId + "/" + checkId + " threshold code");
            Assert.AreEqual(0, Errors(report).Count, cellId + "/" + checkId);
        }

        private static bool HasCheck(string cellId, string checkId)
        {
            object cell = FindCell(cellId);
            Array checks = (Array)Field(cell.GetType(), cell, "checks");
            for (int i = 0; i < checks.Length; i++)
            {
                object check = checks.GetValue(i);
                if (check != null && (string)Field(check.GetType(), check, "id") == checkId)
                {
                    return true;
                }
            }

            return false;
        }

        private static object FindCell(string cellId)
        {
            object doc = LoadFieldDocument();
            Array cells = (Array)Field(doc.GetType(), doc, "cells");
            for (int i = 0; i < cells.Length; i++)
            {
                object cell = cells.GetValue(i);
                if (cell != null && (string)Field(cell.GetType(), cell, "id") == cellId) return cell;
            }

            Assert.Fail("cell " + cellId + " is not in the criteria file");
            return null;
        }

        private static object FindCheck(string cellId, string checkId)
        {
            object cell = FindCell(cellId);
            Array checks = (Array)Field(cell.GetType(), cell, "checks");
            for (int i = 0; i < checks.Length; i++)
            {
                object check = checks.GetValue(i);
                if (check != null && (string)Field(check.GetType(), check, "id") == checkId)
                {
                    return check;
                }
            }

            Assert.Fail("check " + checkId + " is not in " + cellId);
            return null;
        }

        private static object LoadFieldDocument()
        {
            string path = ResolveFieldPath();
            Assert.IsNotNull(path, FieldJsonRepoRelative + " not found above the Unity project");
            MethodInfo fromJson = typeof(JsonUtility).GetMethod("FromJson",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(fromJson, "JsonUtility.FromJson<T>(string) is missing");
            Type fieldType = ImporterType().GetNestedType("FieldDocument", BindingFlags.Public);
            Assert.IsNotNull(fieldType, ImporterTypeName + ".FieldDocument is missing");
            object doc = fromJson.MakeGenericMethod(fieldType)
                .Invoke(null, new object[] { File.ReadAllText(path) });
            Assert.IsNotNull(doc, "cannot parse " + path);
            return doc;
        }

        private static string ResolveFieldPath()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ??
                                 Directory.GetCurrentDirectory();
            string directory = projectRoot;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(directory); i++)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, "..",
                    FieldJsonRepoRelative));
                if (File.Exists(candidate)) return candidate;
                candidate = Path.GetFullPath(Path.Combine(directory, FieldJsonRepoRelative));
                if (File.Exists(candidate)) return candidate;
                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }

        // ---- DTO construction (same shapes the importer deserializes) ----------

        private static object NewCell(string cellId)
        {
            object cell = Activator.CreateInstance(ImporterType().GetNestedType("CellEntry",
                BindingFlags.Public));
            Set(cell, "id", cellId);
            return cell;
        }

        private static object NewCheck(string checkId)
        {
            object check = Activator.CreateInstance(ImporterType().GetNestedType("CheckEntry",
                BindingFlags.Public));
            Set(check, "id", checkId);
            Set(check, "metric", "deck_gap_m");
            Set(check, "op", "abs<=");
            Set(check, "limit", 0.005f);
            return check;
        }

        private static object NewV2()
        {
            return Activator.CreateInstance(ImporterType().GetNestedType("V2Entry",
                BindingFlags.Public));
        }

        private static object NewReport()
        {
            Type reportType = ImporterType().GetNestedType("Report", BindingFlags.Public);
            Assert.IsNotNull(reportType, ImporterTypeName + ".Report must be public for this test");
            return Activator.CreateInstance(reportType);
        }

        private static IList Errors(object report)
        {
            Assert.IsNotNull(report, "no report");
            FieldInfo errors = report.GetType().GetField("Errors");
            Assert.IsNotNull(errors, "Report.Errors is missing");
            return (IList)errors.GetValue(report);
        }

        private static object Field(Type type, object instance, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(field, type.Name + "." + name + " is missing");
            return field.GetValue(instance);
        }

        private static void Set(object instance, string name, object value)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(field, instance.GetType().Name + "." + name + " is missing");
            field.SetValue(instance, value);
        }

        private static Type CellType()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type found = assemblies[i].GetType(CellTypeName, false);
                if (found != null) return found;
            }

            Assert.Fail("type '" + CellTypeName + "' is not loaded. It lives in " +
                        "Assets/VRC_Katamari/Labs/PhysicsTestField and compiles with the scene " +
                        "assembly.");
            return null;
        }

        private static Type ImporterType()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type found = assemblies[i].GetType(ImporterTypeName, false);
                if (found != null) return found;
            }

            Assert.Fail("type '" + ImporterTypeName + "' is not loaded. It lives in " +
                        "Assets/VRC_Katamari/Editor/PhysicsTestField and is expected to compile " +
                        "into the editor assembly.");
            return null;
        }

        private static object Invoke(string methodName, params object[] arguments)
        {
            MethodInfo method = ImporterType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, ImporterTypeName + "." + methodName + " is missing");
            return method.Invoke(null, arguments);
        }

        /// <summary>
        /// The bake entry point: <c>WriteChecks</c> is private because only <c>Import</c> calls
        /// it, but it is the function that decides <c>checkHasV2[]</c>, so the test drives it
        /// directly instead of copying its body.
        /// </summary>
        private static object InvokePrivate(string methodName, params object[] arguments)
        {
            MethodInfo method = ImporterType().GetMethod(methodName,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, ImporterTypeName + "." + methodName + " is missing");
            return method.Invoke(null, arguments);
        }
    }
}
