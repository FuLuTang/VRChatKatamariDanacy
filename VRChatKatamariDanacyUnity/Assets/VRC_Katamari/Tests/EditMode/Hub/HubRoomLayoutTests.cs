using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the hub room layout (<c>Tools/hubart/hub_layout.json</c>, schema /3):
    /// surfaces (colour, smoothness, texture tiling), the textures' and panel sprites' import
    /// settings, and the refusals that name the entry; plus the committed file itself.
    /// </summary>
    /// <remarks>
    /// Reflection, like the other editor-code tests: <c>HubRoomLayout</c> and
    /// <c>HubTexturePostprocessor</c> live in <c>Assets/VRC_Katamari/Editor/Hub/</c>, which this
    /// assembly does not reference. JSON uses single quotes for readability.
    /// </remarks>
    public class HubRoomLayoutTests
    {
        private const string LayoutTypeName = "VRCKatamari.Editor.Hub.HubRoomLayout";
        private const string ImportTypeName = "VRCKatamari.Editor.Hub.HubTexturePostprocessor";

        private Type layoutType;

        [SetUp]
        public void SetUp()
        {
            layoutType = FindType(LayoutTypeName);
            Assert.IsNotNull(layoutType, LayoutTypeName + " not loaded");
        }

        [Test]
        public void ASurfaceWithATextureTilesOncePerItsMetres()
        {
            object layout = ParseOk(Minimal(
                "{'name':'Floor','rgb':[1,1,1],'smoothness':0.25,'texture':'Wood','metresPerTile':1.6}"));

            object floor = Entry(layout, "Surfaces", "Floor");
            Assert.AreEqual("Wood", Field(floor, "Texture"));
            Assert.AreEqual(0.25f, (float)Field(floor, "Smoothness"), 1e-6f);
            Vector2 tiling = (Vector2)Field(floor, "Tiling");
            Assert.AreEqual(0.625f, tiling.x, 1e-6f);
            Assert.AreEqual(0.625f, tiling.y, 1e-6f);
        }

        [Test]
        public void APlainColourSurfaceHasUnitTilingAndNoTexture()
        {
            object layout = ParseOk(Minimal("{'name':'Slate','rgb':[0.2,0.21,0.18],'smoothness':0.15}"));

            object slate = Entry(layout, "Surfaces", "Slate");
            Assert.AreEqual("", Field(slate, "Texture"));
            Assert.AreEqual(Vector2.one, (Vector2)Field(slate, "Tiling"));
            Color color = (Color)Field(slate, "Color");
            Assert.AreEqual(0.21f, color.g, 1e-6f);
        }

        [Test]
        public void ASurfaceNamingAnUnknownTextureOrNoTilingIsRefused()
        {
            AssertAnyContains(ParseFail(Minimal(
                "{'name':'Floor','rgb':[1,1,1],'texture':'Nope','metresPerTile':1}")), "'Nope'");
            AssertAnyContains(ParseFail(Minimal(
                "{'name':'Floor','rgb':[1,1,1],'texture':'Wood','metresPerTile':0}")), "metresPerTile");
        }

        [Test]
        public void ABoardFrameMustBeASurface()
        {
            List<string> errors = ParseFail(Minimal("{'name':'Frame','rgb':[1,1,1]}")
                .Replace("'frame':'Frame'", "'frame':'Hub_Pink'"));

            AssertAnyContains(errors, "Hub_Pink");
        }

        [Test]
        public void ATextureOutsideTheArtFolderOrWithAnOddSizeIsRefused()
        {
            AssertAnyContains(ParseFail(Minimal("{'name':'Frame','rgb':[1,1,1]}")
                .Replace("Assets/VRC_Katamari/Art/Hub/Textures/Wood.png", "Assets/Elsewhere/Wood.png")), "not a .png under");
            AssertAnyContains(ParseFail(Minimal("{'name':'Frame','rgb':[1,1,1]}")
                .Replace("'maxSize':1024", "'maxSize':1000")), "maxSize");
        }

        [Test]
        public void ASpriteNeedsAFourNumberBorder()
        {
            string sprites = "'sprites':[{'name':'Card','file':'Assets/VRC_Katamari/Art/Hub/Sprites/Card.png','maxSize':128}],";

            AssertAnyContains(ParseFail(Minimal("{'name':'Frame','rgb':[1,1,1]}", sprites)), "border[4]");
        }

        [Test]
        public void TheImportPlanRepeatsTexturesAndClampsSlicedSprites()
        {
            string sprites = "'sprites':[{'name':'Card','file':'Assets/VRC_Katamari/Art/Hub/Sprites/Card.png','maxSize':128,'border':[40,40,40,40]}],";
            object layout = ParseOk(Minimal("{'name':'Frame','rgb':[1,1,1]}", sprites));
            Type importType = FindType(ImportTypeName);
            Assert.IsNotNull(importType, ImportTypeName + " not loaded");
            MethodInfo settings = importType.GetMethod("Settings", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(settings, "HubTexturePostprocessor.Settings not found");

            object wood = settings.Invoke(null, new[] { FindByFile(layout, "Assets/VRC_Katamari/Art/Hub/Textures/Wood.png") });
            Assert.AreEqual(false, Field(wood, "Sprite"));
            Assert.AreEqual(true, Field(wood, "Mipmaps"));
            Assert.AreEqual(TextureWrapMode.Repeat, Field(wood, "Wrap"));
            Assert.AreEqual(1024, Field(wood, "MaxSize"));
            Assert.AreEqual(2, Field(wood, "AnisoLevel"));

            object card = settings.Invoke(null, new[] { FindByFile(layout, "Assets/VRC_Katamari/Art/Hub/Sprites/Card.png") });
            Assert.AreEqual(true, Field(card, "Sprite"));
            Assert.AreEqual(false, Field(card, "Mipmaps"));
            Assert.AreEqual(TextureWrapMode.Clamp, Field(card, "Wrap"));
            Assert.AreEqual(new Vector4(40, 40, 40, 40), Field(card, "Border"));

            Assert.IsNull(FindByFile(layout, "Assets/VRC_Katamari/Art/Hub/HubRoomArt.fbx"),
                "a file the layout does not name keeps its importer");
        }

        [Test]
        public void TheCommittedLayoutParsesAndEveryTextureAndSpriteFileExists()
        {
            string path = FindRepoFile("Tools/hubart/hub_layout.json");
            if (path == null) Assert.Ignore("Tools/hubart/hub_layout.json is not reachable from here");
            string unity = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)));
            unity = Path.Combine(unity, "VRChatKatamariDanacyUnity");

            object layout = ParseOk(File.ReadAllText(path), false);

            CollectionAssert.IsSubsetOf(new[] { "Levels", "Help", "Settings" }, Keys(layout, "Boards"));
            int textures = 0;
            foreach (DictionaryEntry pair in (IDictionary)Field(layout, "Textures"))
            {
                string file = (string)Field(pair.Value, "File");
                Assert.IsTrue(File.Exists(Path.Combine(unity, file)), file + " is missing");
                if (!(bool)Field(pair.Value, "IsSprite")) textures++;
            }
            Assert.AreEqual(4, textures, "the four room textures (wood, plaster, terrazzo, felt)");
            // Every textured surface's texture is a room texture, never a sprite (checked by the parser).
            Assert.GreaterOrEqual(Keys(layout, "Surfaces").Count, 4);
            // The six hub levels each have their own placeholder thumbnail colour.
            CollectionAssert.IsSubsetOf(
                new[] { "_ref_cancer", "story_01_bench_size", "physics_test_field", "terrain_contact_lab", "_ref_wlk_vs2", "_ref_vs00" },
                Keys(layout, "CardPreviewByLevel"));
        }

        [Test]
        public void AVersusCardShowsItsBadgeAndStripeAndAStoryCardItsNumber()
        {
            Type assembler = FindType("VRCKatamari.Editor.Hub.HubAssembler");
            Assert.IsNotNull(assembler, "HubAssembler not loaded");
            MethodInfo active = assembler.GetMethod("CardChildActive", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(active, "HubAssembler.CardChildActive not found");
            Func<string, bool, bool> on = (name, versus) => (bool)active.Invoke(null, new object[] { name, versus });

            foreach (string versusOnly in new[] { "VersusBadge", "VersusStrip", "VersusPill" })
            {
                Assert.IsTrue(on(versusOnly, true), versusOnly);
                Assert.IsFalse(on(versusOnly, false), versusOnly);
            }
            foreach (string storyOnly in new[] { "StoryStrip", "NumberDisc", "Number", "StartPill" })
            {
                Assert.IsFalse(on(storyOnly, true), storyOnly);
                Assert.IsTrue(on(storyOnly, false), storyOnly);
            }

            Assert.IsTrue(on("Name", true), "other card children are not the look's business");
        }

        [Test]
        public void ACardPreviewColourIsTheLevelsOwnElseItsModesDefault()
        {
            string previews = "'cardPreviews':{'story':[0.1,0.2,0.3],'versus':[0.4,0.5,0.6],"
                + "'levels':{'_ref_cancer':[0.8,0.7,0.6]}},";
            object layout = ParseOk(Minimal("{'name':'Frame','rgb':[1,1,1]}", previews));
            MethodInfo pick = layoutType.GetMethod("CardPreviewColor", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(pick, "HubRoomLayout.CardPreviewColor not found");
            Func<string, bool, Color> colour = (id, versus) => (Color)pick.Invoke(layout, new object[] { id, versus });

            Assert.AreEqual(0.8f, colour("_ref_cancer", false).r, 1e-6f);
            Assert.AreEqual(0.8f, colour("_ref_cancer", true).r, 1e-6f, "a level's own colour wins over the mode");
            Assert.AreEqual(0.1f, colour("lab", false).r, 1e-6f);
            Assert.AreEqual(0.4f, colour("vs", true).r, 1e-6f);
            Assert.AreEqual(0.1f, colour(null, false).r, 1e-6f);
        }

        [Test]
        public void OnlyOurHubSpritesCountAsTheNewPanelLook()
        {
            Type assembler = FindType("VRCKatamari.Editor.Hub.HubAssembler");
            MethodInfo isPanel = assembler.GetMethod("IsPanelSprite", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(isPanel, "HubAssembler.IsPanelSprite not found");
            Func<string, bool> ok = name => (bool)isPanel.Invoke(null, new object[] { name });

            Assert.IsTrue(ok("HubRounded"));
            Assert.IsTrue(ok("HubButtonPlain_Pressed"));
            Assert.IsFalse(ok("UISprite"), "Unity's built-in sprite is the old look");
            Assert.IsFalse(ok(""), "no sprite is the old look");
            Assert.IsFalse(ok(null));
        }

        // ------------------------------------------------------------------ helpers

        private static string Minimal(string surface, string sprites = "")
        {
            return ("{'schema':'katamari.hub-layout/3','canvasMetresPerUnit':0.001,'canvasDepthM':0.06,"
                + "'model':{'fbx':'Assets/X.fbx','rootName':'HubRoomArt'},'shader':'Standard',"
                + "'textures':[{'name':'Wood','file':'Assets/VRC_Katamari/Art/Hub/Textures/Wood.png','maxSize':1024,'anisoLevel':2}],"
                + sprites
                + "'surfaces':[" + surface + "],"
                + "'boards':["
                + "{'id':'Levels','center':[0,1.5,3.8],'yawDeg':0,'size':[6.4,2.7],'frame':'Frame'},"
                + "{'id':'Help','center':[-4.9,1.5,2.4],'yawDeg':-50,'size':[3.2,2.3],'frame':'Frame'},"
                + "{'id':'Settings','center':[4.9,1.5,2.4],'yawDeg':50,'size':[3.2,2.6],'frame':'Frame'}],"
                + "'lampsLocal':[[0,2.6,0.4]]}")
                .Replace("'frame':'Frame'", surface.Contains("'Frame'") ? "'frame':'Frame'" : "'frame':'" + FirstName(surface) + "'");
        }

        private static string FirstName(string surface)
        {
            int at = surface.IndexOf("'name':'", StringComparison.Ordinal) + 8;
            return surface.Substring(at, surface.IndexOf('\'', at) - at);
        }

        private object ParseOk(string json, bool quotes = true)
        {
            var errors = new List<string>();
            object[] args = { quotes ? json.Replace('\'', '"') : json, null, errors };
            bool ok = (bool)Invoke("TryParse", args);
            Assert.IsTrue(ok, "expected the layout to parse, errors: " + string.Join(" | ", errors.ToArray()));
            Assert.IsNotNull(args[1]);
            return args[1];
        }

        private List<string> ParseFail(string json)
        {
            var errors = new List<string>();
            object[] args = { json.Replace('\'', '"'), null, errors };
            bool ok = (bool)Invoke("TryParse", args);
            Assert.IsFalse(ok, "expected the layout to be refused");
            Assert.Greater(errors.Count, 0, "a refusal must say why");
            return errors;
        }

        private object FindByFile(object layout, string file)
        {
            MethodInfo method = layoutType.GetMethod("FindTextureByFile", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(method, "HubRoomLayout.FindTextureByFile not found");
            return method.Invoke(layout, new object[] { file });
        }

        private object Invoke(string name, object[] args)
        {
            MethodInfo method = layoutType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "HubRoomLayout." + name + " not found");
            return method.Invoke(null, args);
        }

        private static object Entry(object layout, string dictionary, string key)
        {
            var map = (IDictionary)Field(layout, dictionary);
            Assert.IsTrue(map.Contains(key), dictionary + " has no '" + key + "'");
            return map[key];
        }

        private static List<string> Keys(object layout, string dictionary)
        {
            var keys = new List<string>();
            foreach (object k in ((IDictionary)Field(layout, dictionary)).Keys) keys.Add((string)k);
            return keys;
        }

        private static void AssertAnyContains(List<string> errors, string fragment)
        {
            for (int i = 0; i < errors.Count; i++)
            {
                if (errors[i].Contains(fragment)) return;
            }
            Assert.Fail("no error contains '" + fragment + "'. Errors: " + string.Join(" | ", errors.ToArray()));
        }

        private static object Field(object owner, string name)
        {
            Assert.IsNotNull(owner, "reading '" + name + "' of null");
            Type type = owner.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            FieldInfo field = type.GetField(name, flags);
            if (field != null) return field.GetValue(owner);
            PropertyInfo property = type.GetProperty(name, flags);
            Assert.IsNotNull(property, type.Name + "." + name + " not found");
            return property.GetValue(owner, null);
        }

        private static string FindRepoFile(string relative)
        {
            string current = Directory.GetCurrentDirectory();
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(current); i++)
            {
                string candidate = Path.Combine(current, relative);
                if (File.Exists(candidate)) return candidate;
                current = Path.GetDirectoryName(current);
            }
            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
