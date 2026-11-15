using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the pure rules behind the pickup-field authoring path: size tier from bounds,
    /// value-unit lookup by (kind, tier), the <c>@size=</c> declared-size suffix,
    /// duplicate-id detection and deterministic ordering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These calls go through reflection on purpose. The rules live in
    /// <c>Assets/VRC_Katamari/Editor/Pickup/PickupFieldAuthoringRules.cs</c>, and this test
    /// assembly references only <c>VRCKatamari.Runtime</c> (see
    /// <c>Tests/EditMode/VRCKatamari.Tests.EditMode.asmdef</c>), so it cannot name editor
    /// types directly. Referencing the editor assembly would be a one-line asmdef change,
    /// but that is a project-structure decision, not this task's; the same constraint is
    /// why <c>BallMotor/BankReasonTests.cs</c> reads <c>UdonSharpBehaviour</c> by reflection.
    /// When the editor assembly gains a reference here, these helpers can be replaced with
    /// direct calls without changing any assertion.
    /// </para>
    /// <para>
    /// Expected values are hand calculations from the declared rules, not copies of the
    /// implementation: the tier boundaries sit in the gaps of the PicnicLawn prop catalog
    /// (small props end at 0.18 m, medium at 0.72 m, large begin at 0.80 m, so 0.21 m and
    /// 0.76 m split the two gaps), the value rule is the ledger's "whole units >= 1" from
    /// Docs/OPEN_DECISIONS.md (j), and the <c>@size=</c> suffix is the explicit absorb size
    /// for gate props (Docs/Design/Maps/REGION_MAP_PLAN.md, gap G13): invariant culture, one
    /// marker, stripped from the item id, and refused - never ignored - when malformed.
    /// </para>
    /// </remarks>
    public class PickupFieldAuthoringRulesTests
    {
        private const string RulesTypeName = "VRCKatamari.Editor.Pickup.PickupFieldAuthoringRules";

        // Mirrors the published boundaries; kept as literals so a change to either the rule
        // or the test shows up as a failing assertion instead of silently agreeing.
        private const float SmallTierMax = 0.21f;
        private const float MediumTierMax = 0.76f;

        [Test]
        public void SizeTiersSplitTheCatalogGaps()
        {
            // Small: 0.05 m acorn through 0.18 m pine cone.
            Assert.AreEqual("s", Classify(0.05f, 0.05f, 0.05f));
            Assert.AreEqual("s", Classify(0.18f, 0.06f, 0.03f));

            // Boundaries are inclusive on the lower tier.
            Assert.AreEqual("s", Classify(SmallTierMax, 0.1f, 0.1f));
            Assert.AreEqual("m", Classify(SmallTierMax + 0.0001f, 0.1f, 0.1f));

            // Medium: 0.72 m watermelon through 0.76 m.
            Assert.AreEqual("m", Classify(0.72f, 0.5f, 0.5f));
            Assert.AreEqual("m", Classify(MediumTierMax, 0.1f, 0.1f));

            // Large: 0.80 m and up (1.4 m bench).
            Assert.AreEqual("l", Classify(MediumTierMax + 0.0001f, 0.1f, 0.1f));
            Assert.AreEqual("l", Classify(1.4f, 0.5f, 0.5f));

            // Only the longest extent decides.
            Assert.AreEqual("s", Classify(0.05f, 0.18f, 0.04f));
        }

        [Test]
        public void DegenerateBoundsHaveNoTier()
        {
            // A prop row needs a positive AABB; these are data errors, not small props.
            Assert.AreEqual(string.Empty, Classify(0f, 0.1f, 0.1f));
            Assert.AreEqual(string.Empty, Classify(0.00005f, 0.1f, 0.1f));
            Assert.AreEqual(string.Empty, Classify(-1f, 0.1f, 0.1f));
            Assert.AreEqual(string.Empty, Classify(float.NaN, 0.1f, 0.1f));
            Assert.AreEqual(string.Empty, Classify(0.1f, float.PositiveInfinity, 0.1f));
            Assert.AreEqual(string.Empty, Classify(0.1f, 0.1f, float.NegativeInfinity));
        }

        [Test]
        public void ValueTableIsKeyedByKindAndTier()
        {
            // Declared rows, taken from the catalog's dominant value per (kind, tier).
            Assert.AreEqual(1, Lookup("nature", "s"));
            Assert.AreEqual(1, Lookup("food", "s"));
            Assert.AreEqual(4, Lookup("food", "m"));
            Assert.AreEqual(6, Lookup("garden", "m"));
            Assert.AreEqual(16, Lookup("container", "l"));

            // Story level 2 (fruit counting) rows, LEVEL_CONTENT_PLAN.md 3.1: strawberry and
            // grapes s = 1, apple/pear/orange/banana/watermelon m = 4. Without these two rows
            // every fruit row is refused by the sync and the level counts 0 fruit.
            Assert.AreEqual(1, Lookup("fruit", "s"));
            Assert.AreEqual(4, Lookup("fruit", "m"));

            // Undeclared combinations are refused instead of defaulted.
            Assert.AreEqual(0, Lookup("bewildered", "s"));
            Assert.AreEqual(0, Lookup("nature", "l"));
            Assert.AreEqual(0, Lookup("boundary", "l"));
            Assert.AreEqual(0, Lookup("fruit", "l"));
            Assert.AreEqual(0, Lookup(null, "s"));
            Assert.AreEqual(0, Lookup("nature", null));

            // "Not declared" and "declared with an illegal number" stay distinguishable, so a
            // broken table row is reported as a value error rather than as a missing row.
            Assert.IsTrue(HasValueRow("nature", "s"));
            Assert.IsTrue(HasValueRow("fruit", "m"));
            Assert.IsFalse(HasValueRow("nature", "l"));
            Assert.IsFalse(HasValueRow("fruit", "l"));
            Assert.IsFalse(HasValueRow("boundary", "l"));
            Assert.IsFalse(HasValueRow(null, "s"));
        }

        [Test]
        public void ValueUnitsMustBeWholeAndAtLeastOne()
        {
            // Mirrors the ledger rule in Docs/OPEN_DECISIONS.md (j): 1.0125 and 0 are refused,
            // not rounded; both would make the prop unabsorbable at runtime as RejectNoGain.
            Assert.IsTrue(Validate(1f, out int one));
            Assert.AreEqual(1, one);

            Assert.IsTrue(Validate(16f, out int sixteen));
            Assert.AreEqual(16, sixteen);

            Assert.IsFalse(Validate(0f, out _));
            Assert.IsFalse(Validate(-3f, out _));
            Assert.IsFalse(Validate(1.5f, out _));
            Assert.IsFalse(Validate(1.0125f, out _));
            Assert.IsFalse(Validate(float.NaN, out _));
            Assert.IsFalse(Validate(float.PositiveInfinity, out _));
            Assert.IsFalse(Validate(2147483648f, out _));
        }

        [Test]
        public void DeclaredSizeSuffixIsParsedAndStripped()
        {
            // The gate-prop convention: "prop_bench_01@size=1.25" means the bench's absorb
            // size is 1.25 m, even though its renderer AABB is 3.2 m long.
            DeclaredSizeResult bench = ParseDeclaredSize("prop_bench_01@size=1.25");
            Assert.IsTrue(bench.Accepted);
            Assert.IsNull(bench.Error);
            Assert.AreEqual("prop_bench_01", bench.ItemId, "the suffix is stripped from the item id");
            Assert.AreEqual(1.25f, bench.EdgeM);

            // A name without the marker keeps its whole name and declares nothing.
            DeclaredSizeResult plain = ParseDeclaredSize("prop_apple_03");
            Assert.IsTrue(plain.Accepted);
            Assert.IsNull(plain.Error);
            Assert.AreEqual("prop_apple_03", plain.ItemId);
            Assert.AreEqual(0f, plain.EdgeM);

            // Only the trailing token is the number, so a name may contain '@' otherwise.
            DeclaredSizeResult dotted = ParseDeclaredSize("prop_scarf@2x_01@size=0.3");
            Assert.IsTrue(dotted.Accepted);
            Assert.AreEqual("prop_scarf@2x_01", dotted.ItemId);
            Assert.AreEqual(0.3f, dotted.EdgeM);

            // The marker is published under one spelling so the docs and the parser cannot
            // drift apart.
            Assert.AreEqual("@size=", MarkerText());

            // Stripping is what keeps ids unique: the declared and undeclared spellings of
            // one prop collapse to the same id, so a duplicate is refused instead of
            // shipping two rows that share a ledger key.
            Assert.AreEqual(ParseDeclaredSize("prop_bench_01").ItemId, bench.ItemId);

            // A null name is not a marker problem; the id check reports it.
            DeclaredSizeResult missing = ParseDeclaredSize(null);
            Assert.IsTrue(missing.Accepted);
            Assert.IsNull(missing.ItemId);
            Assert.AreEqual(0f, missing.EdgeM);
        }

        [Test]
        public void DeclaredSizeRejectsMalformedMarkers()
        {
            // No number, not a number, comma decimal, a space, a wrong-case marker, trailing
            // text after the number, and a marker that would leave an empty id. Every one is
            // refused with a reason: none may fall back to the renderer AABB silently.
            AssertRejected("prop_bench_01@size=");
            AssertRejected("prop_bench_01@size=abc");
            AssertRejected("prop_bench_01@size=1,25");
            AssertRejected("prop_bench_01@size= 1.25");
            AssertRejected("prop_bench_01@Size=1.25");
            AssertRejected("prop_bench_01@SIZE=1.25");
            AssertRejected("prop_bench_01@size=1.25#02");
            AssertRejected("prop_bench_01@size=1.25 ");
            AssertRejected("@size=1.25");
        }

        [Test]
        public void DeclaredSizeRejectsMultipleMarkers()
        {
            AssertRejected("prop_bench_01@size=1.25@size=1.5");

            // A second marker in another case is still a second marker, not a name part.
            AssertRejected("prop_bench_01@size=1.25@SIZE=1.5");
        }

        [Test]
        public void DeclaredSizeRejectsNonPositiveAndSubMillimetre()
        {
            AssertRejected("prop_bench_01@size=0");
            AssertRejected("prop_bench_01@size=+0");
            AssertRejected("prop_bench_01@size=-1.25");

            // Positive but below the tier rule's minimum is still unusable, not "small".
            AssertRejected("prop_bench_01@size=0.00005");

            Assert.IsFalse(Usable(0f));
            Assert.IsFalse(Usable(-1f));
            Assert.IsFalse(Usable(0.00005f));
            Assert.IsTrue(Usable(0.0001f));
            Assert.IsTrue(Usable(1.25f));
        }

        [Test]
        public void DeclaredSizeRejectsNonFinite()
        {
            // Checked on the number rule itself too: a name cannot spell every float, and
            // NaN/Infinity must not become a usable pickup size.
            Assert.IsFalse(Usable(float.NaN));
            Assert.IsFalse(Usable(float.PositiveInfinity));
            Assert.IsFalse(Usable(float.NegativeInfinity));

            AssertRejected("prop_bench_01@size=NaN");
            AssertRejected("prop_bench_01@size=Infinity");
            AssertRejected("prop_bench_01@size=-Infinity");

            // A plain metre value only: exponents are refused, so an overflow spelling can
            // never arrive as Infinity.
            AssertRejected("prop_bench_01@size=1e40");
            AssertRejected("prop_bench_01@size=3.5e38");
        }

        [Test]
        public void DeclaredSizeParsingIsCultureIndependent()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                DeclaredSizeResult parsed = ParseDeclaredSize("prop_bench_01@size=1.25");
                Assert.IsTrue(parsed.Accepted);
                Assert.AreEqual(1.25f, parsed.EdgeM);
                Assert.AreEqual("prop_bench_01", parsed.ItemId);

                // ',' is the German decimal separator; the convention is invariant, so on a
                // German machine the comma is refused instead of quietly becoming 1.25 or 125.
                DeclaredSizeResult comma = ParseDeclaredSize("prop_bench_01@size=1,25");
                Assert.IsFalse(comma.Accepted);
                Assert.IsFalse(string.IsNullOrEmpty(comma.Error));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Test]
        public void DeclaredSizeFeedsTierAndValueTable()
        {
            // The declared edge replaces the AABB in the tier decision: a 3.2 m bench that
            // declares 1.25 m is large tier, and the furniture l row supplies its value.
            Assert.AreEqual("l", Classify(1.25f, 1.25f, 1.25f));
            Assert.AreEqual(16, Lookup("furniture", "l"));

            // The plan's 0.72 m watermelon stays medium tier with the fruit m value.
            Assert.AreEqual("m", Classify(0.72f, 0.72f, 0.72f));
            Assert.AreEqual(4, Lookup("fruit", "m"));
        }

        [Test]
        public void DuplicateIdsAreReportedOnceInOrdinalOrder()
        {
            CollectionAssert.AreEqual(new[] { "apple", "banana" },
                Duplicates(new[] { "banana", "apple", "banana", "apple", "cherry" }));

            CollectionAssert.AreEqual(new[] { "x" }, Duplicates(new[] { "x", "x" }));
            CollectionAssert.IsEmpty(Duplicates(new[] { "a", "b", "c" }));
            CollectionAssert.IsEmpty(Duplicates(new string[0]));
            CollectionAssert.IsEmpty(Duplicates(new[] { "single" }));
        }

        [Test]
        public void HierarchySortIsOrdinalAndDoesNotMutateItsInput()
        {
            string[] input = { "nature/pear", "food/apple", "nature/Apple", "food/banana" };
            string[] sorted = Sort(input);

            // Ordinal: 'f' before 'n', and inside a kind 'A' (65) before 'p' (112).
            CollectionAssert.AreEqual(new[] { "food/apple", "food/banana", "nature/Apple", "nature/pear" },
                sorted);

            CollectionAssert.AreEqual(new[] { "nature/pear", "food/apple", "nature/Apple", "food/banana" },
                input);
        }

        [Test]
        public void HierarchySortSeparatesCaseOrdinal()
        {
            // Case-insensitive or culture-aware sorting would merge these; the row order is
            // part of the host's index-aligned data, so it must not depend on either.
            CollectionAssert.AreEqual(new[] { "B/x", "b/x", "b/y" }, Sort(new[] { "b/y", "b/x", "B/x" }));
        }

        [Test]
        public void HierarchySortIsIdempotent()
        {
            string[] once = Sort(new[] { "c/3", "a/1", "b/2" });
            string[] twice = Sort(once);
            CollectionAssert.AreEqual(once, twice);
        }

        [Test]
        public void ItemIdsAndKindsRejectWhitespaceEdgeCases()
        {
            Assert.IsTrue(IsValidName("apple#00"));
            Assert.IsFalse(IsValidName(""));
            Assert.IsFalse(IsValidName(null));
            Assert.IsFalse(IsValidName(" apple"));
            Assert.IsFalse(IsValidName("apple "));

            Assert.IsTrue(IsValidItemId("acorn#07"));
            Assert.IsFalse(IsValidItemId(" "));

            Assert.IsTrue(IsValidKind("food"));
            Assert.IsFalse(IsValidKind("food "));
        }

        // ------------------------------------------------------------- reflection ---

        private static Type RulesType()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type found = assemblies[i].GetType(RulesTypeName, false);
                if (found != null)
                {
                    return found;
                }
            }

            Assert.Fail("type '" + RulesTypeName +
                        "' is not loaded. It lives in Assets/VRC_Katamari/Editor/Pickup and is expected " +
                        "to compile into the editor assembly.");
            return null;
        }

        private static object Invoke(string methodName, params object[] arguments)
        {
            MethodInfo method = RulesType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, RulesTypeName + "." + methodName + " is missing");
            return method.Invoke(null, arguments);
        }

        private static string Classify(float sizeX, float sizeY, float sizeZ)
        {
            return (string)Invoke("ClassifySizeTier", sizeX, sizeY, sizeZ);
        }

        private static int Lookup(string kind, string tier)
        {
            return (int)Invoke("LookupValueUnits", new object[] { kind, tier });
        }

        private static bool HasValueRow(string kind, string tier)
        {
            return (bool)Invoke("HasValueRow", new object[] { kind, tier });
        }

        private static bool Validate(float rawValueUnits, out int valueUnits)
        {
            object[] arguments = { rawValueUnits, 0 };
            bool accepted = (bool)Invoke("TryValidateValueUnits", arguments);
            valueUnits = arguments[1] is int units ? units : 0;
            return accepted;
        }

        private static string[] Duplicates(string[] itemIds)
        {
            return (string[])Invoke("FindDuplicateIds", new object[] { itemIds });
        }

        private static string[] Sort(string[] paths)
        {
            return (string[])Invoke("SortHierarchyPaths", new object[] { paths });
        }

        private static bool IsValidName(string name)
        {
            return (bool)Invoke("IsValidName", new object[] { name });
        }

        private static bool IsValidItemId(string itemId)
        {
            return (bool)Invoke("IsValidItemId", new object[] { itemId });
        }

        private static bool IsValidKind(string kind)
        {
            return (bool)Invoke("IsValidKind", new object[] { kind });
        }

        // -------------------------------------------------- declared absorb size ---

        private struct DeclaredSizeResult
        {
            public bool Accepted;
            public string ItemId;
            public float EdgeM;
            public string Error;
        }

        private static DeclaredSizeResult ParseDeclaredSize(string name)
        {
            object[] arguments = { name, null, 0f, null };
            bool accepted = (bool)Invoke("TryParseDeclaredSize", arguments);

            var result = new DeclaredSizeResult();
            result.Accepted = accepted;
            result.ItemId = arguments[1] as string;
            result.EdgeM = arguments[2] is float edge ? edge : 0f;
            result.Error = arguments[3] as string;
            return result;
        }

        private static void AssertRejected(string name)
        {
            DeclaredSizeResult result = ParseDeclaredSize(name);
            Assert.IsFalse(result.Accepted, "'" + name + "' should be refused");
            Assert.IsFalse(string.IsNullOrEmpty(result.Error), "'" + name + "' must carry a refusal reason");
            Assert.AreEqual(0f, result.EdgeM, "'" + name + "' must not declare a size");
            Assert.AreEqual(name, result.ItemId, "a refused marker must leave the name alone");
        }

        private static bool Usable(float edgeM)
        {
            return (bool)Invoke("IsUsableDeclaredSize", edgeM);
        }

        private static string MarkerText()
        {
            FieldInfo field = RulesType().GetField("DeclaredSizeMarker", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(field, RulesTypeName + ".DeclaredSizeMarker is missing");
            return (string)field.GetValue(null);
        }
    }
}
