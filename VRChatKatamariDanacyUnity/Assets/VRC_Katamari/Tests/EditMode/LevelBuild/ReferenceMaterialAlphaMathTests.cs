using System;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins how a reference prop's source glTF material decides its Unity render mode:
    /// alphaMode -> Standard Opaque/Cutout/Fade, alphaCutoff -> _Cutoff, the GLB JSON chunk,
    /// the materials list, and which manifest names the source GLB.
    /// </summary>
    /// <remarks>
    /// Reflection, like the other LevelBuild tests: the rules live in the default Editor
    /// assembly, which this test assembly cannot reference by name. The GLB and material cases
    /// are shaped like the extracted Cancer data (tomato: one MASK material with
    /// alphaCutoff 1/255; color_tv: two MASK and one BLEND; eggplant: two manifests that name
    /// different GLBs).
    /// </remarks>
    public class ReferenceMaterialAlphaMathTests
    {
        private const string MathTypeName = "VRCKatamari.Editor.LevelBuild.ReferenceMaterialAlphaMath";
        private const float TomatoCutoff = 0.00392156862745098f;

        private static Type MathType => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(MathTypeName))
            .First(type => type != null);

        private static object Call(string name, params object[] args)
        {
            MethodInfo method = MathType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == name && m.GetParameters().Length == args.Length);
            return method.Invoke(null, args);
        }

        private static object Mode(string name) => Enum.Parse(MathType.GetNestedType("RenderMode"), name);
        private static object Read(object target, string member)
        {
            Type type = target.GetType();
            FieldInfo field = type.GetField(member);
            return field != null ? field.GetValue(target) : type.GetProperty(member).GetValue(target);
        }

        [TestCase(null, "Opaque")]
        [TestCase("", "Opaque")]
        [TestCase("OPAQUE", "Opaque")]
        [TestCase("MASK", "Cutout")]
        [TestCase("BLEND", "Fade")]
        [TestCase("mask", "Unknown")]
        [TestCase("ADDITIVE", "Unknown")]
        public void AlphaModeMapsToStandardMode(string alphaMode, string expected)
        {
            Assert.That(Call("ForAlphaMode", alphaMode), Is.EqualTo(Mode(expected)));
        }

        [TestCase(true, 0.00392156862745098, 0.00392156862745098)]
        [TestCase(true, 0.2, 0.2)]
        [TestCase(false, 0.0, 0.5)]
        [TestCase(true, double.NaN, 0.5)]
        [TestCase(true, -1.0, 0.0)]
        [TestCase(true, 2.0, 1.0)]
        public void CutoffIsTheSourceValueOrTheGltfDefault(bool has, double value, double expected)
        {
            Assert.That((float)Call("Cutoff", has, value), Is.EqualTo((float)expected).Within(1e-7f));
        }

        [Test]
        public void EachModeWritesTheStandardInspectorState()
        {
            object opaque = Mode("Opaque"), cutout = Mode("Cutout"), fade = Mode("Fade");

            Assert.That(Call("StandardMode", opaque), Is.EqualTo(0f));
            Assert.That(Call("StandardMode", cutout), Is.EqualTo(1f));
            Assert.That(Call("StandardMode", fade), Is.EqualTo(2f));

            Assert.That(Call("RenderTypeTag", opaque), Is.EqualTo(""));
            Assert.That(Call("RenderTypeTag", cutout), Is.EqualTo("TransparentCutout"));
            Assert.That(Call("RenderTypeTag", fade), Is.EqualTo("Transparent"));

            // Fade is straight alpha (SrcAlpha, OneMinusSrcAlpha), not premultiplied.
            Assert.That(Call("SourceBlend", cutout), Is.EqualTo(1));
            Assert.That(Call("DestinationBlend", cutout), Is.EqualTo(0));
            Assert.That(Call("SourceBlend", fade), Is.EqualTo(5));
            Assert.That(Call("DestinationBlend", fade), Is.EqualTo(10));

            Assert.That(Call("ZWrite", cutout), Is.EqualTo(1));
            Assert.That(Call("ZWrite", fade), Is.EqualTo(0));

            Assert.That(Call("Keyword", opaque), Is.Null);
            Assert.That(Call("Keyword", cutout), Is.EqualTo("_ALPHATEST_ON"));
            Assert.That(Call("Keyword", fade), Is.EqualTo("_ALPHABLEND_ON"));

            Assert.That(Call("RenderQueue", opaque), Is.EqualTo(-1));
            Assert.That(Call("RenderQueue", cutout), Is.EqualTo(2450));
            Assert.That(Call("RenderQueue", fade), Is.EqualTo(3000));
        }

        [Test]
        public void CutoutAndFadeAreNeverInstancedOrOccluders()
        {
            // The instancing pass and the occluder rule skip queue >= 2450; both modes must land
            // there so an alpha-tested leaf is never batched or used to hide what is behind it.
            Assert.That((int)Call("RenderQueue", Mode("Cutout")), Is.GreaterThanOrEqualTo(2450));
            Assert.That((int)Call("RenderQueue", Mode("Fade")), Is.GreaterThanOrEqualTo(2450));
        }

        [Test]
        public void GlbJsonChunkIsRead()
        {
            string json = "{\"materials\":[{\"name\":\"part_0_0\",\"alphaMode\":\"MASK\"}]}";
            object[] args = { Glb(json), null, null };
            MethodInfo method = MathType.GetMethod("TryReadGlbJson");
            Assert.That((bool)method.Invoke(null, args), Is.True, (string)args[2]);
            // The chunk keeps the spec's space padding to 4 bytes; JSON ignores it.
            Assert.That(((string)args[1]).TrimEnd(' '), Is.EqualTo(json));
            Assert.That(((Array)ReadMaterials((string)args[1])).Length, Is.EqualTo(1));
        }

        [TestCase("short")]
        [TestCase("magic")]
        [TestCase("version")]
        [TestCase("chunk")]
        [TestCase("length")]
        public void NonGlbIsRefused(string damage)
        {
            byte[] data = Glb("{}");
            switch (damage)
            {
                case "short": data = data.Take(10).ToArray(); break;
                case "magic": data[0] = (byte)'x'; break;
                case "version": data[4] = 1; break;
                case "chunk": data[16] = (byte)'B'; break;
                case "length": data[12] = 200; break;
            }

            object[] args = { data, null, null };
            Assert.That((bool)MathType.GetMethod("TryReadGlbJson").Invoke(null, args), Is.False);
            Assert.That(args[2], Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void MaterialsKeepModeCutoffAndSides()
        {
            string json = "{\"materials\":[" +
                          "{\"name\":\"part_0_0\",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.00392156862745098}," +
                          "{\"name\":\"part_0_1\",\"alphaMode\":\"MASK\"}," +
                          "{\"name\":\"part_0_2\",\"alphaMode\":\"BLEND\",\"doubleSided\":true}," +
                          "{\"name\":\"part_1_0\"}]}";
            Array materials = ReadMaterials(json);
            Assert.That(materials.Length, Is.EqualTo(4));

            object tomatoLike = Call("Find", materials, "part_0_0");
            Assert.That(Read(tomatoLike, "Mode"), Is.EqualTo(Mode("Cutout")));
            Assert.That((float)Read(tomatoLike, "CutoffValue"), Is.EqualTo(TomatoCutoff).Within(1e-7f));

            object noCutoff = Call("Find", materials, "part_0_1");
            Assert.That((float)Read(noCutoff, "CutoffValue"), Is.EqualTo(0.5f));

            object blend = Call("Find", materials, "part_0_2");
            Assert.That(Read(blend, "Mode"), Is.EqualTo(Mode("Fade")));
            Assert.That(Read(blend, "DoubleSided"), Is.EqualTo(true));

            object plain = Call("Find", materials, "part_1_0");
            Assert.That(Read(plain, "Mode"), Is.EqualTo(Mode("Opaque")));
            Assert.That(Read(plain, "DoubleSided"), Is.EqualTo(false));

            Assert.That(Call("Find", materials, "part_9_9"), Is.Null);
        }

        [Test]
        public void NoMaterialsIsAnEmptyList()
        {
            Assert.That(ReadMaterials("{\"asset\":{\"version\":\"2.0\"}}").Length, Is.EqualTo(0));
        }

        [TestCase("{\"materials\":[{\"alphaMode\":\"MASK\"}]}")]
        [TestCase("{\"materials\":[{\"name\":\"a\"},{\"name\":\"a\"}]}")]
        [TestCase("{\"materials\":{}}")]
        [TestCase("[1]")]
        [TestCase("{")]
        public void AmbiguousOrBrokenMaterialsAreRefused(string json)
        {
            object[] args = { json, null, null };
            Assert.That((bool)MathType.GetMethod("TryReadMaterials").Invoke(null, args), Is.False);
            Assert.That(args[2], Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void OwnManifestNamesTheSourceGlb()
        {
            // eggplant/: the folder holds the Cancer conversion's eggplant.manifest.json (036b)
            // and MAS1's 03b3_Eggplant.manifest.json; the FBX is the Cancer one.
            Assert.That(Resolve("eggplant",
                    new[] { "03b3_Eggplant.manifest.json", "eggplant.manifest.json" },
                    new[] { "models/glb-pack/03b3_Eggplant.glb", "036b_Eggplant.glb" }),
                Is.EqualTo("036b_Eggplant.glb"));
        }

        [Test]
        public void ForeignManifestsMustAgree()
        {
            Assert.That(Resolve("peeler", new[] { "0006_Peeler.manifest.json" },
                new[] { "models/glb-pack/0006_Peeler.glb" }), Is.EqualTo("0006_Peeler.glb"));
            Assert.That(Resolve("bookstand", new[] { "0511_Bookstand.manifest.json", "other.manifest.json" },
                new[] { "models\\glb-pack\\0511_Bookstand.glb", "0511_Bookstand.glb" }), Is.EqualTo("0511_Bookstand.glb"));
            Assert.That(Resolve("eggplant", new[] { "a.manifest.json", "b.manifest.json" },
                new[] { "036b_Eggplant.glb", "03b3_Eggplant.glb" }), Is.Null);
        }

        [Test]
        public void MissingSourceIsRefused()
        {
            Assert.That(Resolve("tomato", new string[0], new string[0]), Is.Null);
            Assert.That(Resolve("tomato", new[] { "tomato.manifest.json" }, new string[] { null }), Is.Null);
            Assert.That(Resolve("tomato", new[] { "x.manifest.json" }, new[] { "" }), Is.Null);
        }

        private static string Resolve(string stem, string[] names, string[] sources)
        {
            object[] args = { stem, names, sources, null, null };
            bool ok = (bool)MathType.GetMethod("TryResolveSourceGlb").Invoke(null, args);
            if (!ok) Assert.That(args[4], Is.Not.Null.And.Not.Empty);
            return ok ? (string)args[3] : null;
        }

        private static Array ReadMaterials(string json)
        {
            object[] args = { json, null, null };
            Assert.That((bool)MathType.GetMethod("TryReadMaterials").Invoke(null, args), Is.True, (string)args[2]);
            return (Array)args[1];
        }

        private static byte[] Glb(string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            int padded = (payload.Length + 3) & ~3;
            var data = new byte[20 + padded];
            WriteUInt32(data, 0, 0x46546C67);
            WriteUInt32(data, 4, 2);
            WriteUInt32(data, 8, (uint)data.Length);
            WriteUInt32(data, 12, (uint)padded);
            WriteUInt32(data, 16, 0x4E4F534A);
            Array.Copy(payload, 0, data, 20, payload.Length);
            for (int i = 20 + payload.Length; i < data.Length; i++) data[i] = (byte)' ';
            return data;
        }

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }
    }
}
