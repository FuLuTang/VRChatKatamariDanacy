namespace VRCKatamari.Story.Hub
{
    /// <summary>Display-only phases for the explicit wall panel navigation.</summary>
    public static class HubRoomMath
    {
        public const int PhaseHub = 0;
        public const int PhaseLoading = 1;
        public const int PhasePlaying = 2;
        public const int PhaseResult = 3;

        public static int PanelPhase(int roundState, bool startPending, bool playerInHub)
        {
            if (startPending) return PhaseLoading;
            if (roundState == StoryRoundController.StateRunning) return PhasePlaying;
            if (!playerInHub && (roundState == StoryRoundController.StatePresenting
                || roundState == StoryRoundController.StateResult)) return PhaseResult;
            return PhaseHub;
        }

        /// <summary>The <see cref="HubPanelTextMath"/> key of the prompt line for a phase.</summary>
        public static int PromptKey(int phase)
        {
            if (phase == PhaseLoading) return HubPanelTextMath.PromptLoading;
            if (phase == PhasePlaying) return HubPanelTextMath.PromptPlaying;
            if (phase == PhaseResult) return HubPanelTextMath.PromptResult;
            return HubPanelTextMath.PromptHub;
        }

        public static string PhaseText(int phase)
        {
            if (phase == PhaseHub) return "hub";
            if (phase == PhaseLoading) return "loading";
            if (phase == PhasePlaying) return "playing";
            if (phase == PhaseResult) return "result";
            return "unknown";
        }
    }
}
