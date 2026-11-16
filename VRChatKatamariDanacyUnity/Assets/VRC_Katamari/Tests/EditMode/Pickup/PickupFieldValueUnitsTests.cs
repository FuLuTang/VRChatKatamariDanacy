using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using VRCKatamari.Economy;
using VRCKatamari.Pickup;

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
    /// read-back mismatch is reported instead of accepted. Step B of Docs/OPEN_DECISIONS.md
    /// (r) added two range cases here: a declaration past <c>int.MaxValue</c> surviving the
    /// declaration, host read-back and ledger legs, and the exactness boundary of the one
    /// float slot that step B deliberately left alone (the motor's candidate queue).
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
            ValueResolution declared = Resolve(8L, true, 4L, true);
            Assert.IsTrue(declared.Accepted);
            Assert.AreEqual(8L, declared.ValueUnits);
            Assert.AreEqual(SourceDeclared, declared.Source);
        }

        [Test]
        public void ExistingSerializedValueIsPreservedWithoutADeclaration()
        {
            // The un-migrated scene path: StoryLevel's 110 props and CpuSkirmish have no
            // placement.json yet, so a menu sync must leave every value alone.
            ValueResolution preserved = Resolve(0L, false, 16L, true);
            Assert.IsTrue(preserved.Accepted);
            Assert.AreEqual(16L, preserved.ValueUnits);
            Assert.AreEqual(SourceExisting, preserved.Source);

            // Zero is preserved bit-for-bit too: "fix" would be a reprice the old scene never
            // asked for. The host reports such a row at start-up; the author decides.
            ValueResolution zero = Resolve(0L, false, 0L, true);
            Assert.IsTrue(zero.Accepted);
            Assert.AreEqual(0L, zero.ValueUnits);
            Assert.AreEqual(SourceExisting, zero.Source);
        }

        [Test]
        public void ANewRowWithNoDeclarationIsWrittenAsZeroAndNotDropped()
        {
            // Neither a declaration nor an existing value: the row is still written, with 0,
            // and the sync report names it. Dropping the row would lose the prop's geometry
            // and pickup wiring over a missing number.
            ValueResolution none = Resolve(0L, false, 0L, false);
            Assert.IsTrue(none.Accepted);
            Assert.AreEqual(0L, none.ValueUnits);
            Assert.AreEqual(SourceNone, none.Source);
        }

        [Test]
        public void AKindTierWithoutAnOldTableRowStillResolves()
        {
            // container m is the pair the old ValueTable never had (PICNIC_CORNER_LEVEL_ONE
            // C5). The resolver has no kind or tier parameter at all, so such a pair cannot
            // be refused or dropped any more: the declared number is the whole input.
            ValueResolution containerMedium = Resolve(16L, true, 0L, false);
            Assert.IsTrue(containerMedium.Accepted);
            Assert.AreEqual(16L, containerMedium.ValueUnits);
            Assert.AreEqual(SourceDeclared, containerMedium.Source);
        }

        [Test]
        public void AnInvalidDeclarationIsRefusedInsteadOfFallingBack()
        {
            // A declared 0 or a negative number is a data error, not "keep the old value":
            // silently preserving a different number than the level states is exactly the
            // disagreement U2 removes. The sync reports the row as rejected.
            ValueResolution zero = Resolve(0L, true, 8L, true);
            Assert.IsFalse(zero.Accepted);
            Assert.AreEqual(0L, zero.ValueUnits);
            Assert.IsNull(zero.Source);

            ValueResolution negative = Resolve(-3L, true, 8L, true);
            Assert.IsFalse(negative.Accepted);
            Assert.IsNull(negative.Source);
        }

        [Test]
        public void ReadBackMismatchIsReportedPerItem()
        {
            var declared = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                { "apple", 8L },
                { "pear", 4L },
            };

            // Host rows are index-aligned; matching is by item id so a reordered row still
            // reports the right prop.
            string[] mismatches = Mismatches(new[] { "pear", "apple" }, new[] { 4L, 2L }, declared);

            Assert.AreEqual(1, mismatches.Length);
            StringAssert.Contains("apple: host=2, declared=8", mismatches[0]);
        }

        [Test]
        public void MatchingReadBackHasNoMismatch()
        {
            var declared = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                { "apple", 8L },
                { "pear", 4L },
            };

            CollectionAssert.IsEmpty(Mismatches(new[] { "apple", "pear" }, new[] { 8L, 4L }, declared));
        }

        [Test]
        public void DeclaredIdWithoutAHostRowIsReported()
        {
            // The sync dropped the prop (bad @size, duplicate id, degenerate AABB, ...) or the
            // caller passed an id outside this field. Either way the declared total cannot be
            // trusted, so the missing id must be named.
            var declared = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                { "apple", 8L },
                { "pear", 4L },
            };

            string[] missing = MissingRows(new[] { "apple" }, declared);

            CollectionAssert.AreEqual(new[] { "pear" }, missing);
        }

        [Test]
        public void AValuePastIntMaxSurvivesDeclarationHostReadBackAndLedger()
        {
            // Docs/OPEN_DECISIONS.md (r) step B: the declaration, the host column and the
            // read-back are 64-bit. This walks the pure-logic path of one row worth
            // 3,000,000,000 units - past int.MaxValue (2,147,483,647), so an int column or an
            // int range guard anywhere on the way would either refuse the row or wrap the
            // number. Nothing here touches a scene, a prefab or a SerializedObject.
            const long pastIntMax = 3000000000L;

            // 1. The declaration. The importer's guard accepts the number unchanged; before
            //    step B it refused everything from 2^31 up, even though the ledger could hold it.
            ValueResolution declared = Resolve(pastIntMax, true, 0L, false);
            Assert.IsTrue(declared.Accepted, "a 64-bit declaration is a value like any other");
            Assert.AreEqual(pastIntMax, declared.ValueUnits, "the declaration must not be narrowed");
            Assert.AreEqual(SourceDeclared, declared.Source);

            // 2. The host row and the read-back. The sync writes valueUnits as a long[] row and
            //    compares it by item id, so the value must come back bit-for-bit. An int host
            //    row would report a mismatch here instead of accepting a different number.
            var written = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                { "huge_rock", pastIntMax },
            };
            long[] hostRows = { pastIntMax };
            CollectionAssert.IsEmpty(Mismatches(new[] { "huge_rock" }, hostRows, written),
                "the host row must read back as declared");

            // 3. The ledger. Since steps C+D the host hands the motor a `long` growth
            //    (`propGrowthMm3`), so there is no float hop left on this path: the gain is
            //    the column's number, exactly.
            Assert.IsTrue(PickupAbsorbMath.TryAbsorbGrowthToLedgerGain(hostRows[0], out long gain),
                "a 64-bit declaration must map to a ledger gain");
            Assert.AreEqual(pastIntMax, gain, "the gain must be the declared number");

            LedgerTestHost ledger = new LedgerTestHost();
            Assert.IsTrue(ledger.Join("a"));
            Assert.IsTrue(ledger.Transition(LedgerPhase.Ready, 0f));
            Assert.IsTrue(ledger.Transition(LedgerPhase.Preparing, 1f));
            Assert.IsTrue(ledger.Transition(LedgerPhase.Playing, 2f));
            Assert.IsTrue(ledger.SubmitClaim("huge_rock", 0, "a", 0, 1));
            Assert.IsTrue(ledger.TryResolve("huge_rock", 0, gain, out int commitRow),
                "the 64-bit gain must resolve");
            Assert.AreEqual(pastIntMax, ledger.CommitGains[commitRow],
                "the commit row must store the gain exactly");
            Assert.AreEqual(pastIntMax, ledger.Confirmed("a"),
                "the confirmed volume must be exact, not wrapped or saturated");
        }

        [Test]
        public void ALongGrowthPassesThroughUnchangedAtAnyMagnitude()
        {
            // The candidate queue carries a long now (steps C+D), so the retired float-slot
            // rounding is gone: 2^31+1 and long.MaxValue both arrive unchanged.
            Assert.IsTrue(PickupAbsorbMath.TryAbsorbGrowthToLedgerGain(2147483649L, out long odd));
            Assert.AreEqual(2147483649L, odd);
            Assert.IsTrue(PickupAbsorbMath.TryAbsorbGrowthToLedgerGain(long.MaxValue, out long top));
            Assert.AreEqual(long.MaxValue, top);
        }

        // ------------------------------------------------------------- reflection ---

        private struct ValueResolution
        {
            public bool Accepted;
            public long ValueUnits;
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

        private static ValueResolution Resolve(long declaredValueUnits, bool hasDeclaredValueUnits,
            long existingValueUnits, bool hasExistingValueUnits)
        {
            object[] arguments = { declaredValueUnits, hasDeclaredValueUnits, existingValueUnits,
                hasExistingValueUnits, 0L, null };
            bool accepted = (bool)RulesMethod("TryResolveValueUnits").Invoke(null, arguments);

            var resolution = new ValueResolution();
            resolution.Accepted = accepted;
            resolution.ValueUnits = arguments[4] is long units ? units : 0L;
            resolution.Source = arguments[5] as string;
            return resolution;
        }

        private static string[] Mismatches(string[] itemIds, long[] hostValueUnits,
            Dictionary<string, long> declaredValueUnits)
        {
            object[] arguments = { itemIds, hostValueUnits, declaredValueUnits };
            return (string[])RulesMethod("FindValueUnitMismatches").Invoke(null, arguments);
        }

        private static string[] MissingRows(string[] itemIds, Dictionary<string, long> declaredValueUnits)
        {
            object[] arguments = { itemIds, declaredValueUnits };
            return (string[])RulesMethod("FindDeclaredIdsWithoutRows").Invoke(null, arguments);
        }
    }
}
