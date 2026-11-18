using NUnit.Framework;
using VRCKatamari.Hub;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;

namespace VRCKatamari.Tests
{
    public class HubResultFlowTests
    {
        [TestCase(0, false)]
        [TestCase(7.99f, false)]
        [TestCase(8f, true)]
        [TestCase(9f, true)]
        public void CompleteResultWaitsEightSeconds(float elapsed, bool expected)
        {
            Assert.AreEqual(expected, HubLoopMath.ShouldAutoReturn(
                StoryRoundController.StateResult, true, false, false, elapsed, 8f));
        }

        [TestCase(StoryRoundController.StateIdle)]
        [TestCase(StoryRoundController.StateRunning)]
        [TestCase(StoryRoundController.StatePresenting)]
        public void IncompleteResultCannotAutoReturn(int state)
        {
            Assert.IsFalse(HubLoopMath.ShouldAutoReturn(state, true, false, false, 100f, 8f));
        }

        [Test]
        public void ManualReturnDoesNotWaitForResultTimer()
        {
            int reason;
            Assert.IsTrue(HubLoopMath.TryDecideReturn(StoryRoundController.StateResult,
                false, 0.1f, -1f, 1f, false, out reason));
            Assert.IsFalse(HubLoopMath.ShouldAutoReturn(StoryRoundController.StateResult,
                true, false, false, 0.1f, 8f));
        }

        [Test]
        public void RetryWinsAtTheDeadlineAndCancelsInFlightAutomaticReturn()
        {
            Assert.IsFalse(HubLoopMath.ShouldAutoReturn(StoryRoundController.StateResult,
                true, true, false, 8f, 8f));
            Assert.IsTrue(HubLoopMath.ShouldCancelReturnForRetry(StoryRoundController.StateResult,
                true, true));
            Assert.IsFalse(HubLoopMath.ShouldAutoReturn(StoryRoundController.StateResult,
                false, false, false, 20f, 8f));
        }

        [Test]
        public void RetryCannotCancelAManualReturnOrARunningRound()
        {
            Assert.IsFalse(HubLoopMath.ShouldCancelReturnForRetry(StoryRoundController.StateResult,
                true, false));
            Assert.IsFalse(HubLoopMath.ShouldCancelReturnForRetry(StoryRoundController.StateRunning,
                true, true));
        }

        [TestCase(0.99f, false)]
        [TestCase(1f, true)]
        [TestCase(2f, true)]
        public void FailedExitRecoveryChecksOnceItsOneSecondDeadlinePasses(float now, bool expected)
        {
            Assert.AreEqual(expected, HubLoopMath.ShouldRetryReturnExit(true, false, now, 1f));
            Assert.IsFalse(HubLoopMath.ShouldRetryReturnExit(true, true, now, 1f));
            Assert.IsFalse(HubLoopMath.ShouldRetryReturnExit(false, false, now, 1f));
        }

        [TestCase(StoryRoundController.StateRunning, true)]
        [TestCase(StoryRoundController.StateIdle, false)]
        [TestCase(StoryRoundController.StatePresenting, false)]
        [TestCase(StoryRoundController.StateResult, false)]
        public void RespawnAbandonsOnlyAnActiveRound(int state, bool expected)
        {
            Assert.AreEqual(expected, HubLoopMath.ShouldAbortForRespawn(state));
        }

        [Test]
        public void RespawnAbandonmentPreventsHoldAndAllowsReturnWithoutAbortPermission()
        {
            // AbortRound accepts a Running round independently of the level's allowAbort
            // UI permission. Its resulting Result state cannot activate hold-in-level.
            Assert.IsTrue(HubLoopMath.ShouldAbortForRespawn(StoryRoundController.StateRunning));
            Assert.IsFalse(HubLoopMath.ShouldHoldInLevel(StoryRoundController.StateResult, false, 0, 3));
            int reason;
            Assert.IsTrue(HubLoopMath.TryDecideReturn(StoryRoundController.StateResult,
                false, 10f, -1f, 1f, false, out reason));
        }

        [Test]
        public void RecoveryDeadlineRejectsNonFiniteValues()
        {
            Assert.IsFalse(HubLoopMath.ShouldRetryReturnExit(true, false, float.NaN, 1f));
            Assert.IsFalse(HubLoopMath.ShouldRetryReturnExit(true, false, 1f, float.NaN));
        }

        [Test]
        public void PendingTransfersCannotBeOvertakenByTimer()
        {
            Assert.IsFalse(HubLoopMath.ShouldAutoReturn(StoryRoundController.StateResult,
                true, false, true, 100f, 8f));
        }

        [TestCase(float.NaN, 8f)]
        [TestCase(float.PositiveInfinity, 8f)]
        [TestCase(-1f, 8f)]
        [TestCase(8f, float.NaN)]
        [TestCase(8f, -1f)]
        public void InvalidTimingFailsClosed(float elapsed, float delay)
        {
            Assert.IsFalse(HubLoopMath.ShouldAutoReturn(StoryRoundController.StateResult,
                true, false, false, elapsed, delay));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void CompletedReturnImmediatelyAllowsEveryPanelChoice(int index)
        {
            // FinishReturn clears transfer flags and lastTransferSeconds to -1, while
            // retaining Result so the existing reset-and-begin path remains available.
            int reason;
            Assert.IsTrue(HubLoopMath.TryDecideStart(StoryRoundController.StateResult,
                index, 4, false, 20f, -1f, 1f, out reason));
            Assert.AreEqual(HubLoopMath.ReasonNone, reason);
            Assert.AreEqual(HubRoomMath.PhaseHub, HubRoomMath.PanelPhase(
                StoryRoundController.StateResult, false, true));
        }

        [Test]
        public void RoomDisplayTracksPanelTransfersWithoutAnyDwellOrZoneInput()
        {
            Assert.AreEqual(HubRoomMath.PhaseHub, HubRoomMath.PanelPhase(
                StoryRoundController.StateIdle, false, true));
            Assert.AreEqual(HubRoomMath.PhaseLoading, HubRoomMath.PanelPhase(
                StoryRoundController.StateResult, true, true));
            Assert.AreEqual(HubRoomMath.PhasePlaying, HubRoomMath.PanelPhase(
                StoryRoundController.StateRunning, false, false));
            Assert.AreEqual(HubRoomMath.PhaseResult, HubRoomMath.PanelPhase(
                StoryRoundController.StateResult, false, false));
        }
    }
}
