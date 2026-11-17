using System;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins when the importer wants a PropFallHost: only for a level with at least one parent edge
    /// between two props of the document. Real JSON through the real reader and
    /// <c>PlacementImporter.CountParentEdges</c> (reflection, like every importer test). The scene
    /// wiring itself (component creation, the three references, a second import writing nothing)
    /// needs Unity; see PROP_BEHAVIOURS 10.9.
    /// </summary>
    public class PlacementPropFallWiringTests
    {
        private static readonly string MetaJson =
            @"""meta"":{""id"":""probe"",""scene"":""ProbeScene"",""units"":""m""," +
            @"""convention"":""unity-y-up-left-handed-quat-xyzw""," +
            @"""ball"":{""startDiameterM"":0.3,""targetDiameterM"":2.2,""maxDiameterRef"":""global""," +
            @"""start"":[1,2,3],""spawnSource"":""authored""},""timeLimitS"":180," +
            @"""playerSpawn"":[4,5,6],""groundY"":-1.5,""bounds"":[[-10,-20],[10,20]]," +
            @"""spawnClearanceM"":0.5,""zones"":[{""id"":""z"",""code"":1,""kind"":""ground""}]," +
            @"""arena"":{""id"":""probe""}," +
            @"""caps"":{""props"":512,""staticSolids"":256}," +
            @"""budget"":{""planes"":8,""boxes"":64,""ramps"":4}," +
            @"""expected"":{""propCount"":0},""source"":{""kind"":""authored""}}";

        private const string Defs = @"""defs"":[{""id"":""crab"",""kind"":""k"",""model"":""x.fbx""}]";

        private static Type FindType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name);
                if (type != null) return type;
            }
            return null;
        }

        private static string Prop(string id, string body)
        {
            return @"{""itemId"":""" + id + @""",""def"":""crab"",""kind"":""k"",""position"":[0,0,0]," +
                   @"""rotationQuat"":[0,0,0,1],""aabb"":[1,0.4,1]" + (body == "" ? "" : "," + body) + "}";
        }

        private static int Edges(string props)
        {
            Type reader = FindType("VRCKatamari.Editor.LevelBuild.PlacementV2Reader");
            Type importer = FindType("VRCKatamari.Editor.LevelBuild.PlacementImporter");
            Assert.IsNotNull(reader);
            Assert.IsNotNull(importer);
            string json = @"{""schema"":""katamari.placement/2""," + MetaJson + "," + Defs +
                          @",""props"":[" + props + "]}";
            object result = reader.GetMethod("Read", new[] { typeof(string) }).Invoke(null, new object[] { json });
            object doc = result.GetType().GetField("Document").GetValue(result);
            Assert.IsNotNull(doc);
            return (int)importer.GetMethod("CountParentEdges").Invoke(null, new[] { doc });
        }

        [Test]
        public void NoParentColumnMeansNoEdges()
        {
            Assert.AreEqual(0, Edges(Prop("a", "") + "," + Prop("b", "")));
        }

        [Test]
        public void ParentsNamingAnotherPropAreCounted()
        {
            Assert.AreEqual(2, Edges(Prop("a", "") + "," + Prop("b", @"""parent"":""a""") + "," +
                                     Prop("c", @"""parent"":""b""")));
        }

        [Test]
        public void ParentWithoutAPropIsNotAnEdge()
        {
            Assert.AreEqual(0, Edges(Prop("a", @"""parent"":""ghost""")));
        }

        [Test]
        public void SelfParentIsNotAnEdge()
        {
            Assert.AreEqual(0, Edges(Prop("a", @"""parent"":""a""")));
        }
    }
}
