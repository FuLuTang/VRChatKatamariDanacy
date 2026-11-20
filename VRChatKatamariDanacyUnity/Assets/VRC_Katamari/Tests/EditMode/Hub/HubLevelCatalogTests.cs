using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the hub level list parser: schema <c>/0</c> (one level per scene)
    /// and <c>/1</c> (several levels, islands, hubs), the defaults an absent field must
    /// give, and the validation errors that name the entry and the field.
    /// </summary>
    /// <remarks>
    /// Reflection, like the other editor-code tests: <c>HubLevelCatalog</c> lives in
    /// <c>Assets/VRC_Katamari/Editor/Hub/</c> and this assembly references only
    /// <c>VRCKatamari.Runtime</c>. The JSON in the tests uses single quotes for readability.
    /// </remarks>
    public class HubLevelCatalogTests
    {
        private const string CatalogTypeName = "VRCKatamari.Editor.Hub.HubLevelCatalog";

        private Type catalogType;

        [SetUp]
        public void SetUp()
        {
            catalogType = FindType(CatalogTypeName);
            Assert.IsNotNull(catalogType, CatalogTypeName + " not loaded");
        }

        // ------------------------------------------------------------------ schema /0

        [Test]
        public void ASchemaZeroSingleLevelFileLoadsWithTodaysDefaults()
        {
            object catalog = ParseOk(
                "{'schema':'katamari.hub-levels/0','room':{'padSizeM':2.8},'levels':["
                + "{'id':'story','name':'Picnic','scenes':['StoryMode'],'content':'LevelContent/Props',"
                + "'entry':'LevelEntry','roundFromPlacement':false}]}");

            object level = Level(catalog, 0);
            Assert.AreEqual("story", Field(level, "id"));
            Assert.AreEqual("round", Field(level, "kind"));
            Assert.AreEqual(false, Field(level, "allowAbort"));
            Assert.AreEqual(true, Field(level, "stars"));
            Assert.IsNull(Field(level, "source"));
            Assert.IsNull(Field(level, "island"));
            Assert.IsNull(Field(level, "start"));
            Assert.IsNull(Field(level, "round"));
            Assert.AreEqual(0, ((Array)Field(catalog, "hubs")).Length);
            // Absent room fields keep their defaults.
            Assert.AreEqual(0.6f, (float)Field(Field(catalog, "room"), "padGapM"), 1e-6f);
        }

        [Test]
        public void ASchemaZeroFileWithoutAnEntryIsStillRefused()
        {
            List<string> errors = ParseFail(
                "{'schema':'katamari.hub-levels/0','levels':["
                + "{'id':'story','name':'Picnic','scenes':['StoryMode'],'content':'LevelContent'}]}");

            AssertAnyContains(errors, "has no entry path");
        }

        [Test]
        public void AnUnknownSchemaIsRefusedAndBothKnownOnesAreNamed()
        {
            List<string> errors = ParseFail("{'schema':'katamari.hub-levels/9','levels':[]}");

            AssertAnyContains(errors, "katamari.hub-levels/9");
            AssertAnyContains(errors, "katamari.hub-levels/0");
            AssertAnyContains(errors, "katamari.hub-levels/1");
        }

        [Test]
        public void UnknownFieldsAreIgnored()
        {
            object catalog = ParseOk(
                "{'schema':'katamari.hub-levels/1','futureTopLevel':{'a':[1,2]},'levels':["
                + "{'id':'story','name':'Picnic','scenes':['S'],'content':'C','entry':'E',"
                + "'futureField':true,'start':{'center':[1,2,3],'futureStart':1}}]}");

            object level = Level(catalog, 0);
            Assert.AreEqual("story", Field(level, "id"));
            CollectionAssert.AreEqual(new float[] { 1f, 2f, 3f },
                (float[])Field(Field(level, "start"), "center"));
        }

        [Test]
        public void InvalidJsonAndEmptyTextAreErrorsNotExceptions()
        {
            AssertAnyContains(ParseFail("{ not json"), "not valid JSON");
            AssertAnyContains(ParseFail(""), "not valid JSON");
            AssertAnyContains(ParseFail("[1,2]"), "empty");
        }

        // ------------------------------------------------------------------ schema /1

        [Test]
        public void AMultiLevelHubFileParsesEveryNewField()
        {
            object catalog = ParseOk(MultiLevelJson());

            Assert.AreEqual(2, ((Array)Field(catalog, "levels")).Length);
            object hub = Hub(catalog, 0);
            Assert.AreEqual("AllMapsHub", Field(hub, "id"));
            Assert.AreEqual("Assets/_RefUnlicensed/Scenes/AllMapsHub.unity", Field(hub, "scene"));
            Assert.AreEqual("Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity", Field(hub, "baseScene"));
            CollectionAssert.AreEqual(new[] { "_ref_cancer", "physics_test_field" },
                (string[])Field(hub, "levels"));

            object cancer = Level(catalog, 0);
            Assert.AreEqual("round", Field(cancer, "kind"));
            Assert.AreEqual(true, Field(cancer, "allowAbort"));
            Assert.AreEqual(false, Field(cancer, "stars"));
            object source = Field(cancer, "source");
            Assert.AreEqual("Assets/_RefUnlicensed/Scenes/RefCancer.unity", Field(source, "scene"));
            CollectionAssert.AreEqual(new[] { "LevelContent/_ref_cancer" }, (string[])Field(source, "takeRoots"));
            Assert.AreEqual(0, ((string[])Field(source, "leaveBehind")).Length);
            CollectionAssert.AreEqual(new float[] { 0f, 0f, 0f },
                (float[])Field(Field(cancer, "island"), "offset"));
            object start = Field(cancer, "start");
            CollectionAssert.AreEqual(new float[] { -5f, -1f, 2.5f }, (float[])Field(start, "center"));
            Assert.AreEqual(true, Field(start, "hasHeading"));
            Assert.AreEqual(0.222f, (float)Field(start, "diameterM"), 1e-6f);
            object round = Field(cancer, "round");
            Assert.AreEqual(true, Field(round, "hasKillPlaneY"));
            Assert.AreEqual(-4f, (float)Field(round, "killPlaneY"));
            Assert.AreEqual(420f, (float)Field(round, "timeLimitSeconds"));
            Assert.AreEqual(0.8f, (float)Field(round, "targetDiameterM"), 1e-6f);

            object field = Level(catalog, 1);
            Assert.AreEqual("freeRoam", Field(field, "kind"));
            Assert.AreEqual("LevelContent/SANDBOX", Field(field, "arenaFirst"));
            Assert.AreEqual(true, Field(field, "EntryOptional"));
        }

        [Test]
        public void TypeBParsesAnIndependentCountDefinition()
        {
            string level = "{'id':'crab','name':'Crab','scenes':['H'],'content':'C','entry':'E',"
                + "'round':{'clearType':'B','targetCount':10,'countKind':'crab',"
                + "'countDefinitions':['small_fiddler_crab','blue_crab'],'recordDiameterAndTotal':true}}";
            object round = Field(Level(ParseOk(Wrap("'levels':[" + level + "]")), 0), "round");
            Assert.AreEqual("B", Field(round, "clearType").ToString());
            Assert.AreEqual(10, Field(round, "targetCount"));
            Assert.AreEqual("crab", Field(round, "countKind"));
            CollectionAssert.AreEqual(new[] { "small_fiddler_crab", "blue_crab" },
                (string[])Field(round, "countDefinitions"));
            Assert.AreEqual(true, Field(round, "recordDiameterAndTotal"));
            Assert.AreEqual(false, Field(round, "hasTargetDiameter"));
        }

        [TestCase("'clearType':'N'", "clearType")]
        [TestCase("'clearType':'B','targetCount':9.5", "targetCount")]
        [TestCase("'clearType':'B','targetCount':10,'countDefinitions':['crab']", "countKind")]
        [TestCase("'clearType':'B','targetCount':10,'countKind':'crab','countDefinitions':['x','x']", "duplicate def")]
        public void InvalidClearDefinitionsAreRejected(string rule, string error)
        {
            string level = "{'id':'lab','name':'Lab','scenes':['H'],'content':'C','entry':'E','round':{" + rule + "}}";
            AssertAnyContains(ParseFail(Wrap("'levels':[" + level + "]")), error);
        }

        [Test]
        public void AStartBlockWithOnlyACenterHasNoHeadingNoDiameterNoRound()
        {
            object catalog = ParseOk(MultiLevelJson().Replace("'headingDeg':0,", ""));

            object start = Field(Level(catalog, 0), "start");
            Assert.AreEqual(false, Field(start, "hasHeading"));
        }

        [Test]
        public void ALevelWithoutAStartOrRoundBlockHasNone()
        {
            object catalog = ParseOk(MultiLevelJson());

            object field = Level(catalog, 1);
            Assert.IsNull(Field(field, "round"));
            Assert.IsNull(Field(field, "start"));
        }

        [Test]
        public void AFreeRoamLevelMayOmitItsEntryButARoundLevelMayNot()
        {
            string free = "{'id':'lab','name':'Lab','scenes':['H'],'content':'LevelContent','kind':'freeRoam'}";
            ParseOk(Wrap("'levels':[" + free + "]"));

            string round = "{'id':'lab','name':'Lab','scenes':['H'],'content':'LevelContent','kind':'round'}";
            AssertAnyContains(ParseFail(Wrap("'levels':[" + round + "]")), "has no entry path");
        }

        [Test]
        public void AnUnknownKindIsAnErrorNamingTheLevel()
        {
            string level = "{'id':'lab','name':'Lab','scenes':['H'],'content':'C','entry':'E','kind':'sandbox'}";

            List<string> errors = ParseFail(Wrap("'levels':[" + level + "]"));

            AssertAnyContains(errors, "'lab'");
            AssertAnyContains(errors, "kind");
        }

        [Test]
        public void AnIslandOffsetWithNonZeroYIsAnError()
        {
            List<string> errors = ParseFail(MultiLevelJson().Replace("'offset':[0,0,0]", "'offset':[0,3,0]"));

            AssertAnyContains(errors, "island.offset");
            AssertAnyContains(errors, "Y must be 0");
        }

        [Test]
        public void AnIslandOffsetThatIsNotThreeNumbersIsAnError()
        {
            List<string> errors = ParseFail(MultiLevelJson().Replace("'offset':[0,0,0]", "'offset':[0,0]"));

            AssertAnyContains(errors, "three numbers");
        }

        [Test]
        public void ATakeRootThatIsNotAHierarchyPathIsAnError()
        {
            List<string> errors = ParseFail(MultiLevelJson().Replace("'LevelContent/_ref_cancer'", "'/LevelContent'"));

            AssertAnyContains(errors, "takeRoots[0]");
        }

        [Test]
        public void ASourceWithNothingToTakeIsAnError()
        {
            List<string> errors = ParseFail(MultiLevelJson().Replace("'takeRoots':['LevelContent/_ref_cancer']",
                "'takeRoots':[]"));

            AssertAnyContains(errors, "takeRoots");
        }

        [Test]
        public void AStartDiameterMustBePositive()
        {
            List<string> errors = ParseFail(MultiLevelJson().Replace("'diameterM':0.222", "'diameterM':0"));

            AssertAnyContains(errors, "start.diameterM");
        }

        [Test]
        public void ARoundBlockNumbersMustBePositive()
        {
            AssertAnyContains(ParseFail(MultiLevelJson().Replace("'timeLimitSeconds':420", "'timeLimitSeconds':0")),
                "timeLimitSeconds");
            AssertAnyContains(ParseFail(MultiLevelJson().Replace("'targetDiameterM':0.8", "'targetDiameterM':-1")),
                "targetDiameterM");
        }

        // ------------------------------------------------------------------ hubs

        [Test]
        public void AHubNamingAnUnknownLevelIsAnError()
        {
            List<string> errors = ParseFail(MultiLevelJson().Replace("'levels':['_ref_cancer','physics_test_field']",
                "'levels':['_ref_cancer','nope']"));

            AssertAnyContains(errors, "'nope'");
        }

        [Test]
        public void AHubLevelThatDoesNotListTheHubSceneIsAnError()
        {
            List<string> errors = ParseFail(MultiLevelJson().Replace("'scenes':['RefCancer','AllMapsHub']",
                "'scenes':['RefCancer']"));

            AssertAnyContains(errors, "AllMapsHub");
            AssertAnyContains(errors, "scenes");
        }

        [Test]
        public void AHubLevelWithoutASourceOrIslandIsAnError()
        {
            string bare = MultiLevelJson().Replace("'source':{'scene':'Assets/VRC_Katamari/Labs/PhysicsTestField/PhysicsTestField.unity','takeRoots':['LevelContent']},", "");

            AssertAnyContains(ParseFail(bare), "no source block");
        }

        [Test]
        public void TwoHubLevelsAtTheSameOffsetAreAnError()
        {
            List<string> errors = ParseFail(MultiLevelJson().Replace("'offset':[-1200,0,0]", "'offset':[0,0,0]"));

            AssertAnyContains(errors, "share the island offset");
        }

        [Test]
        public void ARepeatedLevelIdIsAnError()
        {
            string level = "{'id':'a','name':'A','scenes':['S'],'content':'C','entry':'E'}";

            AssertAnyContains(ParseFail(Wrap("'levels':[" + level + "," + level + "]")), "repeats the id 'a'");
        }

        // ------------------------------------------------------------------ the real list

        [Test]
        public void TheCommittedListParsesAndHasTheFourMapHub()
        {
            string path = FindRepoFile("Docs/Design/StoryMode/hub_levels.json");
            if (path == null) Assert.Ignore("Docs/Design/StoryMode/hub_levels.json is not reachable from here");

            object catalog = ParseOk(File.ReadAllText(path), false);

            Assert.AreEqual(1, ((Array)Field(catalog, "hubs")).Length);
            object hub = Hub(catalog, 0);
            CollectionAssert.AreEqual(
                new[] { "_ref_cancer", "story_01_bench_size", "physics_test_field", "terrain_contact_lab" },
                (string[])Field(hub, "levels"));
            Assert.AreEqual(4, ((Array)Field(catalog, "levels")).Length);
        }

        // ------------------------------------------------------------------ helpers

        private static string MultiLevelJson()
        {
            return "{'schema':'katamari.hub-levels/1',"
                + "'hubs':[{'id':'AllMapsHub','scene':'Assets/_RefUnlicensed/Scenes/AllMapsHub.unity',"
                + "'base':'Assets/VRC_Katamari/Worlds/StoryMode/StoryMode.unity',"
                + "'levels':['_ref_cancer','physics_test_field']}],"
                + "'levels':["
                + "{'id':'_ref_cancer','name':'Cancer','scenes':['RefCancer','AllMapsHub'],"
                + "'content':'LevelContent/_ref_cancer','entry':'LevelContent/_ref_cancer/Markers/PlayerSpawn',"
                + "'roundFromPlacement':true,'kind':'round','allowAbort':true,'stars':false,"
                + "'source':{'scene':'Assets/_RefUnlicensed/Scenes/RefCancer.unity','takeRoots':['LevelContent/_ref_cancer']},"
                + "'island':{'offset':[0,0,0]},"
                + "'start':{'center':[-5.0,-1.0,2.5],'headingDeg':0,'diameterM':0.222},"
                + "'round':{'killPlaneY':-4,'timeLimitSeconds':420,'targetDiameterM':0.8}},"
                + "{'id':'physics_test_field','name':'Map 0','scenes':['AllMapsHub'],'content':'LevelContent',"
                + "'kind':'freeRoam','allowAbort':true,"
                + "'source':{'scene':'Assets/VRC_Katamari/Labs/PhysicsTestField/PhysicsTestField.unity','takeRoots':['LevelContent']},"
                + "'island':{'offset':[-1200,0,0]},'arenaFirst':'LevelContent/SANDBOX'}"
                + "]}";
        }

        private static string Wrap(string levelsMember)
        {
            return "{'schema':'katamari.hub-levels/1'," + levelsMember + "}";
        }

        private object ParseOk(string json)
        {
            return ParseOk(json, true);
        }

        private object ParseOk(string json, bool quotes)
        {
            var errors = new List<string>();
            object[] args = { quotes ? json.Replace('\'', '"') : json, null, errors };
            bool ok = (bool)Invoke("TryParse", args);
            Assert.IsTrue(ok, "expected the list to parse, errors: " + string.Join(" | ", errors.ToArray()));
            Assert.AreEqual(0, errors.Count);
            Assert.IsNotNull(args[1]);
            return args[1];
        }

        private List<string> ParseFail(string json)
        {
            var errors = new List<string>();
            object[] args = { json.Replace('\'', '"'), null, errors };
            bool ok = (bool)Invoke("TryParse", args);
            Assert.IsFalse(ok, "expected the list to be refused");
            Assert.IsNull(args[1]);
            Assert.Greater(errors.Count, 0, "a refusal must say why");
            return errors;
        }

        private static void AssertAnyContains(List<string> errors, string fragment)
        {
            for (int i = 0; i < errors.Count; i++)
            {
                if (errors[i].Contains(fragment)) return;
            }
            Assert.Fail("no error contains '" + fragment + "'. Errors: " + string.Join(" | ", errors.ToArray()));
        }

        private object Invoke(string name, object[] args)
        {
            MethodInfo method = catalogType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "HubLevelCatalog." + name + " not found");
            return method.Invoke(null, args);
        }

        private static object Level(object catalog, int index)
        {
            return ((IList)Field(catalog, "levels"))[index];
        }

        private static object Hub(object catalog, int index)
        {
            return ((IList)Field(catalog, "hubs"))[index];
        }

        // A public field or property (the catalogue's DTOs use fields; EntryOptional is a property).
        private static object Field(object owner, string name)
        {
            Assert.IsNotNull(owner, "reading '" + name + "' of null");
            Type type = owner.GetType();
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(owner);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(property, type.Name + "." + name + " not found");
            return property.GetValue(owner, null);
        }

        private static string FindRepoFile(string relative)
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(directory); i++)
            {
                string candidate = Path.Combine(directory, relative);
                if (File.Exists(candidate)) return candidate;
                directory = Path.GetDirectoryName(directory);
            }
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
