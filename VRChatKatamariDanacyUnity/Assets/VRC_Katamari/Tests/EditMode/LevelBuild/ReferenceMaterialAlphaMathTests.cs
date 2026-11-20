using System;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins how a reference prop's source glTF material decides its Unity render mode:
    /// alphaMode -> Standard Opaque/Cutout/Fade, the REROLL cutoff 0.5, which textures lose
    /// their mipmaps, the audit's mismatch rule, the GLB JSON chunk, the materials list, and
    /// which manifest names the source GLB.
    /// </summary>
    /// <remarks>
    /// Reflection, like the other LevelBuild tests: the rules live in the default Editor
    /// assembly, which this test assembly cannot reference by name. The GLB and material cases
    /// are shaped like the extracted Cancer data (tomato: one MASK material with
    /// alphaCutoff 1/255; color_tv: two MASK and one BLEND; minivan: OPAQUE only; eggplant: two
    /// manifests that name different GLBs).
    /// </remarks>
    public class ReferenceMaterialAlphaMathTests
    {
        private const string MathTypeName = "VRCKatamari.Editor.LevelBuild.ReferenceMaterialAlphaMath";

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

        [Test]
        public void CutoutThresholdIsRerollsHalfNotTheGlbValue()
        {
            // REROLL CustomStandard: discard when alpha < _Cutoff, _Cutoff = 0.5 on the material.
            Assert.That(MathType.GetField("CutoutThreshold").GetValue(null), Is.EqualTo(0.5f));
        }

        [TestCase("MASK", true, true, true)]
        [TestCase("MASK", false, true, false)]
        [TestCase("BLEND", true, true, false)]
        [TestCase("OPAQUE", true, false, false)]
        [TestCase(null, true, false, false)]
        [TestCase("ADDITIVE", true, false, false)]
        public void OnlyTexturedMaskTexturesLoseMipmaps(string alphaMode, bool textured,
            bool expectedAlpha, bool expectedNoMips)
        {
            Array materials = ReadMaterials(MaterialJson(alphaMode, textured));
            object[] args = { materials, null, null };
            MathType.GetMethod("TextureSettings").Invoke(null, args);
            Assert.That(args[1], Is.EqualTo(expectedAlpha), "alphaIsTransparency");
            Assert.That(args[2], Is.EqualTo(expectedNoMips), "disableMipmaps");
        }

        [Test]
        public void MixedMaskAndBlendTextureLosesMipmaps()
        {
            // color_tv-like: the one image serves MASK and BLEND parts; the MASK part samples it.
            Array materials = ReadMaterials("{\"materials\":[" + Part("a", "MASK", true) + "," +
                                            Part("b", "BLEND", true) + "]}");
            object[] args = { materials, null, null };
            MathType.GetMethod("TextureSettings").Invoke(null, args);
            Assert.That(args[1], Is.True);
            Assert.That(args[2], Is.True);

            object[] none = { null, null, null };
            MathType.GetMethod("TextureSettings").Invoke(null, none);
            Assert.That(none[1], Is.False);
            Assert.That(none[2], Is.False);
        }

        [TestCase("Cutout", 2450, true, 0.5f, false)]
        [TestCase("Cutout", 2450, true, 0.00392f, true)]
        [TestCase("Cutout", 2450, false, 0f, true)]
        [TestCase("Cutout", 2000, true, 0.5f, true)]
        [TestCase("Cutout", -1, true, 0.5f, true)]
        [TestCase("Fade", 3000, true, 0.5f, false)]
        [TestCase("Fade", 3000, true, 0.1f, false)]
        [TestCase("Fade", 2450, true, 0.5f, true)]
        [TestCase("Opaque", 2000, true, 0.5f, false)]
        [TestCase("Opaque", 3000, false, 0f, false)]
        [TestCase("Unknown", 2000, false, 0f, false)]
        public void MismatchFollowsTheSourceModeNotTheTexture(string source, int queue, bool hasCutoff,
            float cutoff, bool expected)
        {
            // A minivan-like OPAQUE source never counts, whatever alpha its texture carries.
            Assert.That(Call("MissesSourceAlpha", Mode(source), queue, hasCutoff, cutoff), Is.EqualTo(expected));
        }

        [TestCase("minivan", "part_0_0", true, "part_0_0")]
        [TestCase("tomato", "tomato_part_0_0", false, "part_0_0")]
        [TestCase("color_tv", "color_tv_part_1_0", false, "part_1_0")]
        [TestCase("tomato", "part_0_0", false, null)]
        [TestCase("tomato", "tomato_", false, null)]
        [TestCase("", "part_0_0", false, null)]
        [TestCase("tomato", "", true, null)]
        public void SceneMaterialNameTracesToTheSource(string stem, string name, bool embedded, string expected)
        {
            Assert.That(Call("SourceMaterialName", stem, name, embedded), Is.EqualTo(expected));
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
        public void MaterialsKeepModeTextureAndSides()
        {
            string json = "{\"materials\":[" +
                          "{\"name\":\"part_0_0\",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.00392156862745098," +
                          "\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}," +
                          "{\"name\":\"part_0_1\",\"alphaMode\":\"MASK\"}," +
                          "{\"name\":\"part_0_2\",\"alphaMode\":\"BLEND\",\"doubleSided\":true}," +
                          "{\"name\":\"part_1_0\"}]}";
            Array materials = ReadMaterials(json);
            Assert.That(materials.Length, Is.EqualTo(4));

            object tomatoLike = Call("Find", materials, "part_0_0");
            Assert.That(Read(tomatoLike, "Mode"), Is.EqualTo(Mode("Cutout")));
            Assert.That(Read(tomatoLike, "HasTexture"), Is.EqualTo(true));

            object untextured = Call("Find", materials, "part_0_1");
            Assert.That(Read(untextured, "HasTexture"), Is.EqualTo(false));

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

        private static string Part(string name, string alphaMode, bool textured)
        {
            return "{\"name\":\"" + name + "\"" +
                   (alphaMode == null ? "" : ",\"alphaMode\":\"" + alphaMode + "\"") +
                   (textured ? ",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}" : "") + "}";
        }

        private static string MaterialJson(string alphaMode, bool textured)
        {
            return "{\"materials\":[" + Part("part_0_0", alphaMode, textured) + "]}";
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
