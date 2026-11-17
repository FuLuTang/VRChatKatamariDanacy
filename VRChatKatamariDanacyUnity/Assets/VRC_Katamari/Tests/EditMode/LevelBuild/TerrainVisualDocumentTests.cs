using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins <c>terrain.visual</c>'s engine-free reader (<c>TerrainVisualDocument</c> /
    /// <c>TerrainVisualSidecar</c>): the <c>.katvisual</c> codec against the SAME golden bytes
    /// the Python writer's test pins, and every consistency rule the importer relies on before it
    /// writes an asset (sha256, counts, part tiling, submesh coverage, material references,
    /// part-local indices, paths that cannot escape the level directory).
    /// </summary>
    /// <remarks>
    /// Reflection, like the other importer tests (this assembly references only the runtime).
    /// </remarks>
    public class TerrainVisualDocumentTests
    {
        private const string DocumentTypeName = "VRCKatamari.Editor.LevelBuild.TerrainVisualDocument";
        private const string SidecarTypeName = "VRCKatamari.Editor.LevelBuild.TerrainVisualSidecar";
        private const string ParserTypeName = "VRCKatamari.Editor.LevelBuild.LevelJsonParser";

        /// <summary>
        /// One triangle: positions (0,0,0)(1,0,0)(0,0,1), normals +Y, uvs (0,0)(1,0)(0,1),
        /// indices 0 1 2, one part, index width 2. Identical to the Python test's GOLDEN_HEX.
        /// </summary>
        private const string GoldenHex =
            "4b56495301000203030000000300000001000000000000000000000000000000" +
            "0000803f000000000000000000000000000000000000803f000000000000803f" +
            "00000000000000000000803f00000000000000000000803f0000000000000000" +
            "000000000000803f00000000000000000000803f000001000200";

        private Type documentType;
        private Type sidecarType;
        private string tempDir;

        [SetUp]
        public void SetUp()
        {
            documentType = FindType(DocumentTypeName);
            sidecarType = FindType(SidecarTypeName);
            Assert.IsNotNull(documentType, DocumentTypeName + " not loaded");
            Assert.IsNotNull(sidecarType, SidecarTypeName + " not loaded");
            tempDir = Path.Combine(Path.GetTempPath(), "terrain-visual-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }

        // ---------------------------------------------------------------- sidecar ---

        [Test]
        public void TheGoldenSidecarDecodesToTheSameTriangleAsPython()
        {
            object sidecar = ReadSidecar(Hex(GoldenHex), out string error);
            Assert.IsNull(error, error);
            Assert.AreEqual(2, Field(sidecar, "IndexWidth"));
            Assert.AreEqual(3, Field(sidecar, "VertexCount"));
            Assert.AreEqual(3, Field(sidecar, "IndexCount"));
            Assert.AreEqual(1, Field(sidecar, "PartCount"));
            CollectionAssert.AreEqual(new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f },
                (float[])Field(sidecar, "Positions"));
            CollectionAssert.AreEqual(new[] { 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f },
                (float[])Field(sidecar, "Normals"));
            CollectionAssert.AreEqual(new[] { 0f, 0f, 1f, 0f, 0f, 1f },
                (float[])Field(sidecar, "Uvs"));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, (int[])Field(sidecar, "Indices"));
        }

        [Test]
        public void ADamagedSidecarIsRefusedWithItsTag()
        {
            byte[] good = Hex(GoldenHex);
            byte[] badMagic = (byte[])good.Clone();
            badMagic[0] = (byte)'X';
            ReadSidecar(badMagic, out string error);
            StringAssert.StartsWith("[terrain_visual_sidecar]", error);

            ReadSidecar(Slice(good, good.Length - 1), out error);
            StringAssert.StartsWith("[terrain_visual_sidecar]", error, "truncated");

            byte[] trailing = new byte[good.Length + 1];
            Array.Copy(good, trailing, good.Length);
            ReadSidecar(trailing, out error);
            StringAssert.StartsWith("[terrain_visual_sidecar]", error, "trailing bytes");

            byte[] badVersion = (byte[])good.Clone();
            badVersion[4] = 9;
            ReadSidecar(badVersion, out error);
            StringAssert.StartsWith("[terrain_visual_sidecar]", error, "version");
        }

        // --------------------------------------------------------------- document ---

        [Test]
        public void AWellFormedBlockParsesIntoParts()
        {
            object document = Parse(Block(), out string error);
            Assert.IsNull(error, error);
            Assert.AreEqual("point", Field(document, "Filter"));
            object[] parts = Items(Field(document, "Parts"));
            Assert.AreEqual(1, parts.Length);
            Assert.AreEqual("stage", Field(parts[0], "Role"));
            Assert.AreEqual(3, Field(parts[0], "VertexCount"));
            object[] submeshes = Items(Field(parts[0], "Submeshes"));
            Assert.AreEqual("t000", Field(submeshes[0], "Material"));
            object[] materials = Items(Field(document, "Materials"));
            Assert.AreEqual("opaque", Field(materials[0], "Blend"));
        }

        [Test]
        public void EveryConsistencyRuleHasItsOwnTag()
        {
            AssertRefused(Block(filter: "trilinear"), "[terrain_visual]");
            AssertRefused(Block(blend: "additive"), "[terrain_visual_material]");
            AssertRefused(Block(submeshMaterial: "nope"), "[terrain_visual_material]");
            AssertRefused(Block(submeshFirst: 3), "[terrain_visual_part]");
            AssertRefused(Block(submeshCount: 6), "[terrain_visual_part]");
            AssertRefused(Block(role: "ground"), "[terrain_visual_part]");
            AssertRefused(Block(vertexRange: "[1,3]"), "[terrain_visual_part]");
            AssertRefused(Block(texture: "../escape.png"), "[terrain_visual_texture]");
            AssertRefused(Block(texture: "/abs.png"), "[terrain_visual_texture]");
            AssertRefused(Block(meshRefVertexCount: 9), "[terrain_visual_part]");
        }

        [Test]
        public void TheSidecarIsLoadedAndPinnedToTheDocument()
        {
            byte[] bytes = Hex(GoldenHex);
            string sha = (string)sidecarType.GetMethod("Sha256Hex",
                BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { bytes });
            Directory.CreateDirectory(Path.Combine(tempDir, "terrain"));
            File.WriteAllBytes(Path.Combine(tempDir, "terrain", "v.katvisual"), bytes);

            object document = Parse(Block(sha: sha), out string error);
            Assert.IsNull(error, error);
            object sidecar = TryLoad(document, out error);
            Assert.IsNull(error, error);
            Assert.AreEqual(3, Field(sidecar, "VertexCount"));

            object wrongSha = Parse(Block(sha: new string('0', 64)), out error);
            Assert.IsNull(error, error);
            TryLoad(wrongSha, out error);
            StringAssert.StartsWith("[terrain_visual_sidecar]", error);

            object wrongCount = Parse(Block(sha: sha, meshRefTriangleCount: 2), out error);
            Assert.IsNull(error, error);
            TryLoad(wrongCount, out error);
            StringAssert.StartsWith("[terrain_visual_sidecar]", error);
        }

        [Test]
        public void ATextureThatIsNotTheDeclaredFileIsRefused()
        {
            string relative = "terrain/textures/a.png";
            Directory.CreateDirectory(Path.Combine(tempDir, "terrain", "textures"));
            byte[] png = { 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(tempDir, relative), png);
            string sha = (string)sidecarType.GetMethod("Sha256Hex",
                BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { png });

            object document = Parse(Block(textureSha: sha), out string error);
            Assert.IsNull(error, error);
            object material = Items(Field(document, "Materials"))[0];
            string path = ResolveTexture(document, material, out error);
            Assert.IsNull(error, error);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(tempDir, relative)), path);

            object tampered = Parse(Block(textureSha: new string('1', 64)), out error);
            ResolveTexture(tampered, Items(Field(tampered, "Materials"))[0], out error);
            StringAssert.StartsWith("[terrain_visual_texture]", error);
        }

        // --------------------------------------------------------------- fixtures ---

        private static string Block(string filter = "point", string blend = "opaque",
            string submeshMaterial = "t000", int submeshFirst = 0, int submeshCount = 3,
            string role = "stage", string vertexRange = "[0,3]",
            string texture = "terrain/textures/a.png", int meshRefVertexCount = 3,
            int meshRefTriangleCount = 1, string sha = "00", string textureSha = "00")
        {
            return @"{""meshRef"":{""path"":""terrain/v.katvisual"",""sha256"":""" + sha + @"""," +
                   @"""vertexCount"":" + meshRefVertexCount + @",""indexCount"":3," +
                   @"""triangleCount"":" + meshRefTriangleCount + @",""indexWidth"":2," +
                   @"""normals"":true,""uvs"":true}," +
                   @"""parts"":[{""id"":""p0"",""role"":""" + role + @"""," +
                   @"""vertexRange"":" + vertexRange + @",""indexRange"":[0,3]," +
                   @"""bounds"":{""min"":[0,0,0],""max"":[1,0,1]},""submeshes"":[{" +
                   @"""material"":""" + submeshMaterial + @""",""firstIndex"":" + submeshFirst +
                   @",""indexCount"":" + submeshCount + @"}]}]," +
                   @"""materials"":[{""id"":""t000"",""texture"":""" + texture + @"""," +
                   @"""sha256"":""" + textureSha + @""",""width"":4,""height"":4," +
                   @"""blend"":""" + blend + @"""}],""filter"":""" + filter + @"""}";
        }

        private void AssertRefused(string json, string tag)
        {
            Parse(json, out string error);
            Assert.IsNotNull(error, "expected a refusal carrying " + tag);
            StringAssert.StartsWith(tag, error);
        }

        private object Parse(string json, out string error)
        {
            Type parser = FindType(ParserTypeName);
            object node = parser.GetMethod("ParseText", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { json });
            object[] args = { node, null, null };
            MethodInfo method = documentType.GetMethod("TryParse",
                BindingFlags.Public | BindingFlags.Static);
            bool ok = (bool)method.Invoke(null, args);
            error = (string)args[2];
            return ok ? args[1] : null;
        }

        private object TryLoad(object document, out string error)
        {
            object[] args = { tempDir, null, null };
            MethodInfo method = documentType.GetMethod("TryLoadSidecar");
            bool ok = (bool)method.Invoke(document, args);
            error = (string)args[2];
            return ok ? args[1] : null;
        }

        private string ResolveTexture(object document, object material, out string error)
        {
            object[] args = { tempDir, material, null, null };
            MethodInfo method = documentType.GetMethod("TryResolveTexture");
            bool ok = (bool)method.Invoke(document, args);
            error = (string)args[3];
            return ok ? (string)args[2] : null;
        }

        private object ReadSidecar(byte[] bytes, out string error)
        {
            object[] args = { bytes, null, null };
            MethodInfo method = sidecarType.GetMethod("TryRead",
                BindingFlags.Public | BindingFlags.Static);
            bool ok = (bool)method.Invoke(null, args);
            error = (string)args[2];
            return ok ? args[1] : null;
        }

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            return bytes;
        }

        private static byte[] Slice(byte[] bytes, int length)
        {
            var result = new byte[length];
            Array.Copy(bytes, result, length);
            return result;
        }

        private static object[] Items(object array)
        {
            var list = (System.Collections.IEnumerable)array;
            var items = new System.Collections.Generic.List<object>();
            foreach (object item in list) items.Add(item);
            return items.ToArray();
        }

        private static object Field(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.Instance);
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
