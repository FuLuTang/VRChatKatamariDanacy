using System;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the island arithmetic of the all-maps hub: offset legality (Y must
    /// be 0), adding an offset to points and flat point lists, the gap between two islands'
    /// boxes, the coordinate-range and minimum-gap checks, and the report number format.
    /// </summary>
    /// <remarks>
    /// Reflection, like the other editor-code tests: <c>IslandMath</c> lives in
    /// <c>Assets/VRC_Katamari/Editor/Hub/</c> and this assembly references only
    /// <c>VRCKatamari.Runtime</c>. Expected numbers are hand-computed.
    /// </remarks>
    public class IslandMathTests
    {
        private const string MathTypeName = "VRCKatamari.Editor.Hub.IslandMath";

        private Type mathType;

        [SetUp]
        public void SetUp()
        {
            mathType = FindType(MathTypeName);
            Assert.IsNotNull(mathType, MathTypeName + " not loaded");
        }

        // ---- offset legality ----

        [Test]
        public void AnOffsetWithZeroYIsValid()
        {
            object[] args = { new float[] { -1200f, 0f, 1200f }, null };
            Assert.IsTrue((bool)Invoke("IsOffsetValid", args));
            Assert.IsNull(args[1]);
        }

        [Test]
        public void AnOffsetWithNonZeroYIsRefusedAndTheReasonSaysWhy()
        {
            object[] args = { new float[] { 0f, 5f, 0f }, null };
            Assert.IsFalse((bool)Invoke("IsOffsetValid", args));
            StringAssert.Contains("Y must be 0", (string)args[1]);
        }

        [Test]
        public void AnOffsetThatIsNotThreeFiniteNumbersIsRefused()
        {
            Assert.IsFalse((bool)Invoke("IsOffsetValid", new object[] { null, null }));
            Assert.IsFalse((bool)Invoke("IsOffsetValid", new object[] { new float[] { 1f, 0f }, null }));
            Assert.IsFalse((bool)Invoke("IsOffsetValid",
                new object[] { new float[] { float.NaN, 0f, 0f }, null }));
            Assert.IsFalse((bool)Invoke("IsOffsetValid",
                new object[] { new float[] { 0f, 0f, float.PositiveInfinity }, null }));
        }

        // ---- adding an offset ----

        [Test]
        public void AddIsComponentWiseAndDoesNotChangeItsInputs()
        {
            var a = new float[] { 1f, 2f, 3f };
            var b = new float[] { 10f, 0f, -30f };

            var sum = (float[])Invoke("Add", new object[] { a, b });

            CollectionAssert.AreEqual(new float[] { 11f, 2f, -27f }, sum);
            CollectionAssert.AreEqual(new float[] { 1f, 2f, 3f }, a);
            CollectionAssert.AreEqual(new float[] { 10f, 0f, -30f }, b);
        }

        [Test]
        public void AddOfAMalformedVectorIsNull()
        {
            Assert.IsNull(Invoke("Add", new object[] { new float[] { 1f, 2f }, new float[] { 1f, 2f, 3f } }));
            Assert.IsNull(Invoke("Add", new object[] { null, new float[] { 1f, 2f, 3f } }));
        }

        [Test]
        public void TranslateTriplesShiftsEveryTripleOfAFlatList()
        {
            // Two path points of a motion path: (1,2,3) and (4,5,6), island at (100, 0, -200).
            var flat = new float[] { 1f, 2f, 3f, 4f, 5f, 6f };

            int count = (int)Invoke("TranslateTriples",
                new object[] { flat, new float[] { 100f, 0f, -200f } });

            Assert.AreEqual(2, count);
            CollectionAssert.AreEqual(new float[] { 101f, 2f, -197f, 104f, 5f, -194f }, flat);
        }

        [Test]
        public void TranslateTriplesRefusesAListThatEndsMidTripleAndChangesNothing()
        {
            var flat = new float[] { 1f, 2f, 3f, 4f };

            int count = (int)Invoke("TranslateTriples",
                new object[] { flat, new float[] { 100f, 0f, 0f } });

            Assert.AreEqual(-1, count);
            CollectionAssert.AreEqual(new float[] { 1f, 2f, 3f, 4f }, flat);
        }

        [Test]
        public void TranslateTriplesOfAnEmptyListMovesNothing()
        {
            Assert.AreEqual(0, (int)Invoke("TranslateTriples",
                new object[] { new float[0], new float[] { 1f, 0f, 1f } }));
        }

        [Test]
        public void TranslateBoxMovesBothCorners()
        {
            object[] args =
            {
                new float[] { -20f, 0f, -20f }, new float[] { 4f, 9f, 4f },
                new float[] { 0f, 0f, -1200f }, null, null,
            };

            Assert.IsTrue((bool)Invoke("TranslateBox", args));

            CollectionAssert.AreEqual(new float[] { -20f, 0f, -1220f }, (float[])args[3]);
            CollectionAssert.AreEqual(new float[] { 4f, 9f, -1196f }, (float[])args[4]);
        }

        // ---- gap between islands ----

        [Test]
        public void TheGapBetweenTwoSeparatedBoxesIsTheShortestSegment()
        {
            // Box A is [0,10]^3; box B starts 3 m past A on x and 4 m past A on z (y overlaps):
            // the shortest segment is the hypotenuse of 3 and 4.
            float gap = (float)Invoke("BoxGap", new object[]
            {
                new float[] { 0f, 0f, 0f }, new float[] { 10f, 10f, 10f },
                new float[] { 13f, 2f, 14f }, new float[] { 20f, 8f, 20f },
            });

            Assert.AreEqual(5f, gap, 1e-5f);
        }

        [Test]
        public void TouchingOrOverlappingBoxesHaveZeroGap()
        {
            var a0 = new float[] { 0f, 0f, 0f };
            var a1 = new float[] { 10f, 10f, 10f };

            Assert.AreEqual(0f, (float)Invoke("BoxGap",
                new object[] { a0, a1, new float[] { 10f, 0f, 0f }, new float[] { 20f, 10f, 10f } }));
            Assert.AreEqual(0f, (float)Invoke("BoxGap",
                new object[] { a0, a1, new float[] { 5f, 5f, 5f }, new float[] { 6f, 6f, 6f } }));
        }

        [Test]
        public void AnInvalidBoxHasNegativeGap()
        {
            // min above max on x
            float gap = (float)Invoke("BoxGap", new object[]
            {
                new float[] { 5f, 0f, 0f }, new float[] { 1f, 1f, 1f },
                new float[] { 0f, 0f, 0f }, new float[] { 1f, 1f, 1f },
            });
            Assert.Less(gap, 0f);
        }

        [Test]
        public void TheCurrentFourIslandsAreFarEnoughApartFromTheHubRoom()
        {
            // Authored bounds (R47 inventory) moved by the list's offsets; the hub room is at
            // x 1192..1208. Cancer at the origin, picnic at z -1200, physics field at x -1200,
            // terrain lab at z +1200.
            float[][] mins =
            {
                new[] { -26.4f, -3f, -25.2f },        // cancer
                new[] { -20f, 0f, -1220f },           // picnic
                new[] { -1216f, 0f, -42f },           // physics test field
                new[] { -40f, -1.8f, 1160f },         // terrain lab
                new[] { 1192f, 0f, -8f },             // hub room
            };
            float[][] maxs =
            {
                new[] { 32.7f, 14f, 47.9f },
                new[] { 4f, 9f, -1196f },
                new[] { -1184f, 2f, 19f },
                new[] { 40f, 1.2f, 1240f },
                new[] { 1208f, 4f, 8f },
            };

            object[] args = { mins, maxs, 1000f, 0, 0, 0f };
            bool tooClose = (bool)Invoke("FindTooClosePair", args);

            Assert.IsFalse(tooClose, "pair " + args[3] + "," + args[4] + " gap " + args[5]);
        }

        [Test]
        public void TheSixIslandsOfTheAllMapsHubAreFarEnoughApartAndInRange()
        {
            // Play-area bounds (collision + structures + props, backdrop excluded) moved by the
            // list's offsets. Cancer is the new-rule reading; vs_2 at (2341, 0, -2082) and
            // vs_00 at (-1200, 0, -1200).
            float[][] mins =
            {
                new[] { -26.4f, -3f, -25.2f },        // cancer
                new[] { -20f, 0f, -1220f },           // picnic
                new[] { -1216f, 0f, -42f },           // physics test field
                new[] { -40f, -1.8f, 1160f },         // terrain lab
                new[] { 1192f, 0f, -8f },             // hub room
                new[] { 1139.6f, -17.2f, -1244.9f },  // vs_2
                new[] { -1203.6f, -0.7f, -1203.6f },  // vs_00
            };
            float[][] maxs =
            {
                new[] { 32.7f, 14f, 47.9f },
                new[] { 4f, 9f, -1196f },
                new[] { -1184f, 2f, 19f },
                new[] { 40f, 1.2f, 1240f },
                new[] { 1208f, 4f, 8f },
                new[] { 1259.5f, 57.7f, -1154.8f },
                new[] { -1196.4f, 12.5f, -1196.4f },
            };

            object[] args = { mins, maxs, 1000f, 0, 0, 0f };
            Assert.IsFalse((bool)Invoke("FindTooClosePair", args), "pair " + args[3] + "," + args[4] + " gap " + args[5]);
            for (int i = 0; i < mins.Length; i++)
            {
                Assert.IsTrue((bool)Invoke("IsWithinCoordinateRange", new object[] { mins[i], maxs[i], 1300f }), "island " + i);
            }
        }

        [Test]
        public void AClosePairIsReportedWithItsIndicesAndGap()
        {
            // Second island 400 m from the first on x.
            float[][] mins = { new[] { 0f, 0f, 0f }, new[] { 410f, 0f, 0f }, new[] { 5000f, 0f, 0f } };
            float[][] maxs = { new[] { 10f, 1f, 1f }, new[] { 420f, 1f, 1f }, new[] { 5010f, 1f, 1f } };

            object[] args = { mins, maxs, 1000f, 0, 0, 0f };
            bool tooClose = (bool)Invoke("FindTooClosePair", args);

            Assert.IsTrue(tooClose);
            Assert.AreEqual(0, args[3]);
            Assert.AreEqual(1, args[4]);
            Assert.AreEqual(400f, (float)args[5], 1e-3f);
        }

        // ---- coordinate range ----

        [Test]
        public void MaxAbsCoordinateIsTheLargestCornerCoordinate()
        {
            float largest = (float)Invoke("MaxAbsCoordinate", new object[]
            {
                new float[] { -1220f, 0f, -5f }, new float[] { 4f, 9f, 1100f },
            });
            Assert.AreEqual(1220f, largest);
        }

        [Test]
        public void ABoxInsideThePrecisionRangeIsAcceptedAndOneOutsideIsNot()
        {
            var min = new float[] { -1220f, 0f, -20f };
            var max = new float[] { -1180f, 9f, 20f };

            Assert.IsTrue((bool)Invoke("IsWithinCoordinateRange", new object[] { min, max, 1300f }));
            Assert.IsFalse((bool)Invoke("IsWithinCoordinateRange", new object[] { min, max, 1200f }));
        }

        // ---- backdrop (drawn-only scenery) ----

        [Test]
        public void ARendererUnderTerrainVisualIsBackdropAndOthersAreNot()
        {
            const string folder = "TerrainVisual";
            Assert.IsTrue((bool)Invoke("IsBackdropPath", new object[]
                { "Island__ref_wlk_vs2/LevelContent/_ref_wlk_vs2/TerrainRoot/TerrainVisual/wlk_01_sky_10", folder }));
            Assert.IsFalse((bool)Invoke("IsBackdropPath", new object[]
                { "Island__ref_wlk_vs2/LevelContent/_ref_wlk_vs2/TerrainRoot/TerrainCollision", folder }));
            Assert.IsFalse((bool)Invoke("IsBackdropPath", new object[]
                { "Island__ref_wlk_vs2/LevelContent/_ref_wlk_vs2/Structures/vs2_104", folder }));
            // only a whole folder name counts, not a longer name that contains it
            Assert.IsFalse((bool)Invoke("IsBackdropPath", new object[] { "A/TerrainVisualX/B", folder }));
            Assert.IsFalse((bool)Invoke("IsBackdropPath", new object[] { "", folder }));
            Assert.IsFalse((bool)Invoke("IsBackdropPath", new object[] { null, folder }));
        }

        [Test]
        public void AFarBackdropDoesNotMoveTheIslandsPlayAreaChecks()
        {
            // vs_2 at offset (2341, 0, -2082): play area x 1139.6..1259.5, z -1244.9..-1154.8.
            // Its sky shell would reach 7000 m out; only the play area is judged.
            var min = new float[] { 1139.6f, -17.2f, -1244.9f };
            var max = new float[] { 1259.5f, 57.7f, -1154.8f };
            Assert.IsTrue((bool)Invoke("IsWithinCoordinateRange", new object[] { min, max, 1300f }));
            var skyMin = new float[] { -2259f, 0f, -4627f };
            var skyMax = new float[] { 4941f, 1756f, 2463f };
            Assert.IsFalse((bool)Invoke("IsWithinCoordinateRange", new object[] { skyMin, skyMax, 1300f }));
        }

        // ---- duplicates ----

        [Test]
        public void TwoIslandsAtTheSamePlaceAreFound()
        {
            float[][] offsets = { new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 1200f }, new[] { 0.0004f, 0f, 1200f } };

            Assert.AreEqual(2, (int)Invoke("FirstDuplicateOffset", new object[] { offsets }));
        }

        [Test]
        public void DistinctOffsetsHaveNoDuplicate()
        {
            float[][] offsets = { new[] { 0f, 0f, 0f }, new[] { 0f, 0f, -1200f }, new[] { -1200f, 0f, 0f } };

            Assert.AreEqual(-1, (int)Invoke("FirstDuplicateOffset", new object[] { offsets }));
        }

        // ---- names and report text ----

        [Test]
        public void TheShellNameIsIslandPlusTheLevelId()
        {
            Assert.AreEqual("Island__ref_cancer", (string)Invoke("ShellName", new object[] { "_ref_cancer" }));
            Assert.AreEqual("Island_physics_test_field",
                (string)Invoke("ShellName", new object[] { "physics_test_field" }));
        }

        [Test]
        public void NumbersFormatWithAtMostThreeDecimalsAndNeverNegativeZero()
        {
            Assert.AreEqual("0.222", (string)Invoke("Num", new object[] { 0.222f }));
            Assert.AreEqual("-1200", (string)Invoke("Num", new object[] { -1200f }));
            Assert.AreEqual("0", (string)Invoke("Num", new object[] { -0f }));
            Assert.AreEqual("0.15", (string)Invoke("Num", new object[] { 0.15f }));
        }

        [Test]
        public void VectorsFormatAsAParenthesisedTriple()
        {
            Assert.AreEqual("(0, 0, -1200)",
                (string)Invoke("Format", new object[] { new float[] { 0f, -0f, -1200f } }));
            Assert.AreEqual("(none)", (string)Invoke("Format", new object[] { null }));
        }

        // ------------------------------------------------------------------ helpers

        private object Invoke(string name, object[] args)
        {
            MethodInfo method = mathType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "IslandMath." + name + " not found");
            return method.Invoke(null, args);
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
