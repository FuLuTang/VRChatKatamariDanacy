using NUnit.Framework;
using VRCKatamari.Economy;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the 64-bit range of the ledger's quantity columns and its quantity guards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Docs/OPEN_DECISIONS.md (r) decided that volume is a fixed-point <c>long</c>
    /// quantity, and that migration step A only widens the type: the unit, the arithmetic,
    /// the refusal reason codes and every stored number stay EXACTLY as they were. The
    /// other ledger tests pin those numbers (small integers such as 3/4/5/6/7/11); this
    /// file pins the one thing they cannot see, which is the reason the step exists:
    /// </para>
    /// <list type="bullet">
    /// <item>A SUM past <c>int.MaxValue</c> (2,147,483,647) is exact, not wrapped. The probe
    /// total is 3,000,000,000 units, reached both from gains that each fit in an int and
    /// from one gain that does not: under the old 32-bit columns the first case wrapped
    /// negative and the second was refused outright.</item>
    /// <item>Banking moves exactly that amount and keeps the total held constant, so a
    /// 3,000,000,000 total survives the transaction that used to be impossible.</item>
    /// <item>The four quantity guards are 64-bit guards: a gain, a deposit or an attacker
    /// share that would push a total past <c>long.MaxValue</c> is refused, and one that
    /// lands exactly on <c>long.MaxValue</c> is accepted. A leftover int32 guard would
    /// refuse the second case, and a missing guard would wrap the first.</item>
    /// </list>
    /// <para>
    /// Nothing here re-implements a rule: the calls go through the same
    /// <see cref="LedgerTestHost"/> the other ledger tests use, which is the runtime's own
    /// <see cref="GrowthLedgerMath"/> over caller-owned columns. The expected numbers are
    /// hand calculations (a resolved commit adds its gain once; a bank moves exactly its
    /// amount 1:1), never a re-run of an implementation formula.
    /// </para>
    /// <para>
    /// Deliberate limit: no number here is a scene or level value, so this test needs no
    /// data and no Unity asset. It is a pure EditMode test, safe to run headless.
    /// </para>
    /// </remarks>
    public class LongLedgerRangeTests
    {
        /// <summary>One more than <c>int.MaxValue</c>: the smallest sum an int cannot hold.</summary>
        private const long PastIntMax = 3000000000L;

        /// <summary>A one-player ledger in Playing, the state a commit and a bank require.</summary>
        private static LedgerTestHost Playing()
        {
            LedgerTestHost host = new LedgerTestHost();
            Assert.IsTrue(host.Join("a"));
            Assert.IsTrue(host.Transition(LedgerPhase.Ready, 0f));
            Assert.IsTrue(host.Transition(LedgerPhase.Preparing, 1f));
            Assert.IsTrue(host.Transition(LedgerPhase.Playing, 2f));
            return host;
        }

        /// <summary>Submits one claim and resolves it, returning whether the ledger accepted.</summary>
        private static bool TryCommit(LedgerTestHost host, string item, long gain)
        {
            Assert.IsTrue(host.SubmitClaim(item, 0, "a", 0, 1), "the claim must be accepted");
            int commitRow;
            return host.TryResolve(item, 0, gain, out commitRow);
        }

        /// <summary>
        /// Two gains that each fit in an int add up to 3,000,000,000 exactly.
        /// </summary>
        /// <remarks>
        /// This is the case the old int32 columns got WRONG silently: 2,000,000,000 +
        /// 1,000,000,000 wraps to a negative number, which would then fail every
        /// "confirmed volume is non-negative" validation and drop the row. The per-gain
        /// guard could not catch it because neither gain is out of range on its own.
        /// </remarks>
        [Test]
        public void SumPastIntMaxIsExact()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(TryCommit(host, "first", 2000000000L), "the first commit must resolve");
            Assert.IsTrue(TryCommit(host, "second", 1000000000L), "the second commit must resolve");

            Assert.AreEqual(3000000000L, host.Confirmed("a"), "the confirmed sum must be exact");
            Assert.AreEqual(2000000000L + 1000000000L, host.AbsorbedTotal(),
                "the committed gains must sum exactly");
            Assert.AreEqual(host.AbsorbedTotal(), host.TotalHeld(),
                "value held must equal value committed");
        }

        /// <summary>One gain past int.MaxValue is a real gain, not a refusal.</summary>
        /// <remarks>
        /// The gain argument is a 64-bit ledger quantity (migration step A), so an item
        /// worth 3,000,000,000 units is accounting input like any other. The pickup-side
        /// value gate has the matching 64-bit bound; see PickupAbsorbTests.
        /// </remarks>
        [Test]
        public void SingleGainPastIntMaxIsAccepted()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(TryCommit(host, "huge", PastIntMax), "a 64-bit gain must resolve");
            Assert.AreEqual(PastIntMax, host.Confirmed("a"));
            Assert.AreEqual(PastIntMax, host.CommitGains[0], "the commit row must store the gain exactly");
        }

        /// <summary>A 3,000,000,000 total banks 1:1 and the total held does not move.</summary>
        [Test]
        public void BankMovesPastIntMaxExactly()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(TryCommit(host, "huge", PastIntMax));

            Assert.IsTrue(host.Bank("a", 2500000000L, "req-big"),
                "banking 2,500,000,000 of 3,000,000,000 must be accepted");
            Assert.AreEqual(500000000L, host.Confirmed("a"), "the remainder must be exact");
            Assert.AreEqual(2500000000L, host.Banked("a"), "the banked score must be exact");
            Assert.AreEqual(PastIntMax, host.TotalHeld(), "banking must not create or destroy value");

            Assert.IsTrue(host.Bank("a", 500000000L, "req-rest"), "the remainder must bank too");
            Assert.AreEqual(0L, host.Confirmed("a"));
            Assert.AreEqual(PastIntMax, host.Banked("a"));
            Assert.AreEqual(PastIntMax, host.TotalHeld());
        }

        /// <summary>
        /// The commit guard is a long guard: exactly the top of the range is accepted, and
        /// one unit past it is refused without touching the stores.
        /// </summary>
        /// <remarks>
        /// The row is seeded directly because no sequence of real commits reaches
        /// long.MaxValue in a test. Seeding a column is how the other ledger tests describe
        /// an unusual input too (they write a negative row to prove the validation).
        /// </remarks>
        [Test]
        public void CommitGuardIsSixtyFourBit()
        {
            LedgerTestHost host = Playing();
            int row = host.PlayerRow("a");

            host.ConfirmedVolumes[row] = long.MaxValue - 10L;
            Assert.IsTrue(TryCommit(host, "fills-to-max", 10L),
                "a gain that lands exactly on long.MaxValue must be accepted");
            Assert.AreEqual(long.MaxValue, host.Confirmed("a"), "the total must be exactly long.MaxValue");

            // Same row, one unit more than the range allows: refused, and the refusal
            // leaves the confirmed total and the commit table untouched.
            int commitsBefore = host.CommitCount;
            Assert.IsFalse(TryCommit(host, "one-too-many", 1L),
                "a gain past long.MaxValue must be refused");
            Assert.AreEqual(long.MaxValue, host.Confirmed("a"),
                "a refused gain must not change the confirmed total");
            Assert.AreEqual(commitsBefore, host.CommitCount,
                "a refused gain must not append a commit row");
        }

        /// <summary>The bank guard is a long guard, on the banked column.</summary>
        [Test]
        public void BankGuardIsSixtyFourBit()
        {
            LedgerTestHost host = Playing();
            int row = host.PlayerRow("a");

            host.BankedScores[row] = long.MaxValue - 10L;
            host.ConfirmedVolumes[row] = 10L;
            Assert.IsTrue(host.Bank("a", 10L, "fills-to-max"),
                "a deposit that lands exactly on long.MaxValue must be accepted");
            Assert.AreEqual(long.MaxValue, host.Banked("a"));
            Assert.AreEqual(0L, host.Confirmed("a"));

            host.ConfirmedVolumes[row] = 1L;
            Assert.IsFalse(host.Bank("a", 1L, "one-too-many"),
                "a deposit past long.MaxValue must be refused");
            Assert.AreEqual(long.MaxValue, host.Banked("a"),
                "a refused deposit must not change the banked score");
            Assert.AreEqual(1L, host.Confirmed("a"),
                "a refused deposit must not move the confirmed volume");
        }

        /// <summary>The attacker-share guard is a long guard, on the attacker's column.</summary>
        /// <remarks>
        /// A KO moves at most the victim's own unbanked value, so the share here is small
        /// and the attacker's seeded total is what decides the guard. A refused KO changes
        /// nothing: the victim keeps its volume and stays Active.
        /// </remarks>
        [Test]
        public void AttackerShareGuardIsSixtyFourBit()
        {
            LedgerTestHost host = Playing();
            Assert.IsTrue(host.Join("b"));

            int attackerRow = host.PlayerRow("b");
            host.ConfirmedVolumes[attackerRow] = long.MaxValue - 5L;
            host.ConfirmedVolumes[host.PlayerRow("a")] = 10L;

            Assert.IsFalse(host.Ko("a", "b", 5f, 1f, 10L),
                "a share that would overflow the attacker's total must be refused");
            Assert.AreEqual(10L, host.Confirmed("a"),
                "a refused KO must leave the victim's volume in place");
            Assert.AreEqual(PlayerLifecycle.Active, host.Life("a"),
                "a refused KO must leave the victim Active");
            Assert.AreEqual(long.MaxValue - 5L, host.Confirmed("b"),
                "a refused KO must leave the attacker's total in place");

            // The share that fits is accepted: 5 + 5 lands exactly on long.MaxValue.
            Assert.IsTrue(host.Ko("a", "b", 6f, 1f, 5L),
                "a share that fits in the remaining range must be accepted");
            Assert.AreEqual(5L, host.Confirmed("b") - (long.MaxValue - 5L),
                "the attacker must receive exactly the share");
            Assert.AreEqual(0L, host.Confirmed("a"), "a KO clears the victim's unbanked volume");
        }
    }
}
