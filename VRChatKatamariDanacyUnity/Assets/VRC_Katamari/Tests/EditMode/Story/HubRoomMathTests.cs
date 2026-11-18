using NUnit.Framework;
using VRCKatamari.Story;
using VRCKatamari.Story.Hub;

namespace VRCKatamari.Tests
{
    public class HubRoomMathTests
    {
        [TestCase(StoryRoundController.StateIdle, true, HubRoomMath.PhaseHub)]
        [TestCase(StoryRoundController.StateRunning, false, HubRoomMath.PhasePlaying)]
        [TestCase(StoryRoundController.StatePresenting, false, HubRoomMath.PhaseResult)]
        [TestCase(StoryRoundController.StateResult, false, HubRoomMath.PhaseResult)]
        [TestCase(StoryRoundController.StatePresenting, true, HubRoomMath.PhaseHub)]
        [TestCase(StoryRoundController.StateResult, true, HubRoomMath.PhaseHub)]
        public void NavigationDisplayReflectsRoundAndArrival(int state, bool inHub, int phase)
        {
            Assert.AreEqual(phase, HubRoomMath.PanelPhase(state, false, inHub));
        }

        [Test]
        public void AcceptedTransferIsLoadingAndFailedTransferRevealsHubImmediately()
        {
            Assert.AreEqual(HubRoomMath.PhaseLoading,
                HubRoomMath.PanelPhase(StoryRoundController.StateResult, true, true));
            Assert.AreEqual(HubRoomMath.PhaseHub,
                HubRoomMath.PanelPhase(StoryRoundController.StateResult, false, true));
        }

        [TestCase(HubRoomMath.PhaseHub, "hub")]
        [TestCase(HubRoomMath.PhaseLoading, "loading")]
        [TestCase(HubRoomMath.PhasePlaying, "playing")]
        [TestCase(HubRoomMath.PhaseResult, "result")]
        [TestCase(99, "unknown")]
        public void DisplayPhasesHaveStableLogLabels(int phase, string label)
        {
            Assert.AreEqual(label, HubRoomMath.PhaseText(phase));
        }
    }
}
