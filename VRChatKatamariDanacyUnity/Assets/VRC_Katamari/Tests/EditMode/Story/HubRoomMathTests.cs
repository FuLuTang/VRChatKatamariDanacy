using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the room side of the loop: the Hub -> LevelLoading -> Playing ->
    /// Result -> (retry | Hub) phase machine, the "exactly one level circle" rule, the
    /// re-arm/dwell rule that makes a walk-in start safe, and the one-line prompt gating.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the static rule class is touched, so the whole loop is driven with state
    /// constants and booleans and no scene, no player and no teleport is involved. The
    /// host (<c>HubRoomHost</c>) only reads the player position, calls these rules and
    /// writes one Text; the launcher still owns every transfer and the round still owns
    /// every round fact, so a wrong rule shows up here instead of only in Play Mode.
    /// </para>
    /// <para>
    /// The state values come from <c>StoryRoundController</c>'s public constants, not from
    /// literals: if a state value moves, the room and the round have to move together, and
    /// a test that hardcoded 1 would keep passing while the room showed the wrong phase.
    /// </para>
    /// <para>
    /// What these tests cannot prove: that a teleport moves the player, that a Station
    /// blocks or allows one, or that a client delivers <c>GetPosition()</c> at the rate the
    /// dwell assumes. Those are the platform facts listed in HUB_LOOP_PLAN section 10 and
    /// HUB_ROOM.md's "not done" list; the probe in Labs/HubLoop answers the first.
    /// </para>
    /// </remarks>
    public class HubRoomMathTests
    {
        // ------------------------------------------------------------------ phases

        private static int Advance(int phase, int roundState, bool startAccepted, bool startFailed,
            bool playerInHub, bool holdElapsed)
        {
            int next;
            Assert.IsTrue(HubRoomMath.TryAdvance(phase, roundState, startAccepted, startFailed,
                playerInHub, holdElapsed, out next), "the inputs should be usable");
            return next;
        }

        [Test]
        public void AFreshRoomStartsInHub()
        {
            Assert.AreEqual(HubRoomMath.PhaseHub,
                Advance(HubRoomMath.PhaseHub, StoryRoundController.StateIdle, false, false,
                    true, false));
        }

        [Test]
        public void AnAcceptedStartMovesTheRoomToLevelLoading()
        {
            Assert.AreEqual(HubRoomMath.PhaseLoading,
                Advance(HubRoomMath.PhaseHub, StoryRoundController.StateIdle, true, false,
                    true, false));
        }

        [Test]
        public void LoadingWaitsUntilTheRoundIsRunning()
        {
            // Idle for a frame or two is normal: the launcher teleports first and only calls
            // BeginRound after the arrival is measured.
            Assert.AreEqual(HubRoomMath.PhaseLoading,
                Advance(HubRoomMath.PhaseLoading, StoryRoundController.StateIdle, false, false,
                    false, false));
            Assert.AreEqual(HubRoomMath.PhasePlaying,
                Advance(HubRoomMath.PhaseLoading, StoryRoundController.StateRunning, false, false,
                    false, false));
        }

        [Test]
        public void AStartThatNeverRunsFallsBackToHub()
        {
            Assert.AreEqual(HubRoomMath.PhaseHub,
                Advance(HubRoomMath.PhaseLoading, StoryRoundController.StateIdle, false, true,
                    true, false));
        }

        [Test]
        public void ARunningRoundIsPlaying()
        {
            Assert.AreEqual(HubRoomMath.PhasePlaying,
                Advance(HubRoomMath.PhasePlaying, StoryRoundController.StateRunning, false, false,
                    false, false));
        }

        [Test]
        public void ASettledRoundIsTheResultPhase()
        {
            // Presenting and Result both mean "the facts are frozen and the card is up";
            // the room does not need two phases for the round's presentation beat.
            Assert.AreEqual(HubRoomMath.PhaseResult,
                Advance(HubRoomMath.PhasePlaying, StoryRoundController.StatePresenting, false, false,
                    false, false));
            Assert.AreEqual(HubRoomMath.PhaseResult,
                Advance(HubRoomMath.PhasePlaying, StoryRoundController.StateResult, false, false,
                    false, false));
        }

        [Test]
        public void TheResultHoldsInTheRoomUntilItsHoldTime()
        {
            Assert.AreEqual(HubRoomMath.PhaseResult,
                Advance(HubRoomMath.PhaseResult, StoryRoundController.StateResult, false, false,
                    true, false));
            Assert.AreEqual(HubRoomMath.PhaseHub,
                Advance(HubRoomMath.PhaseResult, StoryRoundController.StateResult, false, false,
                    true, true));
        }

        [Test]
        public void TheResultDoesNotCountAsHomeWhileThePlayerIsStillInTheLevel()
        {
            // A failed return teleport: the round is settled but the player never came back.
            // Showing Hub here would be a lie and would let the room prompt a retry the
            // player cannot reach.
            Assert.AreEqual(HubRoomMath.PhaseResult,
                Advance(HubRoomMath.PhaseResult, StoryRoundController.StateResult, false, false,
                    false, true));
        }

        [Test]
        public void RetryGoesBackThroughLevelLoading()
        {
            Assert.AreEqual(HubRoomMath.PhaseLoading,
                Advance(HubRoomMath.PhaseResult, StoryRoundController.StateResult, true, false,
                    true, true));
            Assert.AreEqual(HubRoomMath.PhasePlaying,
                Advance(HubRoomMath.PhaseLoading, StoryRoundController.StateRunning, false, false,
                    false, false));
        }

        [Test]
        public void AStartAcceptedWhilePlayingIsIgnored()
        {
            // The launcher refuses this press (the round is not idle or finished); the room
            // must not show Loading over a round that is actually running.
            Assert.AreEqual(HubRoomMath.PhasePlaying,
                Advance(HubRoomMath.PhasePlaying, StoryRoundController.StateRunning, true, false,
                    false, false));
        }

        [Test]
        public void AnUnknownPhaseOrStateFailsClosedToHub()
        {
            int next;
            Assert.IsFalse(HubRoomMath.TryAdvance(99, StoryRoundController.StateIdle, true, false,
                true, false, out next));
            Assert.AreEqual(HubRoomMath.PhaseHub, next);

            Assert.IsFalse(HubRoomMath.TryAdvance(HubRoomMath.PhaseHub, 99, true, false, true,
                false, out next));
            Assert.AreEqual(HubRoomMath.PhaseHub, next);
        }

        [Test]
        public void TheFullLoopRunsHubLoadingPlayingResultRetryPlayingAndBack()
        {
            // One scripted session, in the order the owner plays it.
            int phase = HubRoomMath.PhaseHub;

            phase = Advance(phase, StoryRoundController.StateIdle, true, false, true, false);
            Assert.AreEqual(HubRoomMath.PhaseLoading, phase);

            phase = Advance(phase, StoryRoundController.StateIdle, false, false, false, false);
            Assert.AreEqual(HubRoomMath.PhaseLoading, phase);

            phase = Advance(phase, StoryRoundController.StateRunning, false, false, false, false);
            Assert.AreEqual(HubRoomMath.PhasePlaying, phase);

            phase = Advance(phase, StoryRoundController.StatePresenting, false, false, false, false);
            Assert.AreEqual(HubRoomMath.PhaseResult, phase);

            phase = Advance(phase, StoryRoundController.StateResult, false, false, true, false);
            Assert.AreEqual(HubRoomMath.PhaseResult, phase);

            // Retry from the result: same start path, the launcher turns Result into
            // RequestRetry().
            phase = Advance(phase, StoryRoundController.StateResult, true, false, true, true);
            Assert.AreEqual(HubRoomMath.PhaseLoading, phase);

            phase = Advance(phase, StoryRoundController.StateRunning, false, false, false, false);
            Assert.AreEqual(HubRoomMath.PhasePlaying, phase);

            phase = Advance(phase, StoryRoundController.StateResult, false, false, false, false);
            Assert.AreEqual(HubRoomMath.PhaseResult, phase);

            // Back to the room instead of another attempt.
            phase = Advance(phase, StoryRoundController.StateResult, false, false, true, true);
            Assert.AreEqual(HubRoomMath.PhaseHub, phase);
        }

        // ------------------------------------------------------------------ level circles

        [Test]
        public void ACircleSelectsItsOwnLevelIndex()
        {
            var zones = new Vector3[2];
            zones[0] = new Vector3(1200f, 0f, 1.2f);
            zones[1] = new Vector3(1200f, 0f, -1.2f);

            int index;
            Assert.IsTrue(HubRoomMath.TryPickZone(new Vector3(1200f, 0.1f, 1.1f), zones, 1.2f,
                out index));
            Assert.AreEqual(0, index);

            Assert.IsTrue(HubRoomMath.TryPickZone(new Vector3(1200f, 0.1f, -1.1f), zones, 1.2f,
                out index));
            Assert.AreEqual(1, index);
        }

        [Test]
        public void OverlappingCirclesStillSelectExactlyOneLevel()
        {
            // The nearer centre wins, so the room can never believe two levels were chosen.
            var zones = new Vector3[2];
            zones[0] = new Vector3(0f, 0f, 0f);
            zones[1] = new Vector3(0.1f, 0f, 0f);

            int index;
            Assert.IsTrue(HubRoomMath.TryPickZone(new Vector3(0.02f, 0f, 0f), zones, 1f, out index));
            Assert.AreEqual(0, index);
        }

        [Test]
        public void StandingOutsideEveryCircleSelectsNothing()
        {
            var zones = new Vector3[1];
            zones[0] = new Vector3(1200f, 0f, 1.2f);

            int index;
            Assert.IsFalse(HubRoomMath.TryPickZone(new Vector3(1200f, 0f, -2.8f), zones, 1.2f,
                out index));
            Assert.AreEqual(-1, index);
        }

        [Test]
        public void AnEmptyOrUnusableCircleListSelectsNothing()
        {
            int index;
            Assert.IsFalse(HubRoomMath.TryPickZone(Vector3.zero, new Vector3[0], 1f, out index));
            Assert.AreEqual(-1, index);

            Assert.IsFalse(HubRoomMath.TryPickZone(Vector3.zero, null, 1f, out index));
            Assert.AreEqual(-1, index);

            var zones = new Vector3[1];
            zones[0] = new Vector3(0f, 0f, 0f);
            Assert.IsFalse(HubRoomMath.TryPickZone(new Vector3(float.NaN, 0f, 0f), zones, 1f,
                out index));
            Assert.AreEqual(-1, index);

            // A non-finite slot is skipped rather than poisoning the comparison.
            zones = new Vector3[2];
            zones[0] = new Vector3(float.NaN, 0f, 0f);
            zones[1] = new Vector3(0f, 0f, 0f);
            Assert.IsTrue(HubRoomMath.TryPickZone(Vector3.zero, zones, 1f, out index));
            Assert.AreEqual(1, index);
        }

        [Test]
        public void TheCircleRadiusIsInclusiveAndMustBeUsable()
        {
            var zones = new Vector3[1];
            zones[0] = Vector3.zero;

            int index;
            Assert.IsTrue(HubRoomMath.TryPickZone(new Vector3(1f, 0f, 0f), zones, 1f, out index));
            Assert.AreEqual(0, index);

            Assert.IsFalse(HubRoomMath.TryPickZone(new Vector3(1.01f, 0f, 0f), zones, 1f, out index));
            Assert.IsFalse(HubRoomMath.TryPickZone(Vector3.zero, zones, 0f, out index));
            Assert.IsFalse(HubRoomMath.TryPickZone(Vector3.zero, zones, float.NaN, out index));
        }

        // ------------------------------------------------------------------ re-arm / dwell

        [Test]
        public void TheCircleOnlyRearmsOutside()
        {
            Assert.IsTrue(HubRoomMath.NextArmed(false, false));
            Assert.IsFalse(HubRoomMath.NextArmed(false, true));
            Assert.IsTrue(HubRoomMath.NextArmed(true, true));

            // The session in order: armed on the first frame outside, disarmed by a start,
            // still disarmed while the player is returned into the circle, armed again only
            // after stepping out.
            bool armed = HubRoomMath.NextArmed(false, false);
            Assert.IsTrue(armed);
            armed = false; // a start was accepted
            armed = HubRoomMath.NextArmed(armed, true);
            Assert.IsFalse(armed);
            armed = HubRoomMath.NextArmed(armed, false);
            Assert.IsTrue(armed);
        }

        [Test]
        public void AutoStartNeedsArmedInsideAndDwell()
        {
            Assert.IsTrue(HubRoomMath.CanAutoStart(HubRoomMath.PhaseHub, true, true, 0.35f, 0.35f));
            Assert.IsTrue(HubRoomMath.CanAutoStart(HubRoomMath.PhaseHub, true, true, 1f, 0f));
            Assert.IsFalse(HubRoomMath.CanAutoStart(HubRoomMath.PhaseHub, false, true, 1f, 0.35f));
            Assert.IsFalse(HubRoomMath.CanAutoStart(HubRoomMath.PhaseHub, true, false, 1f, 0.35f));
            Assert.IsFalse(HubRoomMath.CanAutoStart(HubRoomMath.PhaseHub, true, true, 0.2f, 0.35f));
            Assert.IsFalse(HubRoomMath.CanAutoStart(HubRoomMath.PhaseHub, true, true, float.NaN, 0.35f));
        }

        [Test]
        public void AutoStartIsRefusedWhileLoadingOrPlayingAndAllowedForARetry()
        {
            Assert.IsFalse(HubRoomMath.CanAutoStart(HubRoomMath.PhaseLoading, true, true, 1f, 0f));
            Assert.IsFalse(HubRoomMath.CanAutoStart(HubRoomMath.PhasePlaying, true, true, 1f, 0f));

            // Result -> a walk-in retry is the same start path; the launcher turns it into
            // RequestRetry(), which resets the ball and the props first.
            Assert.IsTrue(HubRoomMath.CanAutoStart(HubRoomMath.PhaseResult, true, true, 0.5f, 0f));
        }

        // ------------------------------------------------------------------ prompt gating

        [Test]
        public void TheRoomAlwaysSaysHowToStartEvenFarFromTheCircle()
        {
            // The owner's original problem was "I cannot find how to start"; a prompt that
            // only appears within 2.7 m of the circle would repeat it. In Hub the approach
            // line is always readable, near or far.
            Assert.AreEqual(HubRoomMath.PromptApproach,
                HubRoomMath.PromptFor(HubRoomMath.PhaseHub, false, false, true, false, true));
            Assert.AreEqual(HubRoomMath.PromptApproach,
                HubRoomMath.PromptFor(HubRoomMath.PhaseHub, true, false, true, false, true));
        }

        [Test]
        public void TheApproachPromptShowsBeforeThePlayerIsInside()
        {
            Assert.AreEqual(HubRoomMath.PromptApproach,
                HubRoomMath.PromptFor(HubRoomMath.PhaseHub, true, false, true, false, true));
        }

        [Test]
        public void AnUnarmedCircleAsksThePlayerToStepOutAndBackIn()
        {
            Assert.AreEqual(HubRoomMath.PromptReArm,
                HubRoomMath.PromptFor(HubRoomMath.PhaseHub, true, true, false, false, true));
        }

        [Test]
        public void AnArmedCircleShowsStandInThenStarting()
        {
            Assert.AreEqual(HubRoomMath.PromptStandIn,
                HubRoomMath.PromptFor(HubRoomMath.PhaseHub, true, true, true, false, true));
            Assert.AreEqual(HubRoomMath.PromptStarting,
                HubRoomMath.PromptFor(HubRoomMath.PhaseHub, true, true, true, true, true));
        }

        [Test]
        public void LoadingAndPlayingDescribeTheRoundRatherThanTheCircle()
        {
            Assert.AreEqual(HubRoomMath.PromptLoading,
                HubRoomMath.PromptFor(HubRoomMath.PhaseLoading, false, false, false, false, false));
            Assert.AreEqual(HubRoomMath.PromptPlaying,
                HubRoomMath.PromptFor(HubRoomMath.PhasePlaying, false, false, false, false, false));
        }

        [Test]
        public void TheResultPhasePromptsForARetryOrForTheReturnButton()
        {
            // In the room, near the circle: the retry is one step away.
            Assert.AreEqual(HubRoomMath.PromptRetryApproach,
                HubRoomMath.PromptFor(HubRoomMath.PhaseResult, true, false, false, false, true));

            // In the room, away from the circle: the card is the board and the circle is the
            // next attempt.
            Assert.AreEqual(HubRoomMath.PromptResult,
                HubRoomMath.PromptFor(HubRoomMath.PhaseResult, false, false, false, false, true));

            // Not back in the room (a failed return): point at the level's return button
            // instead of pretending the room is showing.
            Assert.AreEqual(HubRoomMath.PromptResultAway,
                HubRoomMath.PromptFor(HubRoomMath.PhaseResult, false, false, false, false, false));
        }
    }
}
