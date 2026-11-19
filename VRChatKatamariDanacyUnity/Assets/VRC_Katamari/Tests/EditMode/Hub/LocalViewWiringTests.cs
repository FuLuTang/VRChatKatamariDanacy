using System;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// 装配器本地视图接线的纯部分：All-Maps 汇总读数行的格式、引用性能诊断的字段名与
    /// 运行时宿主一致、U# 程序资产路径规则。
    /// </summary>
    /// <remarks>
    /// 反射，与其它编辑器代码测试相同：被测类型在 <c>Assets/VRC_Katamari/Editor/</c>，本程序集只
    /// 引用 <c>VRCKatamari.Runtime</c>。场景接线本身（建对象、Udon 表）要 Unity 回合验证。
    /// </remarks>
    public class LocalViewWiringTests
    {
        private const string RideTypeName = "VRCKatamari.Editor.Story.RideUxAssembler";
        private const string ProgramsTypeName = "VRCKatamari.Editor.Hub.UdonProgramAssets";
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void SummaryNamesEveryCountAndTheWireframeState()
        {
            Assert.AreEqual("diagnostics wired 5/5 hosts, catch-name wired 3/3, wireframe settings wired",
                Summary(5, 5, 3, 3, true, true));
        }

        [Test]
        public void SummaryShowsPartialWiring()
        {
            Assert.AreEqual("diagnostics wired 4/5 hosts, catch-name wired 0/3, wireframe settings NOT wired",
                Summary(4, 5, 0, 3, true, false));
        }

        [Test]
        public void SummarySaysWhenTheWireframeIsMissing()
        {
            Assert.AreEqual("diagnostics wired 0/0 hosts, catch-name wired 0/0, wireframe settings MISSING (no CollisionWireframe)",
                Summary(0, 0, 0, 0, false, false));
        }

        [TestCase(typeof(VRCKatamari.BallMotor.KatamariBallMotor))]
        [TestCase(typeof(VRCKatamari.Pickup.PickupFieldHost))]
        [TestCase(typeof(VRCKatamari.Pickup.MotionHost))]
        public void EveryProfiledHostHasThePerformanceDiagnosticsField(Type host)
        {
            string name = (string)Field(RideTypeName, "PerformanceDiagnosticsField");
            FieldInfo field = host.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(field, host.Name + " has no field " + name);
            Assert.AreEqual(typeof(VRCKatamari.Diagnostics.BallPerformanceDiagnostics), field.FieldType);
        }

        [Test]
        public void PickupFieldHostHasTheCatchNameDisplayField()
        {
            string name = (string)Field(RideTypeName, "CatchNameDisplayField");
            FieldInfo field = typeof(VRCKatamari.Pickup.PickupFieldHost).GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            Assert.AreEqual(typeof(VRCKatamari.BallLayers.CatchNameDisplay), field.FieldType);
        }

        [TestCase("Assets/VRC_Katamari/Runtime/Diagnostics/BallPerformanceDiagnostics.cs",
            "Assets/VRC_Katamari/Runtime/Diagnostics/BallPerformanceDiagnostics.asset")]
        [TestCase("Assets/X/Foo.CS", "Assets/X/Foo.asset")]
        [TestCase("Assets/X/Foo.txt", null)]
        [TestCase("", null)]
        public void ProgramAssetSitsNextToTheScript(string script, string expected)
        {
            Type type = FindType(ProgramsTypeName);
            Assert.IsNotNull(type, ProgramsTypeName + " not loaded");
            MethodInfo method = type.GetMethod("ProgramAssetPath", AnyStatic);
            Assert.AreEqual(expected, method.Invoke(null, new object[] { script }));
        }

        // ---- helpers ----

        private static string Summary(int dw, int dh, int cw, int ch, bool found, bool wired)
        {
            Type type = FindType(RideTypeName);
            Assert.IsNotNull(type, RideTypeName + " not loaded");
            MethodInfo method = type.GetMethod("LocalWiringSummary", AnyStatic);
            Assert.IsNotNull(method, "LocalWiringSummary missing");
            return (string)method.Invoke(null, new object[] { dw, dh, cw, ch, found, wired });
        }

        private static object Field(string typeName, string name)
        {
            Type type = FindType(typeName);
            Assert.IsNotNull(type, typeName + " not loaded");
            FieldInfo field = type.GetField(name, AnyStatic);
            Assert.IsNotNull(field, typeName + "." + name + " missing");
            return field.GetValue(null);
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
