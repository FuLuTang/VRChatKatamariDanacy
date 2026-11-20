using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins where a v2 prop with a <c>parent</c> lands: a reserved
    /// <c>Hierarchy/&lt;parent&gt;_children/</c> FOLDER, never the parent prop's GameObject, and
    /// never a folder that shares the parent's name.
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
        private Type propType;
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
            propType = importerType.GetNestedType("PropEntry", BindingFlags.Public);
            Assert.IsNotNull(propType, "PlacementImporter.PropEntry not found");

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
            Assert.AreEqual("table_01_children", folder.name);
            Assert.AreEqual("Hierarchy", folder.parent.name,
                "the parent's children live under the reserved Hierarchy container");
            Assert.AreEqual("porch", folder.parent.parent.name);
            Assert.AreEqual("tableware", folder.parent.parent.parent.name);
            Assert.AreEqual(propsRoot, folder.parent.parent.parent.parent);
            Assert.IsNull(folder.GetComponent<Renderer>(),
                "a container must never carry a Renderer: that is what hides a prop row");
        }

        [Test]
        public void NoContainerSharesTheParentPropsName()
        {
            // The identity folder named exactly like its parent prop is what a by-name probe
            // (Find / recursive name match) hits first: it reads (0,0,0) and no Renderer, so a
            // parent crab with a rider looked frozen at the origin. No object in the props tree
            // may carry the bare parent id.
            Transform folder = FolderFor("tableware", "porch", null, "table_01");
            Transform[] all = propsRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Assert.AreNotEqual("table_01", all[i].name,
                    "a container must not be named like a prop (" + all[i].name + ")");
            }

            Assert.AreEqual("table_01_children", folder.name);
        }

        [Test]
        public void ContainerNameIsTheIdPlusTheSuffix()
        {
            string name = (string)InvokeImporter("HierarchyContainerName", "coconut_crab_009");
            Assert.AreEqual("coconut_crab_009_children", name);
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
            // zone -> Hierarchy -> table_01_children: exactly one of each, however many runs.
            Assert.AreEqual(1, CountNamed(first.parent.parent, "Hierarchy"),
                "no duplicate Hierarchy folder");
            Assert.AreEqual(1, CountNamed(first.parent, "table_01_children"),
                "no duplicate parent folder");
        }

        // ------------------------------------------------------------ world scale ---
        // The contract: a prop's authored scale produces that WORLD size, whatever container it
        // ends up under (`aabb` is in metres and already contains the scale). These pin the bug
        // class behind 18 reference-level child props whose local scale was 1 while their world
        // bounds were ~1/100th of the document's aabb.

        [Test]
        public void AChildUnderAScaledContainerKeepsItsAuthoredWorldSize()
        {
            Transform folder = FolderFor("tableware", "porch", null, "table_01");
            var child = new GameObject("teacup_12");
            child.hideFlags = HideFlags.DontSave;
            child.transform.SetParent(folder, false);

            // A container that is NOT identity - an older layout that nested children inside a
            // scaled prop, or a hand-edited folder. The authored scale must still be the WORLD one.
            folder.localScale = new Vector3(0.5f, 0.25f, 2f);
            var authored = new Vector3(0.5f, 0.25f, 2f);
            object prop = NewProp("teacup_12", authored.x, authored.y, authored.z);

            bool compensated = (bool)InvokeImporter("ApplyPose", child, prop);

            Assert.IsTrue(compensated, "the importer must notice and report the parent's scale");
            Assert.AreEqual(0f, Vector3.Distance(authored, child.transform.lossyScale), 1e-4f,
                "world scale must be the authored one; tolerance 1e-4 (float division by the parent)");
            Assert.IsTrue((bool)InvokeImporter("PoseMatches", child, prop),
                "a compensated prop must read back as settled, or every import would rewrite it");
        }

        [Test]
        public void AChildUnderAnIdentityContainerKeepsTheAuthoredLocalScale()
        {
            // The clean path: three levels of identity folders, so local == world and nothing is
            // rewritten (this is what every prop in a freshly imported level looks like).
            Transform folder = FolderFor("tableware", "porch", null, "table_01");
            var child = new GameObject("teacup_13");
            child.hideFlags = HideFlags.DontSave;
            child.transform.SetParent(folder, false);
            object prop = NewProp("teacup_13", 1f, 1f, 1f);

            bool compensated = (bool)InvokeImporter("ApplyPose", child, prop);

            Assert.IsFalse(compensated, "an identity container needs no compensation");
            Assert.AreEqual(Vector3.one, child.transform.localScale);
            Assert.AreEqual(0f, Vector3.Distance(Vector3.one, child.transform.lossyScale), 1e-5f);
            Assert.IsTrue((bool)InvokeImporter("PoseMatches", child, prop));
        }

        [Test]
        public void ANonUniformAuthoredScaleSurvivesANonUniformContainer()
        {
            // Per-axis compensation, not a single factor: the container squeezes x and stretches z.
            Transform folder = FolderFor("tableware", "porch", null, "table_01");
            var child = new GameObject("teacup_14");
            child.hideFlags = HideFlags.DontSave;
            child.transform.SetParent(folder, false);
            folder.localScale = new Vector3(0.5f, 1f, 2f);
            var authored = new Vector3(0.0741f, 0.0291f, 0.0197f);
            object prop = NewProp("teacup_14", authored.x, authored.y, authored.z);

            Assert.IsTrue((bool)InvokeImporter("ApplyPose", child, prop));
            Assert.AreEqual(0f, Vector3.Distance(authored, child.transform.lossyScale), 1e-4f,
                "each axis is compensated separately (the D-5 non-uniform case)");
        }

        [Test]
        public void FolderCleanupKeepsPrefabBonesAndUnmarkedNodes()
        {
            Scene preview = EditorSceneManager.NewPreviewScene();
            string prefabPath = "Assets/PlacementFolderCleanup_" + Guid.NewGuid().ToString("N") + ".prefab";
            try
            {
                SceneManager.MoveGameObjectToScene(root, preview);
                var source = new GameObject("Skin");
                SceneManager.MoveGameObjectToScene(source, preview);
                new GameObject("root").transform.SetParent(source.transform, false);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
                Assert.IsNotNull(prefab);

                Transform occupied = FolderFor("tableware", "porch", null, null);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, occupied);
                Transform bone = instance.transform.Find("root");
                Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(bone));
                Transform empty = FolderFor("furniture", "yard", "set", "table_01");
                Transform legacy = new GameObject("legacy").transform;
                legacy.SetParent(propsRoot, false);
                Assert.AreEqual(legacy, FolderFor("legacy", null, null, null),
                    "use existing folders without adopting them for deletion");
                Transform decorated = FolderFor("decorated", null, null, null);
                decorated.gameObject.AddComponent<AudioSource>();

                InvokeImporter("DeleteEmptyFolders", propsRoot, report);

                Assert.IsTrue(bone != null, "prefab bone was removed");
                Assert.IsEmpty(PrefabUtility.GetRemovedGameObjects(instance));
                Assert.IsTrue(empty == null, "the importer-owned empty folder should be removed");
                Assert.IsTrue(propsRoot.Find("furniture") == null, "prune newly empty ancestors too");
                Assert.IsTrue(legacy != null, "keep an unmarked empty node");
                Assert.IsTrue(decorated != null, "do not delete a folder that acquired a component");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
                AssetDatabase.DeleteAsset(prefabPath);
            }
        }

        [Test]
        public void FolderMarkerDoesNotReachPickupCandidateValidation()
        {
            Transform folder = FolderFor("tableware", "porch", "set", null);
            Transform prop = new GameObject("teacup_01", typeof(MeshRenderer)).transform;
            prop.SetParent(folder, false);
            new GameObject("old_empty_folder").transform.SetParent(folder, false);
            Transform colliderOnly = new GameObject("collider_only", typeof(BoxCollider)).transform;
            colliderOnly.SetParent(folder, false);
            Type sync = FindType("VRCKatamari.Editor.Pickup.PickupFieldSync");
            Assert.IsNotNull(sync);
            MethodInfo collect = sync.GetMethod("CollectCandidates", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(collect);
            var candidates = (System.Collections.IList)collect.Invoke(null, new object[] { propsRoot });
            Assert.AreEqual(2, candidates.Count, "markers and unmarked empty leaves are not candidates");
            object colliderRow = candidates[0]; // CollectCandidates 排序按路径，collider_only 在前。
            Assert.AreEqual(colliderOnly, Field(colliderRow, "Root"));
            StringAssert.Contains("collider-only", (string)Field(colliderRow, "Rejection"));
            Assert.AreEqual(prop, Field(candidates[1], "Root"));
        }

        private object NewProp(string itemId, float sx, float sy, float sz)
        {
            object prop = Activator.CreateInstance(propType);
            SetField(prop, "itemId", itemId);
            SetField(prop, "position", new[] { 0f, 0f, 0f });
            SetField(prop, "rotationQuat", new[] { 0f, 0f, 0f, 1f });
            SetField(prop, "scale", new[] { sx, sy, sz });
            return prop;
        }

        /// <summary>Invokes one private static PlacementImporter method by name and arity.</summary>
        private object InvokeImporter(string name, params object[] args)
        {
            MethodInfo[] methods = importerType.GetMethods(BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, name, StringComparison.Ordinal)) continue;
                if (methods[i].GetParameters().Length != args.Length) continue;
                return methods[i].Invoke(null, args);
            }

            Assert.Fail(name + " with " + args.Length + " argument(s) not found");
            return null;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, target.GetType().Name + " has no field '" + name + "'");
            field.SetValue(target, value);
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
