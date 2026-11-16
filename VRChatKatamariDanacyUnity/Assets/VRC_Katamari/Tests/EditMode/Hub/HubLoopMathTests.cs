using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Hub;
using VRCKatamari.Story;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the room/level loop's rules: which round state a start and a
    /// return are allowed from, which public call opens a round, how two presses are
    /// de-duplicated and paced, and how "the player is at the point" is measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the static rule class is touched. The launcher (HubLevelLauncher) owns the
    /// timers, the pending transfer and the teleports, which need a scene; everything it
    /// decides is here, so a wrong decision is caught without opening Unity.
    /// </para>
    /// <para>
    /// The state values come from StoryRoundController's public constants rather than
    /// literals, for the same reason the launcher does: if a state value ever moves, the
    /// rules and the round must move together, and a test that hardcoded 1 would pass
    /// while the world broke.
    /// </para>
    /// <para>
    /// These tests do not and cannot prove that a teleport moves the player, that a
    /// station blocks one, or that the exit lands before a move. Those are the
    /// unverified platform facts the plan lists (HUB_LOOP_PLAN sections 5 and 10); the
    /// probe in Labs/HubLoop answers the first, and the first Play Mode run of this loop
    /// answers the other two.
    /// </para>
    /// </remarks>
    public class HubLoopMathTests
    {
        // ------------------------------------------------------------------ start mode

        [Test]
        public void StartingFromIdleBeginsTheRoundDirectly()
        {
            int startMode;

            // Idle means nothing has run: there is no ball to reset and no props to
            // restore, so BeginRound() is the whole start.
            Assert.IsTrue(HubLoopMath.TryStartMode(StoryRoundController.StateIdle, out startMode));
            Assert.AreEqual(HubLoopMath.StartModeBeginRound, startMode);
        }

        [Test]
        public void StartingFromResultResetsThroughTheRoundsOwnRetry()
        {
            int startMode;

            // Result means the ball is at its settled size and the props are attached.
            // RequestRetry() is the round's existing reset+begin, so the launcher must not
            // grow a second reset.
            Assert.IsTrue(HubLoopMath.TryStartMode(StoryRoundController.StateResult, out startMode));
            Assert.AreEqual(HubLoopMath.StartModeResetThenBegin, startMode);
        }

        [Test]
        public void StartingWhileRunningOrPresentingIsRefused()
        {
            int startMode;

            Assert.IsFalse(HubLoopMath.TryStartMode(StoryRoundController.StateRunning, out startMode));
            Assert.AreEqual(HubLoopMath.StartModeNone, startMode);

            Assert.IsFalse(HubLoopMath.TryStartMode(StoryRoundController.StatePresenting, out startMode));
            Assert.AreEqual(HubLoopMath.StartModeNone, startMode);

            // A state value outside the round's four is a wiring problem, not a state to
            // guess about.
            Assert.IsFalse(HubLoopMath.TryStartMode(99, out startMode));
            Assert.AreEqual(HubLoopMath.StartModeNone, startMode);
        }

        // ------------------------------------------------------------------ start decision

        [Test]
        public void StartIsAcceptedFromIdleAndFromResult()
        {
            int reason;

            Assert.IsTrue(HubLoopMath.TryDecideStart(StoryRoundController.StateIdle, 0, 1,
                false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonNone, reason);

            Assert.IsTrue(HubLoopMath.TryDecideStart(StoryRoundController.StateResult, 0, 1,
                false, 100f, 50f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonNone, reason);
        }

        [Test]
        public void StartIsRefusedWhileARoundRunsOrPresents()
        {
            int reason;

            Assert.IsFalse(HubLoopMath.TryDecideStart(StoryRoundController.StateRunning, 0, 1,
                false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonStartNotInIdleOrResult, reason);

            Assert.IsFalse(HubLoopMath.TryDecideStart(StoryRoundController.StatePresenting, 0, 1,
                false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonStartNotInIdleOrResult, reason);
        }

        [Test]
        public void StartIsRefusedForAnIndexWithNoLevelEntry()
        {
            int reason;

            // Three levels, so 0..2 are usable.
            Assert.IsTrue(HubLoopMath.TryDecideStart(StoryRoundController.StateIdle, 2, 3,
                false, 100f, -1f, 1f, out reason));

            Assert.IsFalse(HubLoopMath.TryDecideStart(StoryRoundController.StateIdle, 3, 3,
                false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonLevelIndexOutOfRange, reason);

            Assert.IsFalse(HubLoopMath.TryDecideStart(StoryRoundController.StateIdle, -1, 3,
                false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonLevelIndexOutOfRange, reason);

            // A launcher with no entries wired cannot start anything.
            Assert.IsFalse(HubLoopMath.TryDecideStart(StoryRoundController.StateIdle, 0, 0,
                false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonLevelIndexOutOfRange, reason);
        }

        [Test]
        public void StartIsRefusedWhileATransferIsInFlight()
        {
            int reason;

            // A start takes more than one frame here (arrival is measured before the
            // round opens), so a second press must not queue a second transfer.
            Assert.IsFalse(HubLoopMath.TryDecideStart(StoryRoundController.StateIdle, 0, 1,
                true, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonTransferPending, reason);
        }

        [Test]
        public void StartIsRefusedDuringTheCooldown()
        {
            int reason;

            // Last transfer at t=100, cooldown 1 s: at t=100.5 the press is refused, at
            // t=101 it is accepted (the edge is inclusive).
            Assert.IsFalse(HubLoopMath.TryDecideStart(StoryRoundController.StateIdle, 0, 1,
                false, 100.5f, 100f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonCooldown, reason);

            Assert.IsTrue(HubLoopMath.TryDecideStart(StoryRoundController.StateIdle, 0, 1,
                false, 101f, 100f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonNone, reason);
        }

        [Test]
        public void TheFirstPressIsNotBlockedByTheCooldownSentinel()
        {
            int reason;

            // The launcher starts at lastTransferSeconds = -1 ("no transfer yet"), and
            // Time.time itself starts near zero, so the sentinel must not read as
            // "a transfer just happened" - otherwise the very first press of the world
            // would be refused for the first second.
            Assert.IsTrue(HubLoopMath.TryDecideStart(StoryRoundController.StateIdle, 0, 1,
                false, 0.1f, -1f, 5f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonNone, reason);

            Assert.IsFalse(HubLoopMath.IsCoolingDown(0.1f, -1f, 5f));
        }

        // ------------------------------------------------------------------ return decision

        [Test]
        public void ReturnIsAllowedOnceTheRoundHasSettled()
        {
            int reason;

            Assert.IsTrue(HubLoopMath.TryDecideReturn(StoryRoundController.StatePresenting,
                false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonNone, reason);

            Assert.IsTrue(HubLoopMath.TryDecideReturn(StoryRoundController.StateResult,
                false, 100f, 50f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonNone, reason);
        }

        [Test]
        public void ReturnIsAllowedInIdleBecauseNothingIsRunning()
        {
            int reason;

            // Idle is the "nothing has started" state: a move to the room cannot break a
            // round or unfreeze facts, and it gives the operator one plain "go to the
            // room" press while building the loop.
            Assert.IsTrue(HubLoopMath.TryDecideReturn(StoryRoundController.StateIdle,
                false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonNone, reason);
        }

        [Test]
        public void ReturnIsRefusedWhileARoundRuns()
        {
            int reason;

            // The one hard refusal: leaving the level mid-round would leave a running
            // clock over an empty level (HUB_LOOP_PLAN section 2), and the round has no
            // public "end now" to abandon it with.
            Assert.IsFalse(HubLoopMath.TryDecideReturn(StoryRoundController.StateRunning,
                false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonRoundStillRunning, reason);
        }

        [Test]
        public void ReturnIsRefusedForAnUnknownStateAndForAPendingTransfer()
        {
            int reason;

            Assert.IsFalse(HubLoopMath.TryDecideReturn(42, false, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonReturnNotAllowedInState, reason);

            Assert.IsFalse(HubLoopMath.TryDecideReturn(StoryRoundController.StateResult,
                true, 100f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonTransferPending, reason);
        }

        [Test]
        public void ReturnIsRefusedDuringTheCooldown()
        {
            int reason;

            Assert.IsFalse(HubLoopMath.TryDecideReturn(StoryRoundController.StateResult,
                false, 100.2f, 100f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonCooldown, reason);
        }

        // ------------------------------------------------------------------ cooldown

        [Test]
        public void ACooldownOfZeroNeverBlocks()
        {
            // Fails the "cooldown off" case the operator may set while building.
            Assert.IsFalse(HubLoopMath.IsCoolingDown(100f, 99.999f, 0f));
        }

        [Test]
        public void AnUnusableClockOrCooldownFailsClosed()
        {
            // NaN compares false against every threshold, so a bare comparison would let
            // an unusable reading through and leave the loop unpaced.
            Assert.IsTrue(HubLoopMath.IsCoolingDown(float.NaN, 1f, 1f));
            Assert.IsTrue(HubLoopMath.IsCoolingDown(1f, float.NaN, 1f));
            Assert.IsTrue(HubLoopMath.IsCoolingDown(1f, 1f, float.NaN));
            Assert.IsTrue(HubLoopMath.IsCoolingDown(float.PositiveInfinity, 1f, 1f));
        }

        [Test]
        public void AClockThatWentBackwardsStillBlocks()
        {
            // A stamp from the future (a scene reload resets Time.time but not the field)
            // reads as "just transferred", which refuses instead of allowing a burst.
            Assert.IsTrue(HubLoopMath.IsCoolingDown(1f, 100f, 1f));
        }

        // ------------------------------------------------------------------ geometry

        [Test]
        public void WithinIsInclusiveAtTheTolerance()
        {
            Vector3 point = new Vector3(3f, 0.5f, 7f);

            // Distance 0.5 against a 0.5 m tolerance: the plan's acceptance test is
            // "within 0.5 m", so the edge must count as arrived.
            Assert.IsTrue(HubLoopMath.IsWithin(point, point, 0f));
            Assert.IsTrue(HubLoopMath.IsWithin(point, point + new Vector3(0.5f, 0f, 0f), 0.5f));
            Assert.IsFalse(HubLoopMath.IsWithin(point, point + new Vector3(0.5f, 0f, 0f), 0.49f));
            Assert.IsTrue(HubLoopMath.IsWithin(point, point + new Vector3(0f, -0.9f, 0f), 1f));
        }

        [Test]
        public void WithinRefusesUnusableInputs()
        {
            Vector3 point = new Vector3(1f, 2f, 3f);
            Vector3 notFinite = new Vector3(float.NaN, 0f, 0f);

            // False, not "far away": the launcher reads a false as "not there yet", which
            // must never be caused by an arithmetic artefact.
            Assert.IsFalse(HubLoopMath.IsWithin(notFinite, point, 10f));
            Assert.IsFalse(HubLoopMath.IsWithin(point, notFinite, 10f));
            Assert.IsFalse(HubLoopMath.IsWithin(point, point, float.NaN));
            Assert.IsFalse(HubLoopMath.IsWithin(point, point, -1f));
            Assert.IsFalse(HubLoopMath.IsWithin(point, new Vector3(float.PositiveInfinity, 0f, 0f), 1e9f));
        }

        [Test]
        public void LiftedPointAddsTheLiftOnYOnly()
        {
            Vector3 lifted;

            Assert.IsTrue(HubLoopMath.TryLiftedPoint(new Vector3(4f, 0f, -2f), 0.1f, out lifted));
            Assert.AreEqual(4f, lifted.x, 1e-6f);
            Assert.AreEqual(0.1f, lifted.y, 1e-5f);
            Assert.AreEqual(-2f, lifted.z, 1e-6f);

            // Zero lift is legal: a point already above the floor needs none.
            Assert.IsTrue(HubLoopMath.TryLiftedPoint(new Vector3(0f, 5f, 0f), 0f, out lifted));
            Assert.AreEqual(5f, lifted.y, 1e-6f);
        }

        [Test]
        public void LiftedPointRefusesANegativeLiftAndNonFiniteInput()
        {
            Vector3 lifted;

            // A negative lift would start the capsule under the floor, which reads as
            // "the teleport slid me away"; refusing is better than clamping silently.
            Assert.IsFalse(HubLoopMath.TryLiftedPoint(Vector3.zero, -0.1f, out lifted));
            Assert.IsFalse(HubLoopMath.TryLiftedPoint(Vector3.zero, float.NaN, out lifted));
            Assert.IsFalse(HubLoopMath.TryLiftedPoint(new Vector3(0f, float.NaN, 0f), 0.1f, out lifted));
            Assert.IsFalse(HubLoopMath.TryLiftedPoint(new Vector3(float.NaN, 0f, 0f), 0.1f, out lifted));
        }

        // ------------------------------------------------------------------ hold in level

        [Test]
        public void ThePlayerIsHeldOnlyWhileARoundRunsAndIsOutsideTheIsland()
        {
            // Running + outside: the one case the correction exists for (a menu respawn
            // lands at the room, a fall out of the world does the same).
            Assert.IsTrue(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StateRunning,
                false, 0, 3));

            // Inside the island: nothing to do.
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StateRunning,
                true, 0, 3));

            // Every other state has the player in the room by design, so pulling them
            // into the level would be the bug, not the fix.
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StateIdle,
                false, 0, 3));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StatePresenting,
                false, 0, 3));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StateResult,
                false, 0, 3));
        }

        [Test]
        public void TheHoldStopsAtItsAttemptLimit()
        {
            // A teleport that keeps failing must not become a per-frame loop: three
            // attempts, then the round is left to settle on its own.
            Assert.IsTrue(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StateRunning,
                false, 2, 3));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StateRunning,
                false, 3, 3));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StateRunning,
                false, 0, 0));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StateRunning,
                false, 0, -1));
        }

        [Test]
        public void ASeatedPlayerIsNeverHeldEvenWhenThePositionLooksWrong()
        {
            // A seated avatar is carried by the station and a client can report its
            // position at the seat, the station or somewhere in between. The station's
            // seated flag is the reliable fact, so it wins over any distance reading.
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, true, 100f, 0f, 2.5f, 0, 3));

            // The same reading with nobody seated is exactly the case the hold exists for.
            Assert.IsTrue(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 100f, 0f, 2.5f, 0, 3));

            // Inside the island still means no correction.
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                true, false, 100f, 0f, 2.5f, 0, 3));
        }

        [Test]
        public void TheHoldWaitsOutItsGraceWindowAfterAStart()
        {
            // The 2026-11-16 ClientSim run had the hold pull a respawned player back three
            // times in the first second of a round. A start is teleport -> measure -> seat;
            // nothing may move the player during that window.
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 10f, 9f, 2.5f, 0, 3));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 11f, 9f, 2.5f, 0, 3));
            Assert.IsTrue(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 11.5f, 9f, 2.5f, 0, 3));

            // No start recorded yet (-1): no window to wait for.
            Assert.IsTrue(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 1f, -1f, 2.5f, 0, 3));
        }

        [Test]
        public void TheHoldRefusesUnusableReadings()
        {
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, float.NaN, 0f, 2.5f, 0, 3));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 100f, float.NaN, 2.5f, 0, 3));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 100f, 0f, float.NaN, 0, 3));
        }

        [Test]
        public void TheSeatedAwareHoldStillStopsAtItsAttemptLimit()
        {
            Assert.IsTrue(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 100f, 0f, 2.5f, 2, 3));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 100f, 0f, 2.5f, 3, 3));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 100f, 0f, 2.5f, 0, 0));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevelNow(StoryRoundController.StateRunning,
                false, false, 100f, 0f, 2.5f, 0, -1));
        }

        [Test]
        public void ASeatRequestIsRetriedInsideItsWindowAndStopsAtTheLimit()
        {
            // Unanswered after the interval: ask again.
            Assert.IsTrue(HubLoopMath.ShouldRetrySeat(false, 1, 3, 10, 10));
            // Too soon, already successful, or out of attempts: no.
            Assert.IsFalse(HubLoopMath.ShouldRetrySeat(false, 1, 3, 9, 10));
            Assert.IsFalse(HubLoopMath.ShouldRetrySeat(true, 1, 3, 10, 10));
            Assert.IsFalse(HubLoopMath.ShouldRetrySeat(false, 3, 3, 10, 10));
            Assert.IsFalse(HubLoopMath.ShouldRetrySeat(false, 1, 0, 10, 10));
            // A negative frame delta is not a retry window.
            Assert.IsFalse(HubLoopMath.ShouldRetrySeat(false, 1, 3, -1, 10));
            // Zero interval means "retry now" while attempts remain.
            Assert.IsTrue(HubLoopMath.ShouldRetrySeat(false, 1, 3, 0, 0));
        }

        // ------------------------------------------------------------------ reason text

        [Test]
        public void EveryReasonCodeHasText()
        {
            // The log line is the only thing a refused press produces, so a code without
            // text would be a silent refusal.
            int[] codes = { HubLoopMath.ReasonNone, HubLoopMath.ReasonTransferPending,
                HubLoopMath.ReasonCooldown, HubLoopMath.ReasonLevelIndexOutOfRange,
                HubLoopMath.ReasonStartNotInIdleOrResult, HubLoopMath.ReasonRoundStillRunning,
                HubLoopMath.ReasonReturnNotAllowedInState, HubLoopMath.ReasonNoLocalPlayer,
                HubLoopMath.ReasonPositionNotFinite, HubLoopMath.ReasonLevelEntryUnwired,
                HubLoopMath.ReasonArrivalNotReached, HubLoopMath.ReasonRoundStateChanged,
                HubLoopMath.ReasonRoundRefusedRestart, HubLoopMath.ReasonNoController,
                HubLoopMath.ReasonNoRoomPoint };

            for (int i = 0; i < codes.Length; i++)
            {
                string text = HubLoopMath.DescribeReason(codes[i]);
                Assert.IsNotNull(text);
                Assert.IsTrue(text.Length > 0);
            }

            // An unmapped number still says something instead of returning null.
            Assert.AreEqual("unknown reason", HubLoopMath.DescribeReason(1234));
        }

        [Test]
        public void RoundStatePredicateAcceptsOnlyTheRoundsFourValues()
        {
            Assert.IsTrue(HubLoopMath.IsRoundState(StoryRoundController.StateIdle));
            Assert.IsTrue(HubLoopMath.IsRoundState(StoryRoundController.StateRunning));
            Assert.IsTrue(HubLoopMath.IsRoundState(StoryRoundController.StatePresenting));
            Assert.IsTrue(HubLoopMath.IsRoundState(StoryRoundController.StateResult));
            Assert.IsFalse(HubLoopMath.IsRoundState(-1));
            Assert.IsFalse(HubLoopMath.IsRoundState(4));
        }
    }
}
