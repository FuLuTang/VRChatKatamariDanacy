using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins §4's <c>shape:"proxy"</c> contract: the contact shape comes from the solid's
    /// <c>collider</c> (box/sphere/capsule/convex, local space), the support face stays the
    /// <c>size</c> box's top, and <c>proxySource</c> says where the proxy came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the split matters (the data's own reason): a tree's AABB contains the whole crown, so
    /// a crab standing at the trunk was reported as embedded 60 cm in a "solid". The proxy fixes
    /// the CONTACT; the <c>size</c> box still has to supply the standable TOP, because a fitted
    /// convex hull's upper face can be a spike or a slope and a ball cannot stand on it.
    /// </para>
    /// <para>
    /// The motor's static table holds OBBs only, so a sphere/capsule/convex proxy reaches the
    /// scene as its local AABB box - the documented interim ("先用盒包住代理做过渡"). These tests
    /// pin that the loss is REPORTED (a note on the derived box, an aggregate
    /// <c>[structure_proxy]</c> line from the importer) rather than silent, and that a scene
    /// import of the same document twice writes nothing the second time.
    /// </para>
    /// </remarks>
    public class PlacementV2StructureProxyTests
    {
        private const string ReaderTypeName = "VRCKatamari.Editor.LevelBuild.PlacementV2Reader";
        private const string RulesTypeName = "VRCKatamari.Editor.LevelBuild.PlacementV2Rules";
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";
        private const string ReportTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImportReport";

        private Type readerType;
        private Type rulesType;
        private Type importerType;
        private Type reportType;

        [SetUp]
        public void SetUp()
        {
            readerType = FindType(ReaderTypeName);
            rulesType = FindType(RulesTypeName);
            importerType = FindType(ImporterTypeName);
            reportType = FindType(ReportTypeName);
            Assert.IsNotNull(readerType, ReaderTypeName + " not loaded");
            Assert.IsNotNull(rulesType, RulesTypeName + " not loaded");
            Assert.IsNotNull(importerType, ImporterTypeName + " not loaded");
            Assert.IsNotNull(reportType, ReportTypeName + " not loaded");
        }

        // ------------------------------------------------------------ parsing ---

        [Test]
        public void ProxySolidCarriesItsContactBlockAndSource()
        {
            object doc = Parse(Solid("tree", "proxy", "def", BoxProxy("[1,3,1]", "[0,0,0]")));
            object[] solids = Items(Field(doc, "Solids"));
            Assert.AreEqual(1, solids.Length);
            Assert.AreEqual("proxy", Field(solids[0], "Shape"));
            Assert.AreEqual("def", Field(solids[0], "ProxySource"));
            object collider = Field(solids[0], "Collider");
            Assert.IsNotNull(collider, "a proxy solid must materialise its collider block");
            Assert.AreEqual("box", Field(collider, "Type"));
            Assert.AreEqual(3f, ((float[])Field(collider, "Size"))[1]);
        }

        [Test]
        public void PlainBoxSolidHasNoContactBlock()
        {
            object doc = Parse(Solid("crate", "box", null, ""));
            object[] solids = Items(Field(doc, "Solids"));
            Assert.AreEqual("box", Field(solids[0], "Shape"));
            Assert.IsNull(Field(solids[0], "Collider"),
                "shape:box must not carry a contact proxy (an ignored block misleads the next reader)");
        }

        [Test]
        public void EmptyColliderBlockIsNotAProxy()
        {
            // The JsonUtility trap again, one level down: `collider:{}` is not a declared proxy.
            object doc = Parse(Solid("broken", "proxy", "def", @",""collider"":{}"));
            object[] solids = Items(Field(doc, "Solids"));
            Assert.IsNull(Field(solids[0], "Collider"));
            StringAssert.Contains("[structure_proxy]", SolidError(solids[0]));
        }

        // --------------------------------------------------------- validation ---

        [Test]
        public void ProxyRequiresACollider()
        {
            object doc = Parse(Solid("bare", "proxy", "def", ""));
            StringAssert.Contains("[structure_proxy]", SolidError(Items(Field(doc, "Solids"))[0]));
        }

        [Test]
        public void ProxyRequiresAProxySourceInTheEnum()
        {
            object doc = Parse(Solid("nosource", "proxy", null, BoxProxy("[1,1,1]", "[0,0,0]")));
            StringAssert.Contains("[structure_proxy]", SolidError(Items(Field(doc, "Solids"))[0]));

            string[] good = { "def", "aabb-fallback", "manual" };
            for (int i = 0; i < good.Length; i++)
            {
                object ok = Parse(Solid("s" + i, "proxy", good[i], BoxProxy("[1,1,1]", "[0,0,0]")));
                Assert.IsNull(SolidError(Items(Field(ok, "Solids"))[0]),
                    "proxySource '" + good[i] + "' must be accepted");
            }

            object bad = Parse(Solid("weird", "proxy", "approximate", BoxProxy("[1,1,1]", "[0,0,0]")));
            StringAssert.Contains("[structure_proxy]", SolidError(Items(Field(bad, "Solids"))[0]));
        }

        [Test]
        public void BoxShapeWithAColliderIsRefused()
        {
            object doc = Parse(Solid("crate", "box", "def", BoxProxy("[1,1,1]", "[0,0,0]")));
            StringAssert.Contains("[structure_proxy]", SolidError(Items(Field(doc, "Solids"))[0]));
        }

        [Test]
        public void InvalidProxyBlocksAreRefused()
        {
            string[] bodies =
            {
                @",""collider"":{""type"":""teapot""}",
                @",""collider"":{""type"":""box""}",
                @",""collider"":{""type"":""sphere""}",
                @",""collider"":{""type"":""capsule"",""radius"":0.2}",
                @",""collider"":{""type"":""convex"",""hull"":[0,0,0,1,0,0]}"
            };
            for (int i = 0; i < bodies.Length; i++)
            {
                object doc = Parse(Solid("bad" + i, "proxy", "def", bodies[i]));
                string error = SolidError(Items(Field(doc, "Solids"))[0]);
                StringAssert.Contains("[structure_proxy]", error,
                    "proxy block #" + i + " should be refused");
            }
        }

        // ------------------------------------------------------ contact box ---

        [Test]
        public void BoxProxyKeepsItsFootprintAndTakesTheSizeTop()
        {
            // size 2 x 4 x 2 (support box) but the proxy is 1 x 3 x 1 sitting from -1.5 to +1.5:
            // the contact footprint stays the proxy's, the top rises to size/2 = 2.
            object doc = Parse(Solid("post", "proxy", "def",
                BoxProxy("[1,3,1]", "[0,0,0]"), "[2,4,2]"));
            object box = ContactBox(Items(Field(doc, "Solids"))[0]);
            float[] size = (float[])Field(box, "Size");
            float[] center = (float[])Field(box, "Center");
            Assert.AreEqual(1f, size[0], 1e-4f, "x footprint = proxy");
            Assert.AreEqual(1f, size[2], 1e-4f, "z footprint = proxy");
            Assert.AreEqual(3.5f, size[1], 1e-4f, "height spans proxy bottom to size top");
            Assert.AreEqual(0.25f, center[1], 1e-4f);
            Assert.AreEqual(true, Field(box, "TopRaisedToSizeTop"));
            Assert.AreEqual(false, Field(box, "Exact"));
        }

        [Test]
        public void BoxProxyMatchingTheSizeBoxIsExactAndUnraised()
        {
            object doc = Parse(Solid("crate", "proxy", "def", BoxProxy("[2,4,2]", "[0,0,0]")));
            object box = ContactBox(Items(Field(doc, "Solids"))[0]);
            Assert.AreEqual(true, Field(box, "Exact"));
            Assert.AreEqual(false, Field(box, "TopRaisedToSizeTop"));
            Assert.IsNull(Field(box, "Note"));
        }

        [Test]
        public void ConvexProxyIsBoxedToItsHullAndSaysSo()
        {
            object doc = Parse(Solid("tree", "proxy", "def",
                @",""collider"":{""type"":""convex"",""center"":[0,1,0]," +
                @"""hull"":[-0.5,0,0, 0.5,0,0, 0.5,2,0, -0.5,2,0, 0,1,-0.25, 0,1,0.25]}",
                "[2,4,2]"));
            object box = ContactBox(Items(Field(doc, "Solids"))[0]);
            float[] size = (float[])Field(box, "Size");
            float[] center = (float[])Field(box, "Center");
            Assert.AreEqual(1f, size[0], 1e-4f, "hull AABB x");
            Assert.AreEqual(2f, size[1], 1e-4f, "hull AABB y");
            Assert.AreEqual(0.5f, size[2], 1e-4f, "hull AABB z");
            // The collider's own center is added: hull y 0..2 around a center at y=1 puts the
            // box at 1..3, whose top (3) is already above the size top (2) - so no raise.
            Assert.AreEqual(2f, center[1], 1e-4f);
            Assert.AreEqual(false, (bool)Field(box, "TopRaisedToSizeTop"));
            Assert.AreEqual(false, Field(box, "Exact"));
            StringAssert.Contains("[structure_proxy]", (string)Field(box, "Note"));
            StringAssert.Contains("convex", (string)Field(box, "Note"));
        }

        [Test]
        public void SphereProxyBecomesItsBoundingBox()
        {
            object doc = Parse(Solid("ball", "proxy", "def",
                @",""collider"":{""type"":""sphere"",""center"":[0,0.5,0],""radius"":0.3}",
                "[2,4,2]"));
            object box = ContactBox(Items(Field(doc, "Solids"))[0]);
            float[] size = (float[])Field(box, "Size");
            Assert.AreEqual(0.6f, size[0], 1e-4f);
            Assert.AreEqual(0.6f, size[2], 1e-4f);
            StringAssert.Contains("sphere", (string)Field(box, "Note"));
        }

        [Test]
        public void SupportFaceIsStillTheSizeBoxTop()
        {
            // The support rule is independent of the proxy: whatever the contact shape, the ball
            // stands on size.y/2 in the solid's frame.
            object doc = Parse(Solid("tree", "proxy", "def",
                @",""collider"":{""type"":""convex"",""hull"":[0,0,0,1,0,0,1,1,0,0,1,0]}",
                "[2,8,2]"));
            object solid = Items(Field(doc, "Solids"))[0];
            Assert.AreEqual(4f, (float)Invoke(rulesType, "SolidSupportTopY",
                new[] { solid }), 1e-4f);
            object box = ContactBox(solid);
            float top = ((float[])Field(box, "Center"))[1] +
                        ((float[])Field(box, "Size"))[1] * 0.5f;
            Assert.AreEqual(4f, top, 1e-4f, "the contact box's top is the support face");
        }

        // ------------------------------------------------------------ scene ---

        [Test]
        public void SecondImportOfTheSameSolidsWritesNothing()
        {
            object doc = Parse(Solid("tree", "proxy", "def",
                    @",""collider"":{""type"":""convex"",""hull"":[0,0,0,1,0,0,1,1,0,0,1,0]}",
                    "[2,4,2]") +
                "," + Solid("crate", "box", null, "", "[1,1,1]"));

            object view = Invoke(importerType, "BuildV1View", new[] { doc });
            var root = new GameObject("PlacementV2StructureProxyTests");
            root.hideFlags = HideFlags.DontSave;
            try
            {
                object first = Activator.CreateInstance(reportType);
                Assert.IsTrue((bool)Invoke(importerType, "ApplyStructures",
                    new[] { view, root.transform, first }), Errors(first));
                Assert.AreEqual(2, Field(first, "StructuresCreated"));
                Assert.AreEqual(0, Field(first, "StructuresUpdated"));

                Transform tree = root.transform.Find("tree");
                Assert.IsNotNull(tree, "the proxy solid's object");
                BoxCollider collider = tree.GetComponent<BoxCollider>();
                Assert.IsNotNull(collider, "a proxy solid still gets its one collision box");
                Assert.AreEqual(1, CountColliders(tree.gameObject),
                    "one contact box: no second collider for the support face");

                object second = Activator.CreateInstance(reportType);
                Assert.IsTrue((bool)Invoke(importerType, "ApplyStructures",
                    new[] { view, root.transform, second }), Errors(second));
                Assert.AreEqual(0, Field(second, "StructuresCreated"), "second run creates nothing");
                Assert.AreEqual(0, Field(second, "StructuresUpdated"), "second run moves nothing");
                Assert.AreEqual(0, Field(second, "StructuresDeleted"), "second run deletes nothing");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // ----------------------------------------------------- the reference ---

        [Test]
        public void ReferenceSolidsAreAllValidProxies()
        {
            string repoRoot = (string)Invoke(FindType("VRCKatamari.Editor.LevelBuild.LevelPaths"),
                "RepoRoot", new object[0]);
            if (string.IsNullOrEmpty(repoRoot)) Assert.Ignore("no repository root; skipped");
            string path = System.IO.Path.Combine(repoRoot,
                "Docs/Design/Levels/_ref_cancer/placement.v2.json");
            if (!System.IO.File.Exists(path))
            {
                Assert.Ignore("no _ref_cancer reference data on this machine; skipped");
            }

            object result = Invoke(readerType, "ReadFile", new object[] { path });
            Assert.IsNull(FirstError(result), "the reference document must parse clean");
            object[] solids = Items(Field(Field(result, "Document"), "Solids"));
            Assert.AreEqual(35, solids.Length);

            int box = 0;
            int convex = 0;
            int raised = 0;
            for (int i = 0; i < solids.Length; i++)
            {
                Assert.IsNull(SolidError(solids[i]), "solid " + Field(solids[i], "Id"));
                Assert.AreEqual("proxy", Field(solids[i], "Shape"));
                Assert.AreEqual("def", Field(solids[i], "ProxySource"));

                object boxInfo = ContactBox(solids[i]);
                string type = (string)Field(boxInfo, "ProxyType");
                if (type == "box") box++;
                else if (type == "convex") convex++;
                if ((bool)Field(boxInfo, "TopRaisedToSizeTop")) raised++;

                // The support face the ball stands on: the size box's top, to the millimetre.
                float top = ((float[])Field(boxInfo, "Center"))[1] +
                            ((float[])Field(boxInfo, "Size"))[1] * 0.5f;
                float sizeTop = (float)Invoke(rulesType, "SolidSupportTopY",
                    new[] { solids[i] });
                Assert.AreEqual(sizeTop, top, 0.001f,
                    "solid " + Field(solids[i], "Id") + " must offer the size top as support");
            }

            Assert.AreEqual(17, box, "box proxies");
            Assert.AreEqual(18, convex, "convex proxies");
            Debug.Log("[structure_proxy] reference solids: 35 proxies (17 box, 18 convex), " +
                      raised + " contact top(s) raised to the size top");
        }

        // --------------------------------------------------------------- helpers ---

        private static string Solid(string id, string shape, string proxySource, string extra,
            string size = "[2,4,2]")
        {
            var body = new System.Text.StringBuilder();
            body.Append(@"{""id"":""").Append(id).Append(@""",""kind"":""k"",""role"":""box"",")
                .Append(@"""shape"":""").Append(shape).Append(@""",""center"":[0,0,0],")
                .Append(@"""size"":").Append(size)
                .Append(@",""rotationQuat"":[0,0,0,1],""collision"":""box"",")
                .Append(@"""absorbable"":false,""support"":true");
            if (!string.IsNullOrEmpty(proxySource))
            {
                body.Append(@",""proxySource"":""").Append(proxySource).Append('"');
            }

            if (!string.IsNullOrEmpty(extra)) body.Append(extra);
            body.Append('}');
            return body.ToString();
        }

        private static string BoxProxy(string size, string center)
        {
            return @",""collider"":{""type"":""box"",""center"":" + center +
                   @",""size"":" + size + "}";
        }

        private object ContactBox(object solid)
        {
            MethodInfo method = FindMethod(rulesType, "TrySolidContactBox", 3);
            Assert.IsNotNull(method, "TrySolidContactBox not found");
            var args = new object[] { solid, null, null };
            bool built = (bool)method.Invoke(null, args);
            Assert.IsTrue(built, "contact box refused: " + args[2]);
            return args[1];
        }

        private string SolidError(object solid)
        {
            return (string)Invoke(rulesType, "SolidProxyError", new[] { solid });
        }

        private object Parse(string solids)
        {
            string json = @"{""schema"":""katamari.placement/2""," +
                          @"""meta"":{""id"":""probe"",""scene"":""S"",""units"":""m""," +
                          @"""convention"":""unity-y-up-left-handed-quat-xyzw""," +
                          @"""ball"":{""startDiameterM"":0.3,""maxDiameterRef"":""global""," +
                          @"""start"":[0,0,0],""spawnSource"":""authored""}}," +
                          @"""props"":[],""structures"":{""solids"":[" + solids + "]}}";
            object result = Invoke(readerType, "Read", new object[] { json });
            Assert.IsNull(FirstError(result), "the fixture must parse clean");
            return Field(result, "Document");
        }

        private static string FirstError(object result)
        {
            foreach (object error in (IEnumerable)Field(result, "Errors")) return (string)error;
            return null;
        }

        private static string Errors(object report)
        {
            var text = new System.Text.StringBuilder();
            foreach (object error in (IEnumerable)Field(report, "Errors"))
            {
                if (text.Length > 0) text.Append(" | ");
                text.Append(error);
            }

            return text.ToString();
        }

        private static int CountColliders(GameObject root)
        {
            return root.GetComponentsInChildren<Collider>(true).Length;
        }

        private static object[] Items(object arrayField)
        {
            if (arrayField == null) return new object[0];
            var array = (Array)arrayField;
            var items = new object[array.Length];
            for (int i = 0; i < array.Length; i++) items[i] = array.GetValue(i);
            return items;
        }

        private static object Field(object target, string name)
        {
            Assert.IsNotNull(target, "cannot read field '" + name + "' from a null object");
            FieldInfo field = target.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, target.GetType().Name + " has no field '" + name + "'");
            return field.GetValue(target);
        }

        private static object Invoke(Type type, string name, object[] args)
        {
            MethodInfo method = FindMethod(type, name, args.Length);
            Assert.IsNotNull(method, type.Name + " has no method '" + name + "'");
            return method.Invoke(null, args);
        }

        private static MethodInfo FindMethod(Type type, string name, int argumentCount)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, name, StringComparison.Ordinal)) continue;
                if (methods[i].GetParameters().Length == argumentCount) return methods[i];
            }

            return null;
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
