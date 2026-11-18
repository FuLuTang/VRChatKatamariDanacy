using UdonSharp;
using UnityEngine;
using TMPro;
using VRC.SDKBase;
using VRCKatamari.Core;
using VRCKatamari.Hub;
using VRCKatamari.Story;

namespace VRCKatamari.Story.Hub
{
    /// <summary>Read-only room navigation display. Starting is exclusively a level card (uGUI Button) on the levels board.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubRoomHost : UdonSharpBehaviour
    {
        [SerializeField] private HubLevelLauncher launcher;
        [SerializeField] private StoryRoundController controller;
        [SerializeField] private Transform hubCenter;
        [SerializeField] private float hubRadiusMeters = 12f;
        [Tooltip("Level card Button transforms in launcher level order; never proximity triggers.")]
        [SerializeField] private Transform[] levelButtons;
        [SerializeField] private TextMeshProUGUI promptText;
        [Tooltip("Optional: the prompt follows this player's language setting (Chinese when unwired).")]
        [SerializeField] private HubSettingsHost settings;
        private int phase = -1;
        private int shownLanguage = -1;

        public void Update()
        {
            if (launcher == null || controller == null) return;
            int state = controller.GetState();
            VRCPlayerApi player = Networking.LocalPlayer;
            bool inHub = player != null && hubCenter != null
                && HubLoopMath.IsWithin(player.GetPosition(), hubCenter.position, hubRadiusMeters);
            int next = HubRoomMath.PanelPhase(state, launcher.IsStartPending(), inHub);
            int lang = settings != null ? settings.GetLanguage() : HubSettingsMath.LangZh;
            if (next == phase && lang == shownLanguage) return;
            if (next != phase) Debug.Log("[hub-room] phase=" + HubRoomMath.PhaseText(next));
            phase = next;
            shownLanguage = lang;
            if (promptText == null) return;
            promptText.text = HubPanelTextMath.Text(HubRoomMath.PromptKey(phase), lang);
        }

        public int GetPhase() { return phase; }
    }
}
