using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRCKatamari.Input;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// The local player's settings: one integer step per <see cref="HubSettingsMath"/> id,
    /// saved in VRChat PlayerData, applied to the consumers that exist and announced to the
    /// ones that listen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Store, not rule: ranges, defaults, stepping and the step-to-value conversion are
    /// <see cref="HubSettingsMath"/>'s. Values are read only after <c>OnPlayerRestored</c>
    /// for the local player (the platform rule: writing earlier can be overwritten by the
    /// restore); before that every setting is its default and a press still works, it is just
    /// saved once the restore arrived. A missing language key takes the client's language
    /// (<c>VRCPlayerApi.GetCurrentLanguage</c>).
    /// </para>
    /// <para>
    /// Consumers. Applied directly: the VR stick scheme (<c>VrStickMoveInput.SetControlMode</c>)
    /// and the panel language (<see cref="listeners"/> redraw). Every other setting is stored
    /// and readable through the getters below and <see cref="GetRevision"/>; a consumer either
    /// polls the revision or is added to <see cref="listeners"/>, which receive the custom event
    /// <c>OnHubSettingsChanged</c>. Which consumer is still missing is listed in
    /// Docs/Design/StoryMode/HUB_ROOM.md, and the panel tags those rows "coming soon".
    /// </para>
    /// <para>Local only; nothing is network-synced by this behaviour (PlayerData syncs itself).</para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubSettingsHost : UdonSharpBehaviour
    {
        [Tooltip("VR stick adapter whose scheme the ControlMode setting switches.")]
        [SerializeField] private VrStickMoveInput vrInput;

        [Tooltip("Behaviours that get SendCustomEvent(\"OnHubSettingsChanged\") after any change or restore.")]
        [SerializeField] private UdonSharpBehaviour[] listeners;

        private int[] values;
        private bool restored;
        private int revision;

        private void Start()
        {
            EnsureValues();
            Announce();
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            EnsureValues();
            for (int id = 0; id < HubSettingsMath.Count; id++)
            {
                int stored;
                if (PlayerData.TryGetInt(player, HubSettingsMath.StorageKey(id), out stored))
                {
                    values[id] = HubSettingsMath.Clamp(id, stored);
                }
                else if (id == HubSettingsMath.Language)
                {
                    values[id] = HubSettingsMath.LanguageFromCode(VRCPlayerApi.GetCurrentLanguage());
                }
            }
            restored = true;
            Debug.Log("[hub-settings] restored language=" + values[HubSettingsMath.Language]
                + " controlMode=" + values[HubSettingsMath.ControlMode]);
            Announce();
        }

        /// <summary>One press on a setting's arrow: +1 or -1 step, saved and applied.</summary>
        public void StepSetting(int id, int delta)
        {
            if (!HubSettingsMath.IsValidId(id)) return;
            EnsureValues();
            int next = HubSettingsMath.Stepped(id, values[id], delta);
            if (next == values[id]) return;
            values[id] = next;
            if (restored) PlayerData.SetInt(HubSettingsMath.StorageKey(id), next);
            Debug.Log("[hub-settings] " + HubSettingsMath.StorageKey(id) + "=" + next
                + (restored ? "" : " (not saved: player data not restored yet)"));
            Announce();
        }

        public int GetSetting(int id)
        {
            if (!HubSettingsMath.IsValidId(id)) return 0;
            EnsureValues();
            return values[id];
        }

        /// <summary>Advances on every change or restore; a polling consumer re-reads when it moves.</summary>
        public int GetRevision() { return revision; }

        public bool IsRestored() { return restored; }

        public int GetLanguage() { return GetSetting(HubSettingsMath.Language); }

        public int GetControlMode() { return GetSetting(HubSettingsMath.ControlMode); }

        public bool IsSwapHands() { return GetSetting(HubSettingsMath.SwapHands) == 1; }

        public float GetDeadzone()
        {
            return HubSettingsMath.DeadzoneValue(GetSetting(HubSettingsMath.Deadzone));
        }

        public float GetTurnSensitivity()
        {
            return HubSettingsMath.TurnSensitivityValue(GetSetting(HubSettingsMath.TurnSensitivity));
        }

        public float GetMusicVolume01()
        {
            return HubSettingsMath.Volume01(HubSettingsMath.MusicVolume,
                GetSetting(HubSettingsMath.MusicVolume));
        }

        public float GetEffectsVolume01()
        {
            return HubSettingsMath.Volume01(HubSettingsMath.EffectsVolume,
                GetSetting(HubSettingsMath.EffectsVolume));
        }

        public float GetVoiceVolume01()
        {
            return HubSettingsMath.Volume01(HubSettingsMath.VoiceVolume,
                GetSetting(HubSettingsMath.VoiceVolume));
        }

        public float GetHaptics01()
        {
            return HubSettingsMath.Haptics01(GetSetting(HubSettingsMath.Haptics));
        }

        public bool IsLowEffects() { return GetSetting(HubSettingsMath.LowEffects) == 1; }

        public bool IsReduceAutoRotate() { return GetSetting(HubSettingsMath.ReduceAutoRotate) == 1; }

        private void EnsureValues()
        {
            if (values != null) return;
            values = new int[HubSettingsMath.Count];
            for (int id = 0; id < HubSettingsMath.Count; id++) values[id] = HubSettingsMath.Default(id);
        }

        private void Announce()
        {
            revision++;
            if (vrInput != null) vrInput.SetControlMode(values[HubSettingsMath.ControlMode]);
            if (listeners == null) return;
            for (int i = 0; i < listeners.Length; i++)
            {
                if (listeners[i] != null) listeners[i].SendCustomEvent("OnHubSettingsChanged");
            }
        }
    }
}
