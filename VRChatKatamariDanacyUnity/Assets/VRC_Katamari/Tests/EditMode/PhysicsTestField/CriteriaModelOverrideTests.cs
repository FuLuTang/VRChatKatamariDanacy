using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the criteria importer's v2 override encoding and the cell fields it writes into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SAME 36-run matrix has to judge both motor models (design
    /// <c>PHYSICS_TEST_FIELD.md</c> section 5.6): a criterion may carry an optional <c>v2</c>
    /// block that the driver applies only when the motor reports <c>IsContactModelV2()</c>, and
    /// the v1 row stays the criterion otherwise. The JSON side is checked in
    /// <c>Docs/tests/levelbuild/test_physics_test_field_cells.py</c>; what only EditMode can
    /// check is the C# half - the cell really has the six arrays the importer writes through
    /// <c>SerializedObject</c>, and the encoder turns the JSON's v2 block into the masks the
    /// driver reads back.
    /// </para>
    /// <para>
    /// These calls go through reflection on purpose, exactly like
    /// <see cref="PlacementImporterCurveRampTests"/>: the importer lives in
    /// <c>Assets/VRC_Katamari/Editor/PhysicsTestField/</c> and this assembly references only
    /// <c>VRCKatamari.Runtime</c>, so it cannot name editor types. No scene is created and
    /// nothing is saved.
    /// </para>
    /// <para>
    /// Mask arithmetic (the cell's encoding): two bits per preset diameter in preset order,
    /// 0 = n/a, 1 = pass, 2 = fail. So all-pass is 0b01010101 = 85, all-n/a is 0, and
    /// <c>-1</c> means "inherit the v1 row".
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
            Assert.AreEqual("2026-11-15e", revision.GetRawConstantValue(),
                "the cell's build stamp must match the driver's (both 2026-11-15e)");
        }

        // ---- the real criteria file, encoded -----------------------------------

        [Test]
        public void EveryV2RowInTheCriteriaFileEncodesAsTheDriverExpects()
        {
            object doc = LoadFieldDocument();
            Type docType = doc.GetType();
            Array cells = (Array)Field(docType, doc, "cells");
            Assert.AreEqual(9, cells.Length, "the field has 9 wired cells");

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
                    if (v2 == null) continue;
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
                        where + ": both models' answers are all-pass here, got mask " + mask);
                    if (threshold == 1)
                    {
                        Assert.AreEqual(1, opCode,
                            where + ": climb_cap_bound is an upper bound, so op must be <=");
                    }
                }
            }

            // 4 ramp_registered rows + C06 x3 + C09/C10 on_step + C10 blocked_gap + C14 lift.
            Assert.AreEqual(11, rows, "the v2 table changed; update this test with it");
        }

        [Test]
        public void TheDesignRowsThatFlipAreEncodedAsWritten()
        {
            // Every row CONTACT_MODEL_V2 7.7.2/6.1 flips or replaces, checked end to end:
            // JSON -> encoder -> the mask/op/threshold the driver will read back.
            AssertRow("C01", "ramp_registered", AllPass, 2, 0);      // >= over static_obb_count
            AssertRow("C03", "ramp_registered", AllPass, 2, 0);
            AssertRow("C05", "ramp_registered", AllPass, 2, 0);
            AssertRow("C13", "ramp_registered", AllPass, 2, 0);
            AssertRow("C06", "no_ramp_registered", 0, -1, 0);        // not_applicable
            AssertRow("C06", "no_climb", AllPass, 1, 1);             // <= climb_cap_bound
            AssertRow("C06", "stopped_by_wall", AllPass, 1, 0);      // <= 0
            AssertRow("C09", "on_step", AllPass, -1, 0);             // expect flip only
            AssertRow("C10", "on_step", AllPass, -1, 0);
            AssertRow("C10", "blocked_gap", AllPass, 1, 0);          // <= 0
            AssertRow("C14", "lift_bounded", AllPass, 1, 0);          // gated lift metric
        }

        [Test]
        public void TheC14LiftRowNamesTheGatedMetric()
        {
            object check = FindCheck("C14", "lift_bounded");
            object v2 = Field(check.GetType(), check, "v2");
            Assert.AreEqual("lift_over_nominal_in_deck_m",
                (string)Field(v2.GetType(), v2, "metric"),
                "the v2 lift is only the plate's, so it must name the gated metric " +
                "(CONTACT_MODEL_V2 7.7.4)");
            object cell = FindCell("C14");
            float nominal = (float)Field(cell.GetType(), cell, "nominal_m");
            Assert.Less(Math.Abs(nominal - 0.028f), 1e-6f,
                "nominal stays the plate top: the gate changes WHERE lift is sampled, not what");
        }

        // ---- the encoder's own rules -------------------------------------------

        [Test]
        public void AV2BlockWithoutExpectInheritsTheV1Row()
        {
            object check = NewCheck("only_metric");
            object v2 = NewV2();
            Set(v2, "metric", "static_obb_count");
            Set(check, "v2", v2);
            object report = NewReport();
            Assert.AreEqual(-1, (int)Invoke("V2ExpectMask", NewCell("C01"), check, report),
                "a v2 block that only replaces the metric must keep the v1 expectation");
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
            Assert.IsNotNull(Field(check.GetType(), check, "v2"),
                cellId + "/" + checkId + " has no v2 override");
            Assert.AreEqual(expectMask, (int)Invoke("V2ExpectMask", cell, check, report),
                cellId + "/" + checkId + " expect mask");
            Assert.AreEqual(opCode, (int)Invoke("V2OpCode", cell, check, report),
                cellId + "/" + checkId + " op code");
            Assert.AreEqual(thresholdCode, (int)Invoke("V2ThresholdCode", cell, check, report),
                cellId + "/" + checkId + " threshold code");
            Assert.AreEqual(0, Errors(report).Count, cellId + "/" + checkId);
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
    }
}
