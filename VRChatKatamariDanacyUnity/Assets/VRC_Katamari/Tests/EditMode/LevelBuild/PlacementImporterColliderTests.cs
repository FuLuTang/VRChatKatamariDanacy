using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the JsonUtility presence rule for <c>defs[].collider</c>: an absent block is
    /// materialised as a default <c>ColliderEntry</c> with an empty type, and must NOT be
    /// treated as declared. The O6 re-import produced 44
    /// <c>collider.type must be box/sphere/capsule/convex (got '')</c> errors because the
    /// importer tested <c>collider != null</c>.
    /// </summary>
    /// <remarks>
    /// Reflection, like the other importer tests: <c>PlacementImporter</c> lives in the
    /// editor assembly and this test assembly references only <c>VRCKatamari.Runtime</c>.
    /// The real <c>JsonUtility</c> path is used, so this is the defect's actual trigger, not
    /// a hand-built DTO.
    /// </remarks>
    public class PlacementImporterColliderTests
    {
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";
        private const string RulesTypeName =
            "VRCKatamari.Editor.Pickup.PickupFieldAuthoringRules";

        private Type importerType;
        private Type docType;
        private Type defType;
        private Type colliderType;
        private Type rulesType;

        [SetUp]
        public void SetUp()
        {
            importerType = FindType(ImporterTypeName);
            rulesType = FindType(RulesTypeName);
            Assert.IsNotNull(importerType, ImporterTypeName + " type not loaded");
            Assert.IsNotNull(rulesType, RulesTypeName + " type not loaded");
            docType = importerType.GetNestedType("PlacementDocument", BindingFlags.Public);
            defType = importerType.GetNestedType("DefEntry", BindingFlags.Public);
            colliderType = importerType.GetNestedType("ColliderEntry", BindingFlags.Public);
            Assert.IsNotNull(docType, "PlacementDocument");
            Assert.IsNotNull(defType, "DefEntry");
            Assert.IsNotNull(colliderType, "ColliderEntry");
        }

        [Test]
        public void AnAbsentBlockMaterialisesButIsNotDeclared()
        {
            const string json = "{\"defs\":[{\"id\":\"acorn\",\"kind\":\"nature\"," +
                                "\"valueUnits\":1}],\"props\":[{\"itemId\":\"a\",\"def\":\"acorn\"}]}";
            object doc = JsonUtility.FromJson(json, docType);
            Assert.IsNotNull(doc, "the document must parse");

            Array defs = (Array)docType.GetField("defs").GetValue(doc);
            object def = defs.GetValue(0);
            object collider = defType.GetField("collider").GetValue(def);
            Assert.IsNotNull(collider,
                "JsonUtility materialises the nested ColliderEntry even when the key is absent");
            // JsonUtility leaves the absent block's `type` at its CLR default. For a string
            // that is null (not ""), so the predicate must treat both as "not declared"
            // (it does: null and a blank string are the same answer). The test accepts
            // either spelling instead of pinning one serializer detail.
            string absentType = (string)colliderType.GetField("type").GetValue(collider);
            Assert.IsTrue(absentType == null || absentType.Trim().Length == 0,
                "the absent block's type must be null or empty, got '" + absentType + "'");

            Assert.IsFalse((bool)InvokeRules("IsColliderDeclared", ""),
                "an empty type is not a declaration");
            Assert.IsTrue((bool)InvokeRules("IsColliderDeclared", "box"));
            Assert.AreEqual(0, DeclaredProxyCount(doc),
                "an absent collider must contribute 0 declared proxies");
        }

        [Test]
        public void ARealBlockStillValidatesAndIsDeclared()
        {
            const string json = "{\"defs\":[{\"id\":\"melon\",\"kind\":\"food\"," +
                                "\"valueUnits\":1,\"collider\":{\"type\":\"sphere\"," +
                                "\"center\":[0,0,0],\"radius\":0.05,\"source\":\"auto\"," +
                                "\"fit_error\":0.01}}],\"props\":[{\"itemId\":\"m\"," +
                                "\"def\":\"melon\"}]}";
            object doc = JsonUtility.FromJson(json, docType);
            Array defs = (Array)docType.GetField("defs").GetValue(doc);
            object def = defs.GetValue(0);
            object collider = defType.GetField("collider").GetValue(def);
            Assert.IsTrue((bool)InvokeRules("IsColliderDeclared",
                (string)colliderType.GetField("type").GetValue(collider)));

            object proxy;
            string error;
            Assert.IsTrue(TryBuildProxy(collider, out proxy, out error), error);
            Assert.AreEqual("sphere", proxy.GetType().GetField("TypeName").GetValue(proxy));
            Assert.That((float)proxy.GetType().GetField("Radius").GetValue(proxy),
                Is.EqualTo(0.05f).Within(1e-6f));
            Assert.AreEqual(1, DeclaredProxyCount(doc),
                "a real block contributes exactly one declared proxy");
        }

        [Test]
        public void AnEmptyBlockIsRefusedByTheRulesNotTreatedAsABox()
        {
            object proxy = Activator.CreateInstance(
                rulesType.GetNestedType("PickupProxyDefinition", BindingFlags.Public));
            string error = (string)InvokeRules("ProxyError", proxy);
            Assert.IsNotNull(error, "an empty proxy definition must be refused");
            StringAssert.Contains("not declared", error);
        }

        private int DeclaredProxyCount(object doc)
        {
            MethodInfo method = importerType.GetMethod("BuildDeclaredProxies",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "BuildDeclaredProxies");
            object result = method.Invoke(null, new object[] { doc });
            return ((IDictionary)result).Count;
        }

        private bool TryBuildProxy(object collider, out object proxy, out string error)
        {
            MethodInfo method = importerType.GetMethod("TryBuildProxy",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "TryBuildProxy");
            object[] args = { collider, null, null };
            bool ok = (bool)method.Invoke(null, args);
            proxy = args[1];
            error = (string)args[2];
            return ok;
        }

        private object InvokeRules(string name, params object[] args)
        {
            MethodInfo method = rulesType.GetMethod(name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(method, name);
            return method.Invoke(null, args);
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }
    }
}
