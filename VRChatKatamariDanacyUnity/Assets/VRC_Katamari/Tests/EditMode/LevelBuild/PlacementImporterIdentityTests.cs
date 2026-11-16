using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the importer's container and identity rules: a group container lives under a
    /// reserved parent so a group id can never resolve to a prop, a prop is never used as a
    /// container, and a prop's identity is its itemId under the managed props root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect these pin (F2/F3, reference level run): <c>EnsureFolder</c> resolved a
    /// container by NAME, so a prop whose <c>itemId</c> equalled another prop's <c>groupId</c>
    /// was found and the group's children were parented inside it. The pickup sync's row rule
    /// is "the topmost Renderer is one prop", so 8 of 648 props never became rows (640 rows,
    /// the declared value total failed read-back) and every re-import re-created them
    /// (<c>+8</c> instead of <c>+0 ~0 -0</c>).
    /// </para>
    /// <para>
    /// Reflection, like the other importer tests: <c>PlacementImporter</c> lives in
    /// <c>Assets/VRC_Katamari/Editor/LevelBuild/</c> and this assembly references only
    /// <c>VRCKatamari.Runtime</c>. The document/report types are created through
    /// <c>Activator</c> for the same reason.
    /// </para>
    /// <para>
    /// NOT COVERED HERE: a full <c>Import(levelId)</c> against a real placement document and
    /// real prop models (that needs a scene plus the asset database). The two decisions that
    /// made the second import <c>+8</c> are covered directly instead - the container lookup
    /// and the identity index - plus the row-count verdict the importer now fails on. The
    /// <c>+0 ~0 -0</c> acceptance gate itself is the operator's Unity run.
    /// </para>
    /// </remarks>
    public class PlacementImporterIdentityTests
    {
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";
        private const string ReportTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImportReport";

        // An existing model in the repo, used only to get real prefab instances (a prop is a
        // prefab instance; a plain GameObject would not exercise the nested-prefab state).
        private const string PropModelPath = "Assets/VRC_Katamari/Art/Props/acorn/acorn.fbx";

        private Type importerType;
        private Type reportType;
        private GameObject propsRoot;
        private readonly List<GameObject> spawned = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            importerType = FindType(ImporterTypeName);
            reportType = FindType(ReportTypeName);
            Assert.IsNotNull(importerType, ImporterTypeName + " type not loaded");
            Assert.IsNotNull(reportType, ReportTypeName + " type not loaded");

            propsRoot = Track(new GameObject("Props"));
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = spawned.Count - 1; i >= 0; i--)
            {
                if (spawned[i] != null) UnityEngine.Object.DestroyImmediate(spawned[i]);
            }

            spawned.Clear();
            propsRoot = null;
        }

        [Test]
        public void AGroupIdThatEqualsAnotherPropsIdGetsItsOwnContainer()
        {
            object report = NewReport();
            Transform kindFolder = (Transform)CallImporter("EnsureFolder", propsRoot.transform,
                "food", report);
            Assert.IsNotNull(kindFolder, "the kind folder is a normal container");

            // The reference level's shape: the group of one prop is another prop's itemId.
            GameObject containerProp = Track(NewPropWithRenderer("caramel_018", kindFolder));

            Transform folder = (Transform)CallImporter("EnsurePropFolder", propsRoot.transform,
                "food", null, "caramel_018", report);

            Assert.IsNotNull(folder, "the group gets a container");
            Assert.AreNotSame(containerProp.transform, folder,
                "the group container must not be the prop that owns the same name");
            Assert.AreEqual("caramel_018", folder.name, "the container keeps the readable name");
            Assert.AreEqual("Groups", folder.parent.name,
                "it lives under the reserved parent, where no prop can be");
            Assert.AreEqual("food", folder.parent.parent.name);
            Assert.AreEqual(0, containerProp.transform.childCount,
                "nothing was re-parented into the prop");
            CollectionAssert.IsEmpty(Errors(report), "no collision was reported, because there is none");
        }

        [Test]
        public void ASecondLookupReturnsTheSameContainerAndCreatesNothing()
        {
            object report = NewReport();
            Transform kindFolder = (Transform)CallImporter("EnsureFolder", propsRoot.transform,
                "food", report);
            Assert.IsNotNull(kindFolder);
            Track(NewPropWithRenderer("caramel_018", kindFolder));

            Transform first = (Transform)CallImporter("EnsurePropFolder", propsRoot.transform,
                "food", null, "caramel_018", report);
            int childCountAfterFirst = CountDescendants(propsRoot.transform);

            Transform second = (Transform)CallImporter("EnsurePropFolder", propsRoot.transform,
                "food", null, "caramel_018", report);

            Assert.AreSame(first, second, "the container's identity is stable across imports");
            Assert.AreEqual(childCountAfterFirst, CountDescendants(propsRoot.transform),
                "a second import creates no container, so it cannot create a duplicate prop either");
        }

        [Test]
        public void AZoneNameThatEqualsAPropIdIsAnErrorNotAPropContainer()
        {
            object report = NewReport();
            Transform kindFolder = (Transform)CallImporter("EnsureFolder", propsRoot.transform,
                "food", report);
            GameObject zoneNamedProp = Track(NewPropWithRenderer("dining", kindFolder));

            Transform folder = (Transform)CallImporter("EnsurePropFolder", propsRoot.transform,
                "food", "dining", null, report);

            Assert.IsNull(folder, "a prop cannot be a container, so the folder is refused");
            Assert.AreEqual(1, Errors(report).Count, "the collision is reported, not swallowed");
            StringAssert.Contains("is a prop", Errors(report)[0]);
            StringAssert.Contains("dining", Errors(report)[0]);
            Assert.AreEqual(0, zoneNamedProp.transform.childCount, "nothing was re-parented into it");

            // A refusal is stable: the next run refuses too, and still creates nothing.
            object secondReport = NewReport();
            Assert.IsNull(CallImporter("EnsurePropFolder", propsRoot.transform, "food", "dining",
                    null, secondReport));
            Assert.AreEqual(1, Errors(secondReport).Count);
        }

        [Test]
        public void ANestedPropIsFoundByItsIdSoItIsUpdatedInsteadOfRecreated()
        {
            // The pre-fix scene state: a real prop parented inside another prop (Unity models
            // it as a nested prefab). The identity walk must still find it, which is what turns
            // the F3 "+8 every import" into "+0 after one repair".
            GameObject model;
            if (!TryLoadPropModel(out model))
            {
                Assert.Ignore("fixture model unusable: " + PropModelPath +
                              " (missing, or its root has no Renderer)");
            }

            object report = NewReport();
            Transform groupFolder = (Transform)CallImporter("EnsurePropFolder", propsRoot.transform,
                "food", null, "caramel_018", report);
            Assert.IsNotNull(groupFolder);

            GameObject parentProp = Track(PrefabUtility.InstantiatePrefab(model, groupFolder) as GameObject);
            Assert.IsNotNull(parentProp);
            parentProp.name = "caramel_018";

            GameObject nestedProp = Track(PrefabUtility.InstantiatePrefab(model,
                parentProp.transform) as GameObject);
            Assert.IsNotNull(nestedProp);
            nestedProp.name = "caramel_019";
            Assert.AreEqual(parentProp, nestedProp.transform.parent.gameObject,
                "the fixture is the broken state: one prop owns another");

            var wanted = new HashSet<string>(StringComparer.Ordinal) { "caramel_018", "caramel_019" };
            var index = (Dictionary<string, GameObject>)CallImporter("IndexProps",
                propsRoot.transform, wanted);

            Assert.AreEqual(2, index.Count, "both ids are found, so neither is 'missing'");
            Assert.AreSame(parentProp, index["caramel_018"]);
            Assert.AreSame(nestedProp, index["caramel_019"],
                "the nested prop is the instance the reconcile will move, not a new one");
        }

        [Test]
        public void APropThatOwnsAPropFailsTheImportRowCheck()
        {
            // The verdict the importer now makes before it claims a run: the sync would see one
            // row where the document declares two, so the run must fail with the id named.
            object report = NewReport();
            Transform kindFolder = (Transform)CallImporter("EnsureFolder", propsRoot.transform,
                "food", report);
            GameObject parentProp = Track(NewPropWithRenderer("caramel_018", kindFolder));
            GameObject nestedProp = Track(NewPropWithRenderer("caramel_019", parentProp.transform));

            object doc = NewDocument("caramel_018", "caramel_019");
            bool ok = (bool)CallImporter("VerifyPropRows", doc, propsRoot.transform, report);

            Assert.IsFalse(ok, "a prop that owns a prop loses a row and must fail the run");
            Assert.AreEqual(1, Errors(report).Count);
            StringAssert.Contains("caramel_019", Errors(report)[0]);

            // Reparenting the nested prop to its own folder is the repair; the same verdict
            // then passes, which is what makes the next import clean.
            object secondReport = NewReport();
            nestedProp.transform.SetParent(kindFolder, true);
            Assert.IsTrue((bool)CallImporter("VerifyPropRows", doc, propsRoot.transform, secondReport));
            CollectionAssert.IsEmpty(Errors(secondReport));
        }

        [Test]
        public void AnUndeclaredTopLevelPropIsStillIndexedSoRemovalKeepsWorking()
        {
            // The identity walk must NOT filter the top level: an object the document no
            // longer declares is exactly what the reconcile's stale sweep has to find and
            // delete. Only the walk INSIDE a prop is filtered by the declaration set.
            object report = NewReport();
            Transform kindFolder = (Transform)CallImporter("EnsureFolder", propsRoot.transform,
                "food", report);
            GameObject stale = Track(NewPropWithRenderer("stale_prop", kindFolder));

            var wanted = new HashSet<string>(StringComparer.Ordinal) { "caramel_019" };
            var index = (Dictionary<string, GameObject>)CallImporter("IndexProps",
                propsRoot.transform, wanted);

            Assert.IsTrue(index.ContainsKey("stale_prop"),
                "an undeclared object is still found, so a second import can delete it");
            Assert.AreSame(stale, index["stale_prop"]);
        }

        [Test]
        public void ASecondReconcileOfTheCollidingGroupIsZero()
        {
            // The end-to-end idempotency gate, at the reconcile level: the group of prop 2
            // equals prop 1's itemId (F2's collision), both run through the real ApplyProps -
            // instantiate from the repo model, place, re-index, compare, delete stale. The
            // first run creates 2, the second must be +0 ~0 -0 (F3's acceptance number).
            GameObject model;
            if (!TryLoadPropModel(out model))
            {
                Assert.Ignore("fixture model unusable: " + PropModelPath +
                              " (missing, or its root has no Renderer)");
            }

            object doc = NewPropsDocument(new[] { "caramel_018", "caramel_019" },
                new[] { null, "caramel_018" });

            object first = NewReport();
            Assert.IsTrue((bool)CallImporter("ApplyProps", doc, propsRoot.transform, first),
                "the first run imports both props");
            Assert.AreEqual(2, ReportInt(first, "PropsCreated"));
            Assert.IsTrue((bool)CallImporter("VerifyPropRows", doc, propsRoot.transform, first),
                "both props are topmost rows: the group container is a folder, not a prop");

            // The shape the fix promises: the prop named like the group stays a prop, and the
            // group's children live in a folder under the reserved parent.
            Transform propFolder = propsRoot.transform.Find("food");
            Assert.IsNotNull(propFolder);
            Transform nameCollision = propFolder.Find("caramel_018");
            Assert.IsNotNull(nameCollision);
            Assert.IsNotNull(nameCollision.GetComponent<Renderer>(),
                "the object that owns the group's name is still the PROP");
            Transform groupFolder = propFolder.Find("Groups/caramel_018");
            Assert.IsNotNull(groupFolder, "the group container lives under Groups/");
            Assert.IsNull(groupFolder.GetComponent<Renderer>(), "a container is never a prop");
            Assert.AreEqual(1, groupFolder.childCount);
            Assert.AreEqual("caramel_019", groupFolder.GetChild(0).name);

            object second = NewReport();
            Assert.IsTrue((bool)CallImporter("ApplyProps", doc, propsRoot.transform, second),
                "the second run succeeds");
            Assert.AreEqual(0, ReportInt(second, "PropsCreated"), "second import: +0");
            Assert.AreEqual(0, ReportInt(second, "PropsUpdated"), "second import: ~0");
            Assert.AreEqual(0, ReportInt(second, "PropsDeleted"), "second import: -0");
            Assert.AreEqual(2, ReportInt(second, "PropsSkipped"));
        }

        // ---- fixtures ----------------------------------------------------------

        /// <summary>
        /// The fixture model, when it can stand in for an imported prop: present, and with a
        /// Renderer on its root (the importer refuses a model whose root has none, so a test
        /// that ignored that would be testing a shape the tool never produces).
        /// </summary>
        private static bool TryLoadPropModel(out GameObject model)
        {
            model = AssetDatabase.LoadAssetAtPath<GameObject>(PropModelPath);
            if (model == null) return false;
            return model.GetComponent<Renderer>() != null;
        }

        private GameObject Track(GameObject created)
        {
            if (created != null) spawned.Add(created);
            return created;
        }

        private static GameObject NewPropWithRenderer(string name, Transform parent)
        {
            var prop = new GameObject(name);
            prop.AddComponent<MeshRenderer>();
            prop.transform.SetParent(parent, false);
            return prop;
        }

        private static int CountDescendants(Transform root)
        {
            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                count = count + 1 + CountDescendants(root.GetChild(i));
            }

            return count;
        }

        private object NewReport()
        {
            return Activator.CreateInstance(reportType);
        }

        private List<string> Errors(object report)
        {
            FieldInfo fieldInfo = reportType.GetField("Errors",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(fieldInfo, "Errors field not found on the report");
            return (List<string>)fieldInfo.GetValue(report);
        }

        /// <summary>
        /// A minimal document with only the ids <see cref="VerifyPropRows"/> reads. The types
        /// are nested in the importer and live in the editor assembly, hence reflection.
        /// </summary>
        private object NewDocument(params string[] itemIds)
        {
            Type docType = importerType.GetNestedType("PlacementDocument",
                BindingFlags.Public | BindingFlags.NonPublic);
            Type propType = importerType.GetNestedType("PropEntry",
                BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(docType, "PlacementDocument not found");
            Assert.IsNotNull(propType, "PropEntry not found");

            object doc = Activator.CreateInstance(docType);
            Array props = Array.CreateInstance(propType, itemIds.Length);
            FieldInfo itemIdField = propType.GetField("itemId", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(itemIdField, "PropEntry.itemId not found");
            for (int i = 0; i < itemIds.Length; i++)
            {
                object prop = Activator.CreateInstance(propType);
                itemIdField.SetValue(prop, itemIds[i]);
                props.SetValue(prop, i);
            }

            FieldInfo propsField = docType.GetField("props", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(propsField, "PlacementDocument.props not found");
            propsField.SetValue(doc, props);
            return doc;
        }

        /// <summary>
        /// A document the real <c>ApplyProps</c> can run with: one def (the repo's acorn model)
        /// and one prop per id, optionally grouped. Used by the end-to-end two-run test, which
        /// is the only place the reconcile's instantiate/re-index/pose comparison runs for real.
        /// </summary>
        private object NewPropsDocument(string[] itemIds, string[] groups)
        {
            Type docType = importerType.GetNestedType("PlacementDocument",
                BindingFlags.Public | BindingFlags.NonPublic);
            Type propType = importerType.GetNestedType("PropEntry",
                BindingFlags.Public | BindingFlags.NonPublic);
            Type defType = importerType.GetNestedType("DefEntry",
                BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(docType, "PlacementDocument not found");
            Assert.IsNotNull(propType, "PropEntry not found");
            Assert.IsNotNull(defType, "DefEntry not found");

            object doc = Activator.CreateInstance(docType);

            Array props = Array.CreateInstance(propType, itemIds.Length);
            for (int i = 0; i < itemIds.Length; i++)
            {
                object prop = Activator.CreateInstance(propType);
                SetMember(propType, prop, "itemId", itemIds[i]);
                SetMember(propType, prop, "def", "acorn");
                SetMember(propType, prop, "kind", "food");
                SetMember(propType, prop, "group", groups == null ? null : groups[i]);
                SetMember(propType, prop, "position", new[] { i * 0.5f, 0f, 0f });
                SetMember(propType, prop, "rotationQuat", new float[] { 0f, 0f, 0f, 1f });
                props.SetValue(prop, i);
            }

            SetMember(docType, doc, "props", props);

            Array defs = Array.CreateInstance(defType, 1);
            object def = Activator.CreateInstance(defType);
            SetMember(defType, def, "id", "acorn");
            SetMember(defType, def, "kind", "food");
            SetMember(defType, def, "valueUnits", 1L);   // deprecated input, still accepted
            defs.SetValue(def, 0);
            SetMember(docType, doc, "defs", defs);
            return doc;
        }

        private static void SetMember(Type type, object target, string name, object value)
        {
            FieldInfo fieldInfo = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(fieldInfo, type.Name + "." + name + " not found");
            fieldInfo.SetValue(target, value);
        }

        private static int ReportInt(object report, string name)
        {
            FieldInfo fieldInfo = report.GetType().GetField(name, BindingFlags.Instance |
                BindingFlags.Public);
            Assert.IsNotNull(fieldInfo, name + " not found on the report");
            return (int)fieldInfo.GetValue(report);
        }

        private object CallImporter(string name, params object[] args)
        {
            MethodInfo method = null;
            MethodInfo[] candidates = importerType.GetMethods(BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Name != name) continue;
                if (candidates[i].GetParameters().Length != args.Length) continue;
                method = candidates[i];
                break;
            }

            Assert.IsNotNull(method, name + " with " + args.Length + " parameters not found");
            return method.Invoke(null, args);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found = assembly.GetType(fullName);
                if (found != null) return found;
            }

            return null;
        }
    }
}
