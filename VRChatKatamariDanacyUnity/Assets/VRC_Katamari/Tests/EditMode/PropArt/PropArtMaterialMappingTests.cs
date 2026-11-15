using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the pure parts of the prop-material pipeline: manifest parsing (slots, linear
    /// colours, refusal rules), the deterministic material asset path, and the FBX-name to
    /// manifest-slot mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection, like <c>PickupFieldAuthoringRulesTests</c>: the code lives in
    /// <c>Assets/VRC_Katamari/Editor/PropArt/</c> and this test assembly references only
    /// <c>VRCKatamari.Runtime</c> (see the EditMode asmdef), so it cannot name the editor
    /// types directly. Every method exercised here is a pure static function, which is why
    /// reflection is enough.
    /// </para>
    /// <para>
    /// NOT COVERED HERE: <c>AssetDatabase.CreateAsset</c>, the ModelImporter external object
    /// map, the reimport and the post-import renderer verification. Those need a real Unity
    /// import and an FBX, which an EditMode unit test cannot fabricate without adding fixture
    /// assets to the project. The operator acceptance run (README section 11) checks them:
    /// the Materials tab must show every slot remapped to
    /// <c>Materials/Prop_&lt;def&gt;_&lt;slot&gt;.mat</c>, and the Console must be free of
    /// <c>[prop-art]</c> errors.
    /// </para>
    /// <para>
    /// The colour expectations are the manifest's linear numbers copied by hand from
    /// <c>Docs/Art/OriginalModels/props/exports/acorn.manifest.json</c>, not computed by the
    /// code under test. The identity mapping is pinned because <c>prop_lib.py</c> names the
    /// Blender material <c>Prop_&lt;def&gt;_&lt;key&gt;</c> and the FBX stores that string.
    /// </para>
    /// </remarks>
    public class PropArtMaterialMappingTests
    {
        private const string ManifestTypeName = "VRCKatamari.Editor.PropArt.PropArtManifests";
        private const string MaterialsTypeName = "VRCKatamari.Editor.PropArt.PropArtMaterials";

        private const string AcornJson =
            "{\n" +
            "  \"id\": \"acorn\",\n" +
            "  \"materials\": [\"Prop_acorn_nut\", \"Prop_acorn_cap\"],\n" +
            "  \"material_base_colors\": {\n" +
            "    \"Prop_acorn_nut\": [0.36, 0.18, 0.06],\n" +
            "    \"Prop_acorn_cap\": [0.13, 0.06, 0.025]\n" +
            "  }\n" +
            "}";

        [Test]
        public void ManifestParsesDeclaredSlotsAndLinearColours()
        {
            string error;
            object manifest = Parse(AcornJson, "acorn", out error);

            Assert.IsNull(error);
            Assert.IsNotNull(manifest);
            CollectionAssert.AreEqual(
                new[] { "Prop_acorn_nut", "Prop_acorn_cap" },
                Slots(manifest));

            Color[] colors = Colors(manifest);
            Assert.AreEqual(2, colors.Length);

            // The manifest stores Blender's linear Principled Base Color; the tool must copy
            // it verbatim (the existing hand-made materials do). No sRGB conversion here.
            Assert.AreEqual(0.36f, colors[0].r, 1e-6f);
            Assert.AreEqual(0.18f, colors[0].g, 1e-6f);
            Assert.AreEqual(0.06f, colors[0].b, 1e-6f);
            Assert.AreEqual(1f, colors[0].a, 1e-6f);

            Assert.AreEqual(0.13f, colors[1].r, 1e-6f);
            Assert.AreEqual(0.06f, colors[1].g, 1e-6f);
            Assert.AreEqual(0.025f, colors[1].b, 1e-6f);
        }

        [Test]
        public void ManifestRefusesADifferentDefFolder()
        {
            string error;
            object manifest = Parse(AcornJson, "apple", out error);

            Assert.IsNull(manifest);
            StringAssert.Contains("does not match", error);
        }

        [Test]
        public void ManifestRefusesMissingOrExtraColourKeys()
        {
            string missing =
                "{\"id\":\"acorn\",\"materials\":[\"Prop_acorn_nut\"]," +
                "\"material_base_colors\":{}}";
            string error;
            Assert.IsNull(Parse(missing, "acorn", out error));
            StringAssert.Contains("no 3-number entry", error);

            // A colour for a material that is not declared means the two lists disagree; the
            // tool refuses instead of silently dropping the extra entry.
            string extra =
                "{\"id\":\"acorn\",\"materials\":[\"Prop_acorn_nut\"]," +
                "\"material_base_colors\":{\"Prop_acorn_nut\":[0.36,0.18,0.06]," +
                "\"Prop_acorn_cap\":[0.13,0.06,0.025]}}";
            Assert.IsNull(Parse(extra, "acorn", out error));
            StringAssert.Contains("not in 'materials'", error);
        }

        [Test]
        public void ManifestRefusesDuplicateSlotsAndOutOfRangeChannels()
        {
            string duplicate =
                "{\"id\":\"acorn\",\"materials\":[\"Prop_acorn_nut\",\"Prop_acorn_nut\"]," +
                "\"material_base_colors\":{\"Prop_acorn_nut\":[0.36,0.18,0.06]}}";
            string error;
            Assert.IsNull(Parse(duplicate, "acorn", out error));
            StringAssert.Contains("twice", error);

            string outOfRange =
                "{\"id\":\"acorn\",\"materials\":[\"Prop_acorn_nut\"]," +
                "\"material_base_colors\":{\"Prop_acorn_nut\":[1.2,0.18,0.06]}}";
            Assert.IsNull(Parse(outOfRange, "acorn", out error));
            StringAssert.Contains("outside [0, 1]", error);

            string malformed =
                "{\"id\":\"acorn\",\"materials\":[\"Prop_acorn_nut\"]," +
                "\"material_base_colors\":{\"Prop_acorn_nut\":[0.36,0.18]}}";
            Assert.IsNull(Parse(malformed, "acorn", out error));
            StringAssert.Contains("no 3-number entry", error);
        }

        [Test]
        public void MaterialAssetPathIsDerivedOnlyFromDefAndSlot()
        {
            Assert.AreEqual(
                "Assets/VRC_Katamari/Art/Props/acorn/Materials/Prop_acorn_nut.mat",
                MaterialAssetPath("acorn", "Prop_acorn_nut"));
            Assert.AreEqual(
                "Assets/VRC_Katamari/Art/Props/watering_can/Materials/Prop_watering_can_enamel.mat",
                MaterialAssetPath("watering_can", "Prop_watering_can_enamel"));
        }

        [Test]
        public void SlotMappingAcceptsTheIdentityAndRefusesAmbiguity()
        {
            string[] declared = { "Prop_acorn_nut", "Prop_acorn_cap" };

            Assert.AreEqual("Prop_acorn_nut", ResolveSlot("Prop_acorn_nut", "acorn", declared));
            Assert.AreEqual("Prop_acorn_cap", ResolveSlot("Prop_acorn_cap", "acorn", declared));

            // Hand-edit tolerance: case, Blender's duplicate suffix, and a bare key.
            Assert.AreEqual("Prop_acorn_nut", ResolveSlot("prop_ACORN_nut", "acorn", declared));
            Assert.AreEqual("Prop_acorn_nut", ResolveSlot("Prop_acorn_nut.001", "acorn", declared));
            Assert.AreEqual("Prop_acorn_nut", ResolveSlot("nut", "acorn", declared));

            Assert.IsNull(ResolveSlot("Prop_apple_skin", "acorn", declared));
            Assert.IsNull(ResolveSlot("", "acorn", declared));

            // Two declared names end with "_b"; guessing one would paint the wrong slot.
            string[] ambiguous = { "Prop_x_a_b", "Prop_x_b" };
            Assert.IsNull(ResolveSlot("b", "other", ambiguous));
        }

        [Test]
        public void PropPathsAreRecognizedOnlyBelowThePropsRoot()
        {
            Assert.AreEqual("acorn",
                DefFromFbx("Assets/VRC_Katamari/Art/Props/acorn/acorn.fbx"));
            Assert.AreEqual("watering_can",
                DefFromFbx("Assets/VRC_Katamari/Art/Props/watering_can/watering_can.fbx"));

            // A differently named FBX in the def folder is not the prop model the placement
            // importer loads, so the postprocessor must not touch it.
            Assert.IsNull(DefFromFbx("Assets/VRC_Katamari/Art/Props/acorn/preview.fbx"));
            Assert.IsNull(DefFromFbx("Assets/VRC_Katamari/Art/Props/acorn/Materials/acorn.fbx"));
            Assert.IsNull(DefFromFbx("Assets/Other/acorn/acorn.fbx"));
            Assert.IsNull(DefFromFbx("Assets/VRC_Katamari/Art/Props/acorn/acorn.manifest.json"));

            Assert.AreEqual("acorn",
                DefFromManifest("Assets/VRC_Katamari/Art/Props/acorn/acorn.manifest.json"));
            Assert.IsNull(DefFromManifest("Assets/VRC_Katamari/Art/Props/acorn/other.manifest.json"));
        }

        // ------------------------------------------------------------- reflection ---

        private static Type FindType(string typeName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type found = assemblies[i].GetType(typeName, false);
                if (found != null)
                {
                    return found;
                }
            }

            Assert.Fail("type '" + typeName + "' is not loaded. It lives in " +
                        "Assets/VRC_Katamari/Editor/PropArt and is expected to compile into " +
                        "the editor assembly.");
            return null;
        }

        private static object Parse(string json, string expectedDef, out string error)
        {
            MethodInfo method = FindType(ManifestTypeName).GetMethod(
                "TryParse", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, ManifestTypeName + ".TryParse is missing");

            object[] arguments = { json, expectedDef, null, null };
            bool accepted = (bool)method.Invoke(null, arguments);
            error = arguments[3] as string;
            return accepted ? arguments[2] : null;
        }

        private static string[] Slots(object manifest)
        {
            Assert.IsNotNull(manifest);
            FieldInfo field = manifest.GetType().GetField("Slots", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, "PropArtManifest.Slots is missing");
            return (string[])field.GetValue(manifest);
        }

        private static Color[] Colors(object manifest)
        {
            Assert.IsNotNull(manifest);
            FieldInfo field = manifest.GetType().GetField("BaseColors", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, "PropArtManifest.BaseColors is missing");
            return (Color[])field.GetValue(manifest);
        }

        private static string DefFromFbx(string assetPath)
        {
            MethodInfo method = FindType(MaterialsTypeName).GetMethod(
                "TryGetDefFromFbxPath", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, MaterialsTypeName + ".TryGetDefFromFbxPath is missing");

            object[] arguments = { assetPath, null };
            bool recognized = (bool)method.Invoke(null, arguments);
            return recognized ? arguments[1] as string : null;
        }

        private static string DefFromManifest(string assetPath)
        {
            MethodInfo method = FindType(MaterialsTypeName).GetMethod(
                "TryGetDefFromManifestPath", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, MaterialsTypeName + ".TryGetDefFromManifestPath is missing");

            object[] arguments = { assetPath, null };
            bool recognized = (bool)method.Invoke(null, arguments);
            return recognized ? arguments[1] as string : null;
        }

        private static string MaterialAssetPath(string def, string slot)
        {
            MethodInfo method = FindType(MaterialsTypeName).GetMethod(
                "MaterialAssetPath", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, MaterialsTypeName + ".MaterialAssetPath is missing");
            return (string)method.Invoke(null, new object[] { def, slot });
        }

        private static string ResolveSlot(string importedName, string def, string[] declaredNames)
        {
            MethodInfo method = FindType(MaterialsTypeName).GetMethod(
                "TryResolveSlot", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, MaterialsTypeName + ".TryResolveSlot is missing");

            object[] arguments = { importedName, def, declaredNames, null };
            bool resolved = (bool)method.Invoke(null, arguments);
            return resolved ? arguments[3] as string : null;
        }
    }
}
