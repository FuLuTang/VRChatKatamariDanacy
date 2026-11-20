using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    public class ReferenceParityMathTests
    {
        private static Type EditorType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("VRCKatamari.Editor.LevelBuild." + name))
            .First(type => type != null);

        private static object Call(string name, params object[] args) =>
            EditorType("ReferenceParityMath").GetMethod(name).Invoke(null, args);

        private static object New(string name) => Activator.CreateInstance(EditorType(name));
        private static object Read(object target, string field) => target.GetType().GetField(field).GetValue(target);
        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field).SetValue(target, value);

        private static Array Rows(string type, params object[] items)
        {
            Array rows = Array.CreateInstance(EditorType(type), items.Length);
            for (int i = 0; i < items.Length; i++) rows.SetValue(items[i], i);
            return rows;
        }

        [Test]
        public void DuplicateCannotHideMissingId()
        {
            var result = Call("Compare", new[] { "a", "b" }, new[] { "b", "b" });
            Assert.That(Read(result, "Matched"), Is.EqualTo(1));
            Assert.That(Read(result, "Missing"), Is.EqualTo(1));
            Assert.That(Read(result, "Extra"), Is.EqualTo(1));
            Assert.That(Read(result, "MissingIds"), Is.EqualTo(new[] { "a" }));
            Assert.That(Read(result, "ExtraIds"), Is.EqualTo(new[] { "b" }));
        }

        [Test]
        public void ComparisonSamplesAtMostTenSortedDistinctIds()
        {
            var names = Enumerable.Range(0, 12).Select(i => $"id{i:D2}").Reverse().ToArray();
            var result = Call("Compare", names, new string[0]);
            Assert.That(Read(result, "Missing"), Is.EqualTo(12));
            Assert.That(Read(result, "MissingIds"), Is.EqualTo(names.Reverse().Take(10).ToArray()));
        }

        [Test]
        public void StaticBoxesCountPartsButNotOffAreaOrNonBoxRoles()
        {
            var doc = New("PlacementV2Document");
            var meta = New("PlacementV2Meta");
            Set(meta, "AreaNo", 2);
            Set(doc, "Meta", meta);
            Set(doc, "Solids", Rows("PlacementV2Solid", Solid("split", "box", 2, 3),
                Solid("off", "box", 3, 2), Solid("ramp", "ramp", -1, 0), Solid("single", "box", -1, 0)));
            Assert.That(((IEnumerable)Call("ExpectedStaticBoxes", doc)).Cast<string>().ToArray(),
                Is.EqualTo(new[] { "split", "split", "split", "single" }));
        }

        private static object Solid(string id, string role, int area, int parts)
        {
            var solid = New("PlacementV2Solid");
            Set(solid, "Id", id);
            Set(solid, "Role", role);
            Set(solid, "HitOnAreaNo", area);
            Set(solid, "Shape", parts == 0 ? "box" : "proxy");
            if (parts != 0) Set(solid, "Parts", Array.CreateInstance(EditorType("PlacementV2SolidPart"), parts));
            return solid;
        }

        [TestCase("motion", true)]
        [TestCase("attached", true)]
        [TestCase("ambient", true)]
        [TestCase("static", false)]
        public void DeclaredMotionDoesNotInventAnimationOnlyRows(string behaviour, bool expected)
        {
            var prop = New("PlacementV2Prop");
            Set(prop, "Behaviour", behaviour);
            Assert.That(Call("DeclaresMotion", prop), Is.EqualTo(expected));
        }

        [Test]
        public void AnimationUsesSpawnIndexAndRejectsPartialOrDuplicateSideTable()
        {
            var doc = New("PlacementV2Document");
            var crab = Prop("crab", 7);
            var spoon = Prop("spoon", 12);
            Set(doc, "Props", Rows("PlacementV2Prop", crab, spoon));
            const string start = "{\"format\":\"katamari.motion_table/1\",\"instances\":[";
            const string yes = "{\"instanceIndex\":7,\"animated\":true}";
            const string no = "{\"instanceIndex\":12,\"animated\":false}";
            Assert.That((ISet<string>)Call("ReadAnimatedIds", doc, start + no + "," + yes + "]}"), Is.EquivalentTo(new[] { "crab" }));
            Assert.That(Assert.Throws<TargetInvocationException>(() => Call("ReadAnimatedIds", doc, start + yes + "]}")).InnerException,
                Is.TypeOf<FormatException>());
            Assert.That(Assert.Throws<TargetInvocationException>(() => Call("ReadAnimatedIds", doc,
                start + no + "," + yes + "," + yes + "]}")).InnerException, Is.TypeOf<FormatException>());
        }

        private static object Prop(string id, int index)
        {
            var prop = New("PlacementV2Prop");
            Set(prop, "ItemId", id);
            Set(prop, "InstanceIndex", index);
            Set(prop, "HasInstanceIndex", true);
            return prop;
        }
    }
}
