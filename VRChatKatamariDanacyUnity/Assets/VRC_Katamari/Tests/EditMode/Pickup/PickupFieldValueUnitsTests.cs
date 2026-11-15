using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the value-units decision introduced by Docs/OPEN_DECISIONS.md (q) U2: a row's
    /// value is the caller's declared number, else the value already serialized on the host,
    /// else 0 with a report note. No (kind, tier) table participates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection, like <c>PickupFieldAuthoringRulesTests</c>: the rules live in
    /// <c>Assets/VRC_Katamari/Editor/Pickup/PickupFieldAuthoringRules.cs</c> and this test
    /// assembly references only <c>VRCKatamari.Runtime</c> (see the EditMode asmdef), so it
    /// cannot name the editor type directly. The methods exercised here are pure static
    /// functions, which is why reflection is enough.
    /// </para>
    /// <para>
    /// NOT COVERED HERE: the SerializedObject write and the UdonSharp proxy copy inside
    /// <c>PickupFieldSync.SyncFromChildren</c>. That entry point refuses to run unless the
    /// host is a real UdonSharp proxy with a backing UdonBehaviour, which an EditMode unit
    /// test cannot fabricate without the Lab's scene-building sequence; the pure decision
    /// (<c>TryResolveValueUnits</c>) and the read-back comparison
    /// (<c>FindValueUnitMismatches</c>, <c>FindDeclaredIdsWithoutRows</c>) are
    /// split out precisely so the rules are testable here. The integration check is the
    /// operator's placement import: the report must print
    /// <c>valueUnits: declared=..., preserved=0, undeclared=0, read-back: verified</c>, and a
    /// deliberate mismatch (or a dropped row) must make it print <c>NOT verified</c> and
    /// fail the run.
    /// </para>
    /// <para>
    /// The named scenarios are the four U2 acceptance cases: declared value written,
    /// existing value preserved, a kind/tier with no old table row still forms a row, and a
    /// read-back mismatch is reported instead of accepted.
    /// </para>
    /// </remarks>
    public class PickupFieldValueUnitsTests
    {
        private const string RulesTypeName = "VRCKatamari.Editor.Pickup.PickupFieldAuthoringRules";

        private const string SourceDeclared = "declared";
        private const string SourceExisting = "existing";
        private const string SourceNone = "none";

        [Test]
        public void DeclaredValueWinsOverTheSerializedValue()
        {
            // The importer's declaration is the truth even when the scene already carries a
            // different number from an older sync.
            ValueResolution declared = Resolve(8, true, 4, true);
            Assert.IsTrue(declared.Accepted);
            Assert.AreEqual(8, declared.ValueUnits);
            Assert.AreEqual(SourceDeclared, declared.Source);
        }

        [Test]
        public void ExistingSerializedValueIsPreservedWithoutADeclaration()
        {
            // The un-migrated scene path: StoryLevel's 110 props and CpuSkirmish have no
            // placement.json yet, so a menu sync must leave every value alone.
            ValueResolution preserved = Resolve(0, false, 16, true);
            Assert.IsTrue(preserved.Accepted);
            Assert.AreEqual(16, preserved.ValueUnits);
            Assert.AreEqual(SourceExisting, preserved.Source);

            // Zero is preserved bit-for-bit too: "fix" would be a reprice the old scene never
            // asked for. The host reports such a row at start-up; the author decides.
            ValueResolution zero = Resolve(0, false, 0, true);
            Assert.IsTrue(zero.Accepted);
            Assert.AreEqual(0, zero.ValueUnits);
            Assert.AreEqual(SourceExisting, zero.Source);
        }

        [Test]
        public void ANewRowWithNoDeclarationIsWrittenAsZeroAndNotDropped()
        {
            // Neither a declaration nor an existing value: the row is still written, with 0,
            // and the sync report names it. Dropping the row would lose the prop's geometry
            // and pickup wiring over a missing number.
            ValueResolution none = Resolve(0, false, 0, false);
            Assert.IsTrue(none.Accepted);
            Assert.AreEqual(0, none.ValueUnits);
            Assert.AreEqual(SourceNone, none.Source);
        }

        [Test]
        public void AKindTierWithoutAnOldTableRowStillResolves()
        {
            // container m is the pair the old ValueTable never had (PICNIC_CORNER_LEVEL_ONE
            // C5). The resolver has no kind or tier parameter at all, so such a pair cannot
            // be refused or dropped any more: the declared number is the whole input.
            ValueResolution containerMedium = Resolve(16, true, 0, false);
            Assert.IsTrue(containerMedium.Accepted);
            Assert.AreEqual(16, containerMedium.ValueUnits);
            Assert.AreEqual(SourceDeclared, containerMedium.Source);
        }

        [Test]
        public void AnInvalidDeclarationIsRefusedInsteadOfFallingBack()
        {
            // A declared 0 or a negative number is a data error, not "keep the old value":
            // silently preserving a different number than the level states is exactly the
            // disagreement U2 removes. The sync reports the row as rejected.
            ValueResolution zero = Resolve(0, true, 8, true);
            Assert.IsFalse(zero.Accepted);
            Assert.AreEqual(0, zero.ValueUnits);
            Assert.IsNull(zero.Source);

            ValueResolution negative = Resolve(-3, true, 8, true);
            Assert.IsFalse(negative.Accepted);
            Assert.IsNull(negative.Source);
        }

        [Test]
        public void ReadBackMismatchIsReportedPerItem()
        {
            var declared = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "apple", 8 },
                { "pear", 4 },
            };

            // Host rows are index-aligned; matching is by item id so a reordered row still
            // reports the right prop.
            string[] mismatches = Mismatches(new[] { "pear", "apple" }, new[] { 4, 2 }, declared);

            Assert.AreEqual(1, mismatches.Length);
            StringAssert.Contains("apple: host=2, declared=8", mismatches[0]);
        }

        [Test]
        public void MatchingReadBackHasNoMismatch()
        {
            var declared = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "apple", 8 },
                { "pear", 4 },
            };

            CollectionAssert.IsEmpty(Mismatches(new[] { "apple", "pear" }, new[] { 8, 4 }, declared));
        }

        [Test]
        public void DeclaredIdWithoutAHostRowIsReported()
        {
            // The sync dropped the prop (bad @size, duplicate id, degenerate AABB, ...) or the
            // caller passed an id outside this field. Either way the declared total cannot be
            // trusted, so the missing id must be named.
            var declared = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "apple", 8 },
                { "pear", 4 },
            };

            string[] missing = MissingRows(new[] { "apple" }, declared);

            CollectionAssert.AreEqual(new[] { "pear" }, missing);
        }

        // ------------------------------------------------------------- reflection ---

        private struct ValueResolution
        {
            public bool Accepted;
            public int ValueUnits;
            public string Source;
        }

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

        private static MethodInfo RulesMethod(string methodName)
        {
            MethodInfo method = RulesType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, RulesTypeName + "." + methodName + " is missing");
            return method;
        }

        private static ValueResolution Resolve(int declaredValueUnits, bool hasDeclaredValueUnits,
            int existingValueUnits, bool hasExistingValueUnits)
        {
            object[] arguments = { declaredValueUnits, hasDeclaredValueUnits, existingValueUnits,
                hasExistingValueUnits, 0, null };
            bool accepted = (bool)RulesMethod("TryResolveValueUnits").Invoke(null, arguments);

            var resolution = new ValueResolution();
            resolution.Accepted = accepted;
            resolution.ValueUnits = arguments[4] is int units ? units : 0;
            resolution.Source = arguments[5] as string;
            return resolution;
        }

        private static string[] Mismatches(string[] itemIds, int[] hostValueUnits,
            Dictionary<string, int> declaredValueUnits)
        {
            object[] arguments = { itemIds, hostValueUnits, declaredValueUnits };
            return (string[])RulesMethod("FindValueUnitMismatches").Invoke(null, arguments);
        }

        private static string[] MissingRows(string[] itemIds, Dictionary<string, int> declaredValueUnits)
        {
            object[] arguments = { itemIds, declaredValueUnits };
            return (string[])RulesMethod("FindDeclaredIdsWithoutRows").Invoke(null, arguments);
        }
    }
}
