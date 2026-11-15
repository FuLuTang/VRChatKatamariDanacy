using System;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the pure rules behind the pickup-field authoring path: size tier from bounds,
    /// value-unit lookup by (kind, tier), duplicate-id detection and deterministic ordering.
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
    /// 0.76 m split the two gaps), and the value rule is the ledger's "whole units >= 1"
    /// from Docs/OPEN_DECISIONS.md (j).
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

            // Undeclared combinations are refused instead of defaulted.
            Assert.AreEqual(0, Lookup("bewildered", "s"));
            Assert.AreEqual(0, Lookup("nature", "l"));
            Assert.AreEqual(0, Lookup("boundary", "l"));
            Assert.AreEqual(0, Lookup(null, "s"));
            Assert.AreEqual(0, Lookup("nature", null));

            // "Not declared" and "declared with an illegal number" stay distinguishable, so a
            // broken table row is reported as a value error rather than as a missing row.
            Assert.IsTrue(HasValueRow("nature", "s"));
            Assert.IsFalse(HasValueRow("nature", "l"));
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
    }
}
