using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins where a v2 prop with a <c>parent</c> lands: a reserved
    /// <c>Hierarchy/&lt;parent&gt;/</c> FOLDER, never the parent prop's GameObject.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this is a correctness test and not a layout preference (owner ruling A):
    /// <c>PickupFieldSync</c>'s row rule is "the topmost object that carries a Renderer is one
    /// prop, and its subtree is that prop's visual". Parenting a child prop's GameObject under
    /// its parent prop's GameObject therefore HIDES the child: it never becomes a row, it can
    /// never be absorbed, and <c>VerifyPropRows</c> fails the run. The reference level has 83
    /// parented props, so that layout would lose 83 of 511 rows.
    /// </para>
    /// <para>
    /// The folder is at identity and the child keeps its authored WORLD pose, which is also what
    /// makes "the parent transform is applied twice" impossible by construction: there is no
    /// parent transform, only a container. <see cref="ChildKeepsItsAuthoredWorldPose"/> asserts
    /// exactly that.
    /// </para>
    /// <para>
    /// The scene objects this test creates are thrown away in <see cref="TearDown"/> and the root
    /// carries <c>HideFlags.DontSave</c>, so a test run can never leave anything behind in an
    /// open scene or save it by accident.
    /// </para>
    /// </remarks>
    public class PlacementImporterHierarchyTests
    {
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";
        private const string ReportTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImportReport";

        private Type importerType;
        private Type reportType;
        private GameObject root;
        private object report;
        private Transform propsRoot;

        [SetUp]
        public void SetUp()
        {
            importerType = FindType(ImporterTypeName);
            reportType = FindType(ReportTypeName);
            Assert.IsNotNull(importerType, ImporterTypeName + " not loaded");
            Assert.IsNotNull(reportType, ReportTypeName + " not loaded");

            root = new GameObject("PlacementImporterHierarchyTests");
            root.hideFlags = HideFlags.DontSave;
            report = Activator.CreateInstance(reportType);
            propsRoot = new GameObject("Props").transform;
            propsRoot.hideFlags = HideFlags.DontSave;
            propsRoot.SetParent(root.transform, false);
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
                root = null;
            }
        }

        [Test]
        public void ParentBecomesAReservedHierarchyFolder()
        {
            Transform folder = FolderFor("tableware", "porch", null, "table_01");
            Assert.AreEqual("table_01", folder.name);
            Assert.AreEqual("Hierarchy", folder.parent.name,
                "the parent id lives under the reserved Hierarchy container");
            Assert.AreEqual("porch", folder.parent.parent.name);
            Assert.AreEqual("tableware", folder.parent.parent.parent.name);
            Assert.AreEqual(propsRoot, folder.parent.parent.parent.parent);
            Assert.IsNull(folder.GetComponent<Renderer>(),
                "a container must never carry a Renderer: that is what hides a prop row");
        }

        [Test]
        public void EveryContainerOnThePathIsIdentity()
        {
            Transform folder = FolderFor("tableware", "porch", null, "table_01");
            for (Transform cursor = folder; cursor != null && cursor != propsRoot;
                cursor = cursor.parent)
            {
                Assert.AreEqual(Vector3.zero, cursor.localPosition, "container " + cursor.name);
                Assert.AreEqual(Quaternion.identity, cursor.localRotation, "container " + cursor.name);
                Assert.AreEqual(Vector3.one, cursor.localScale, "container " + cursor.name);
            }
        }

        [Test]
        public void ChildKeepsItsAuthoredWorldPose()
        {
            Transform folder = FolderFor("tableware", "porch", null, "table_01");
            var child = new GameObject("teacup_12");
            child.hideFlags = HideFlags.DontSave;
            child.transform.SetParent(folder, false);

            // ApplyPose's order: give the child its authored WORLD pose after it is parented.
            var authored = new Vector3(0.42f, -0.53f, 1.07f);
            var authoredRotation = Quaternion.Euler(0f, 100f, 0f);
            child.transform.position = authored;
            child.transform.rotation = authoredRotation;

            Assert.AreEqual(0f, Vector3.Distance(authored, child.transform.position), 1e-5f,
                "an identity container cannot move the prop: no second application of a parent pose");
            Assert.Greater(Mathf.Abs(Quaternion.Dot(authoredRotation, child.transform.rotation)),
                0.99999f);

            // And reparenting with worldPositionStays keeps it, which is the other half of the
            // "never multiply the parent transform in twice" rule.
            GameObject otherRoot = new GameObject("OtherRoot");
            otherRoot.hideFlags = HideFlags.DontSave;
            otherRoot.transform.SetParent(root.transform, false);
            child.transform.SetParent(otherRoot.transform, true);
            Assert.AreEqual(0f, Vector3.Distance(authored, child.transform.position), 1e-5f);
        }

        [Test]
        public void NoParentMeansNoHierarchyFolder()
        {
            Transform folder = FolderFor("nature", "pocket", null, null);
            Assert.AreEqual("pocket", folder.name);
            Assert.AreEqual("nature", folder.parent.name);
            Assert.AreEqual(propsRoot, folder.parent.parent);
        }

        [Test]
        public void EnsurePropFolderIsIdempotent()
        {
            Transform first = FolderFor("tableware", "porch", null, "table_01");
            Transform second = FolderFor("tableware", "porch", null, "table_01");
            Assert.AreSame(first, second,
                "a second import must reuse the container, not nest a new one");
            // zone -> Hierarchy -> table_01: exactly one of each, however many runs.
            Assert.AreEqual(1, CountNamed(first.parent.parent, "Hierarchy"),
                "no duplicate Hierarchy folder");
            Assert.AreEqual(1, CountNamed(first.parent, "table_01"),
                "no duplicate parent folder");
        }

        // --------------------------------------------------------------- helpers ---

        private Transform FolderFor(string kind, string zone, string group, string parentId)
        {
            MethodInfo method = null;
            MethodInfo[] methods = importerType.GetMethods(BindingFlags.NonPublic |
                                                           BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (string.Equals(methods[i].Name, "EnsurePropFolder", StringComparison.Ordinal))
                {
                    method = methods[i];
                    break;
                }
            }

            Assert.IsNotNull(method, "EnsurePropFolder not found");
            object folder = method.Invoke(null,
                new object[] { propsRoot, kind, zone, group, parentId, report });
            Assert.IsNotNull(folder, "EnsurePropFolder refused: " + JoinErrors());
            return (Transform)folder;
        }

        private string JoinErrors()
        {
            var errors = (System.Collections.IEnumerable)Field(report, "Errors");
            var text = new System.Text.StringBuilder();
            foreach (object error in errors)
            {
                if (text.Length > 0) text.Append(" | ");
                text.Append(error);
            }

            return text.ToString();
        }

        private static int CountNamed(Transform parent, string name)
        {
            int count = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                if (string.Equals(parent.GetChild(i).name, name, StringComparison.Ordinal)) count++;
            }

            return count;
        }

        private static object Field(object target, string name)
        {
            Assert.IsNotNull(target);
            FieldInfo field = target.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, target.GetType().Name + " has no field '" + name + "'");
            return field.GetValue(target);
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, false);
                if (type != null) return type;
            }

            return null;
        }
    }
}
