using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins <c>PlacementImporter</c>'s curved-ramp expansion: the segment count the 5 %-of-r
    /// chord budget asks for, the frames the chain lands on, and the refusals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These calls go through reflection on purpose, exactly like
    /// <see cref="PlacementImporterVisualTests"/>: the importer lives in
    /// <c>Assets/VRC_Katamari/Editor/LevelBuild/</c> and this assembly references only
    /// <c>VRCKatamari.Runtime</c>, so it cannot name editor types.
    /// </para>
    /// <para>
    /// The expected numbers come from the geometry the design states, not from the
    /// implementation (<c>CONTACT_MODEL_V2</c> 5.7): an arc of total angle Phi cut into K
    /// segments has <c>delta = Phi/K</c>, chord <c>L = 2R sin(delta/2)</c>, sagitta
    /// <c>s = R(1 - cos(delta/2))</c>, segment <c>i</c> sits at <c>phi = (i + 0.5) delta</c>,
    /// its centre is <c>origin + R sin(phi) forward + R(1 - cos(phi)) up</c>, its walking
    /// normal is <c>-sin(phi) forward + cos(phi) up</c> and it is one sagitta longer than its
    /// chord. The design's table (2 m -> 10, 3 -> 12, 5 -> 15, 10 -> 21, 20 -> 29 segments for
    /// 90 deg) is the independent check on the auto count.
    /// </para>
    /// <para>
    /// What is <b>not</b> covered here: the scene side (creating/updating/deleting the
    /// <c>&lt;id&gt;_00</c> children under a parent and the second import's <c>+0 ~0 -0</c>).
    /// That needs a scene and the import menu, so it is the Unity operator's check, not an
    /// EditMode test.
    /// </para>
    /// </remarks>
    public class PlacementImporterCurveRampTests
    {
        private const string ImporterTypeName = "VRCKatamari.Editor.LevelBuild.PlacementImporter";

        // float32 through the tessellator and LookRotation: identical input, identical output,
        // so 1e-4 is generous for a metre-scale value.
        private const float Tolerance = 1e-4f;

        private const float BallDiameterStart = 0.30f;
        private static readonly Vector3 Origin = new Vector3(8f, 0f, 12f);

        // ---- the chord budget: the design's segment table ---------------------

        [Test]
        public void AutoSegmentCountsMatchTheDesignTable()
        {
            // CONTACT_MODEL_V2 5.7: 90 deg arcs, error budget 5 % of r_min = 7.5 mm.
            int[][] table =
            {
                new[] { 2, 10 }, new[] { 3, 12 }, new[] { 5, 15 },
                new[] { 10, 21 }, new[] { 20, 29 },
            };

            for (int row = 0; row < table.Length; row++)
            {
                Chain chain = Expand(table[row][0], 90f, 0, 4f, 0.25f, "up", Quaternion.identity);
                Assert.IsTrue(chain.Accepted, "R=" + table[row][0]);
                Assert.AreEqual(table[row][1], chain.Centers.Length,
                    "R=" + table[row][0] + " needs " + table[row][1] + " segments at 90 deg");
            }
        }

        [Test]
        public void AutoCountIsTheSmallestThatFitsTheSagittaBudget()
        {
            // R = 5 m, 90 deg: 15 segments give delta = 6 deg and s = 5(1-cos 3 deg) = 6.85 mm
            // (inside the 7.5 mm budget); 14 would give 7.87 mm (outside).
            Chain chain = Expand(5f, 90f, 0, 3f, 0.25f, "up", Quaternion.identity);
            Assert.AreEqual(15, chain.Centers.Length);

            float sagitta = 5f * (1f - Mathf.Cos(Mathf.Deg2Rad * (90f / 15f) * 0.5f));
            Assert.LessOrEqual(sagitta, 0.05f * BallDiameterStart * 0.5f);
            float coarser = 5f * (1f - Mathf.Cos(Mathf.Deg2Rad * (90f / 14f) * 0.5f));
            Assert.Greater(coarser, 0.05f * BallDiameterStart * 0.5f,
                "14 segments must be over budget, otherwise the auto count is too coarse");
        }

        // ---- the chain's geometry ---------------------------------------------

        [Test]
        public void ChainFollowsTheArcWithTheWalkingNormalUp()
        {
            const float radius = 4f;
            const float arc = 30f;
            const int segments = 8;
            Chain chain = Expand(radius, arc, segments, 4f, 0.25f, "up", Quaternion.identity);

            Assert.IsTrue(chain.Accepted);
            Assert.AreEqual(segments, chain.Centers.Length);

            float delta = arc / segments;
            for (int i = 0; i < segments; i++)
            {
                float phi = Mathf.Deg2Rad * (i + 0.5f) * delta;
                float forward = radius * Mathf.Sin(phi);
                float rise = radius * (1f - Mathf.Cos(phi));
                Assert.AreEqual(Origin.x, chain.Centers[i].x, Tolerance,
                    "segment " + i + ": the arc runs along +Z, so x stays at the origin's");
                Assert.AreEqual(Origin.y + rise, chain.Centers[i].y, Tolerance, "segment " + i);
                Assert.AreEqual(Origin.z + forward, chain.Centers[i].z, Tolerance, "segment " + i);

                // normal = -sin(phi) * forward + cos(phi) * up, with forward = +Z and up = +Y.
                Vector3 normal = chain.Rotations[i] * Vector3.up;
                Assert.AreEqual(0f, normal.x, Tolerance);
                Assert.AreEqual(Mathf.Cos(phi), normal.y, Tolerance, "the walking face still points up");
                Assert.AreEqual(-Mathf.Sin(phi), normal.z, Tolerance);

                // tangent = cos(phi) * forward + sin(phi) * up (local +Z runs up the slope).
                Vector3 tangent = chain.Rotations[i] * Vector3.forward;
                Assert.AreEqual(Mathf.Sin(phi), tangent.y, Tolerance, "local +Z runs up the slope");
                Assert.AreEqual(Mathf.Cos(phi), tangent.z, Tolerance);
            }
        }

        [Test]
        public void AdjacentSegmentsTurnByOneSegmentAngle()
        {
            // delta/2 is what the ball can feel when it presses two segments at once (5.7).
            const float arc = 60f;
            const int segments = 12;
            Chain chain = Expand(6f, arc, segments, 3f, 0.2f, "up", Quaternion.identity);

            float delta = arc / segments;
            float expected = Mathf.Cos(Mathf.Deg2Rad * delta);
            for (int i = 0; i + 1 < segments; i++)
            {
                Vector3 left = chain.Rotations[i] * Vector3.up;
                Vector3 right = chain.Rotations[i + 1] * Vector3.up;
                Assert.AreEqual(expected, Vector3.Dot(left, right), 1e-4f, "segment " + i);
            }
        }

        [Test]
        public void SegmentSizeIsWidthByThicknessByTheOverlappedChord()
        {
            const float radius = 4f;
            const float arc = 30f;
            const int segments = 8;
            Chain chain = Expand(radius, arc, segments, 4f, 0.25f, "up", Quaternion.identity);

            float delta = Mathf.Deg2Rad * arc / segments;
            float chord = 2f * radius * Mathf.Sin(delta * 0.5f);
            float sagitta = radius * (1f - Mathf.Cos(delta * 0.5f));
            float stretched = chord * (1f + 2f * sagitta / chord);

            for (int i = 0; i < segments; i++)
            {
                Assert.AreEqual(4f, chain.Sizes[i].x, Tolerance);
                Assert.AreEqual(0.25f, chain.Sizes[i].y, Tolerance);
                Assert.AreEqual(stretched, chain.Sizes[i].z, Tolerance,
                    "segment " + i + " is one sagitta longer than its chord");
            }
        }

        [Test]
        public void TheArcRisesByTheClosedFormAtItsEnd()
        {
            // A 30 deg arc of R = 4 rises 4(1-cos 30 deg) = 0.5359 m between the arc's start
            // and its end; the chain's last centre sits half a segment inside that.
            const float radius = 4f;
            const float arc = 30f;
            const int segments = 8;
            Chain chain = Expand(radius, arc, segments, 4f, 0.25f, "up", Quaternion.identity);

            float delta = arc / segments;
            float lastPhi = Mathf.Deg2Rad * (arc - delta * 0.5f);
            Assert.AreEqual(Origin.y + radius * (1f - Mathf.Cos(lastPhi)),
                chain.Centers[segments - 1].y, Tolerance);
            Assert.AreEqual(Origin.y + radius * (1f - Mathf.Cos(Mathf.Deg2Rad * delta * 0.5f)),
                chain.Centers[0].y, Tolerance, "the first centre is one sagitta above the origin");
        }

        // ---- the down bend -----------------------------------------------------

        [Test]
        public void DownBendDescendsWithTheNormalTiltingForward()
        {
            const float radius = 4f;
            const float arc = 30f;
            const int segments = 8;
            Chain chain = Expand(radius, arc, segments, 4f, 0.25f, "down", Quaternion.identity);

            Assert.IsTrue(chain.Accepted);
            Assert.AreEqual(segments, chain.Centers.Length);
            Assert.Less(chain.Centers[segments - 1].y, chain.Centers[0].y, "a down arc descends");

            float delta = arc / segments;
            for (int i = 0; i < segments; i++)
            {
                float phi = Mathf.Deg2Rad * (i + 0.5f) * delta;
                Assert.AreEqual(Origin.y - radius * (1f - Mathf.Cos(phi)), chain.Centers[i].y,
                    Tolerance, "segment " + i);

                Vector3 normal = chain.Rotations[i] * Vector3.up;
                Assert.AreEqual(Mathf.Sin(phi), normal.z, Tolerance,
                    "the walking face tilts towards travel, not away from it");
                Assert.AreEqual(Mathf.Cos(phi), normal.y, Tolerance, "and it still points up");
            }
        }

        [Test]
        public void DownBendIsTheMirrorOfTheUpBendNotASecondFormula()
        {
            // Same centres and tangents as the up arc folded about y = origin.y; only the
            // normal's sign is put back. This is what keeps the down bend out of the
            // tessellator (there is exactly one arc formula, in CurveRampMath).
            const float radius = 5f;
            const float arc = 45f;
            const int segments = 10;
            Chain up = Expand(radius, arc, segments, 3f, 0.2f, "up", Quaternion.identity);
            Chain down = Expand(radius, arc, segments, 3f, 0.2f, "down", Quaternion.identity);

            for (int i = 0; i < segments; i++)
            {
                Assert.AreEqual(up.Centers[i].y - Origin.y, Origin.y - down.Centers[i].y, Tolerance);
                Assert.AreEqual(up.Centers[i].z, down.Centers[i].z, Tolerance);
                Vector3 upNormal = up.Rotations[i] * Vector3.up;
                Vector3 downNormal = down.Rotations[i] * Vector3.up;
                Assert.AreEqual(upNormal.y, downNormal.y, Tolerance, "the normal's height is unchanged");
                Assert.AreEqual(-upNormal.z, downNormal.z, Tolerance, "only its lean is mirrored");
            }
        }

        // ---- refusals ----------------------------------------------------------

        [Test]
        public void ImpossibleArcsAreRefusedInsteadOfBuilt()
        {
            AssertRefused(0f, 30f, 8, 4f, 0.25f, "up");          // zero radius
            AssertRefused(float.NaN, 30f, 8, 4f, 0.25f, "up");   // NaN radius
            AssertRefused(4f, 0f, 8, 4f, 0.25f, "up");           // zero arc
            AssertRefused(4f, 361f, 8, 4f, 0.25f, "up");         // over a full turn
            AssertRefused(4f, 30f, 8, 0f, 0.25f, "up");          // zero width
            AssertRefused(4f, 30f, 8, 4f, 0f, "up");             // zero thickness
            AssertRefused(4f, 30f, 8, 4f, float.PositiveInfinity, "up");
            AssertRefused(4f, 30f, 8, 4f, 0.25f, "helix");       // no horizontal/helix tessellator
            AssertRefused(4f, 30f, 8, 4f, 0.25f, "downish");
        }

        [Test]
        public void ASegmentCountUnderTheBudgetIsStillAcceptedByTheExpansion()
        {
            // The budget is a *validator* rule; the expansion itself is pure geometry, so a
            // coarse chain expands (the importer refuses it earlier, with
            // [curve_segments_coarse]). Pinning that keeps the two layers separate.
            Chain chain = Expand(20f, 90f, 3, 2f, 0.2f, "up", Quaternion.identity);
            Assert.IsTrue(chain.Accepted);
            Assert.AreEqual(3, chain.Centers.Length);
        }

        // ---- the child names the scene gets ------------------------------------

        [Test]
        public void SegmentChildrenAreNamedCurveIdUnderscoreIndex()
        {
            Assert.AreEqual("curve_up_a_00", SegmentName("curve_up_a", 0));
            Assert.AreEqual("curve_up_a_03", SegmentName("curve_up_a", 3));
            Assert.AreEqual("curve_up_a_12", SegmentName("curve_up_a", 12));
        }

        // ------------------------------------------------------------ helpers ---

        private struct Chain
        {
            public bool Accepted;
            public Vector3[] Centers;
            public Quaternion[] Rotations;
            public Vector3[] Sizes;
        }

        private static Chain Expand(float radiusM, float arcDeg, int segments, float widthM,
            float thicknessM, string bend, Quaternion rotation)
        {
            object[] arguments =
            {
                radiusM, arcDeg, segments, widthM, thicknessM, bend, Origin, rotation,
                BallDiameterStart, null, null, null,
            };
            bool accepted = (bool)Invoke("TryExpandCurveRamp", arguments);

            var chain = new Chain();
            chain.Accepted = accepted;
            chain.Centers = arguments[9] as Vector3[] ?? new Vector3[0];
            chain.Rotations = arguments[10] as Quaternion[] ?? new Quaternion[0];
            chain.Sizes = arguments[11] as Vector3[] ?? new Vector3[0];
            return chain;
        }

        private static void AssertRefused(float radiusM, float arcDeg, int segments, float widthM,
            float thicknessM, string bend)
        {
            Chain chain = Expand(radiusM, arcDeg, segments, widthM, thicknessM, bend,
                Quaternion.identity);
            Assert.IsFalse(chain.Accepted, "radius=" + radiusM + " arc=" + arcDeg + " bend=" + bend +
                                           " must not produce a chain");
            Assert.AreEqual(0, chain.Centers.Length);
            Assert.AreEqual(0, chain.Rotations.Length);
            Assert.AreEqual(0, chain.Sizes.Length);
        }

        private static string SegmentName(string structureId, int index)
        {
            MethodInfo method = ImporterType().GetMethod("CurveSegmentName",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, ImporterTypeName + ".CurveSegmentName is missing");
            return (string)method.Invoke(null, new object[] { structureId, index });
        }

        // ----------------------------------------------------------- reflection ---

        private static Type ImporterType()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type found = assemblies[i].GetType(ImporterTypeName, false);
                if (found != null)
                {
                    return found;
                }
            }

            Assert.Fail("type '" + ImporterTypeName + "' is not loaded. It lives in " +
                        "Assets/VRC_Katamari/Editor/LevelBuild and is expected to compile into the " +
                        "editor assembly.");
            return null;
        }

        private static object Invoke(string methodName, params object[] arguments)
        {
            MethodInfo method = ImporterType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, ImporterTypeName + "." + methodName + " is missing");
            return method.Invoke(null, arguments);
        }
    }
}
