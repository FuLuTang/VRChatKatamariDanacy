using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.Hub;
using VRCKatamari.Riding;

namespace VRCKatamari.Pickup
{
    /// <summary>One local presentation sink for the pickup fields bound to the ridden ball.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class AbsorbFeedbackHost : UdonSharpBehaviour
    {
        [SerializeField] private BallRideStation ride;
        [SerializeField] private HubSettingsHost settings;
        [SerializeField] private AudioSource[] voices;
        [SerializeField] private int[] clipClasses;
        [SerializeField] private AudioClip[] clips;

        private int clusterCount;
        private int clusterClass;
        private int clusterBand;
        private float audioDeadline;
        private int hapticBand = -1;
        private float hapticDeadline;
        private int randomState = 197;
        private AudioClip previousClip;
        private float lastVolume = -1f;
        // Allocated on first use: an array field initializer is not part of the U# subset.
        private float[] voiceGain;
        private bool wasRiding;
        // Settings are read through GetRevision so a frame that changed nothing costs one call.
        private int settingsRevision = -1;
        private float effectsVolume = 0.8f;
        private float haptics = 1f;

        private int confirmedCount;
        private int audioClusterCount;
        private int audioPlayedCount;
        private int audioPoolDropCount;
        private int hapticPulseCount;
        private int lastLoggedConfirmed = -1;

        // Session counters for ClientSim / live probes; clearing a round does not erase evidence.
        public int GetConfirmedCount() { return confirmedCount; }
        public int GetAudioClusterCount() { return audioClusterCount; }
        public int GetAudioPlayedCount() { return audioPlayedCount; }
        public int GetAudioPoolDropCount() { return audioPoolDropCount; }
        public int GetHapticPulseCount() { return hapticPulseCount; }

        public void RecordConfirmed(int soundClass, float relativeSize)
        {
            if (!IsRiding()) return;
            wasRiding = true;
            confirmedCount++;
            RefreshSettings();
            float now = Time.unscaledTime;
            FlushAudio(now);
            int band = AbsorbFeedbackMath.SizeBand(relativeSize);
            int usableClass = soundClass > 0 && HasClip(soundClass) ? soundClass : 0;
            if (AbsorbFeedbackMath.PreferAudioClass(clusterCount, clusterClass, clusterBand, usableClass, band))
            {
                clusterClass = usableClass;
                clusterBand = band;
            }
            if (clusterCount == 0) audioDeadline = now + AbsorbFeedbackMath.AudioWindowSeconds;
            clusterCount++;

            VRCPlayerApi player = Networking.LocalPlayer;
            if (player != null && player.isLocal && player.IsUserInVR()
                && Haptics() > 0f && band > hapticBand)
                hapticBand = band;
        }

        public void LateUpdate()
        {
            if (!IsRiding())
            {
                if (wasRiding) ClearFeedback();
                return;
            }
            RefreshSettings();
            float now = Time.unscaledTime;
            RefreshVoiceVolume();
            FlushAudio(now);
            if (hapticBand < 0) return;
            float amplitude = AbsorbFeedbackMath.HapticAmplitude(hapticBand, Haptics());
            if (amplitude <= 0f) { hapticBand = -1; return; }
            if (!AbsorbFeedbackMath.IsDue(now, hapticDeadline)) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player != null && player.isLocal && player.IsUserInVR())
            {
                float duration = AbsorbFeedbackMath.HapticDuration(hapticBand);
                player.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, duration, amplitude, 0.5f);
                player.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, duration, amplitude, 0.5f);
                hapticPulseCount++;
                hapticDeadline = now + AbsorbFeedbackMath.HapticIntervalSeconds;
            }
            hapticBand = -1;
        }

        public void ClearFeedback()
        {
            clusterCount = 0;
            hapticBand = -1;
            wasRiding = false;
            // Keep the haptic deadline: leaving or retrying must not bypass the hardware rate cap.
            StopVoices();
            LogCounters();
        }

        public void OnDisable() { ClearFeedback(); }

        // One line when a ride ends and anything happened, so an operator reading the
        // Console can compare the counters with the absorbs they just heard. Cumulative
        // for the session: clearing a round does not erase evidence, and an unchanged
        // count is not logged twice.
        private void LogCounters()
        {
            if (confirmedCount == lastLoggedConfirmed) return;
            lastLoggedConfirmed = confirmedCount;
            Debug.Log("[absorb-feedback] confirmed=" + confirmedCount
                + " audioClusters=" + audioClusterCount
                + " audioPlayed=" + audioPlayedCount
                + " poolDrops=" + audioPoolDropCount
                + " hapticPulses=" + hapticPulseCount);
        }

        private bool IsRiding() { return ride != null && ride.IsLocalPlayerSeated() && !ride.IsExitPending(); }
        private float Haptics() { return haptics; }
        private float Volume() { return effectsVolume; }
        private int ClipCount() { return clips != null && clipClasses != null && clips.Length == clipClasses.Length ? clips.Length : 0; }

        private void RefreshSettings()
        {
            if (settings == null)
            {
                effectsVolume = 0.8f;
                haptics = 1f;
                settingsRevision = -1;
                return;
            }
            int revision = settings.GetRevision();
            if (revision == settingsRevision) return;
            settingsRevision = revision;
            effectsVolume = settings.GetEffectsVolume01();
            haptics = settings.GetHaptics01();
        }

        private float[] Gains()
        {
            if (voiceGain == null) voiceGain = new float[AbsorbFeedbackMath.VoiceCount];
            return voiceGain;
        }

        private bool HasClip(int soundClass)
        {
            int n = ClipCount();
            for (int i = 0; i < n; i++)
                if (clipClasses[i] == soundClass && clips[i] != null) return true;
            return false;
        }

        private AudioClip ChooseClip(int soundClass)
        {
            int count = 0;
            int previousRank = -1;
            int n = ClipCount();
            for (int i = 0; i < n; i++)
            {
                if (clipClasses[i] != soundClass || clips[i] == null) continue;
                if (clips[i] == previousClip) previousRank = count;
                count++;
            }
            randomState = AbsorbFeedbackMath.NextRandomState(randomState);
            int rank = AbsorbFeedbackMath.ClipRank(randomState, count, previousRank);
            for (int i = 0; rank >= 0 && i < n; i++)
            {
                if (clipClasses[i] != soundClass || clips[i] == null) continue;
                if (rank == 0) return clips[i];
                rank--;
            }
            return null;
        }

        private void FlushAudio(float now)
        {
            if (clusterCount == 0 || !AbsorbFeedbackMath.IsDue(now, audioDeadline)) return;
            int count = clusterCount;
            clusterCount = 0;
            audioClusterCount++;
            float volume = Volume();
            if (volume <= 0f || voices == null) return;
            AudioClip clip = ChooseClip(clusterClass);
            if (clip == null) return;
            float[] gains = Gains();
            for (int i = 0; i < voices.Length && i < gains.Length; i++)
            {
                AudioSource voice = voices[i];
                if (voice == null || voice.isPlaying) continue;
                gains[i] = AbsorbFeedbackMath.AudioGain(count);
                voice.volume = gains[i] * volume;
                voice.clip = clip;
                voice.Play();
                previousClip = clip;
                audioPlayedCount++;
                return;
            }
            audioPoolDropCount++;
        }

        private void RefreshVoiceVolume()
        {
            float volume = Volume();
            if (volume == lastVolume) return;
            lastVolume = volume;
            if (volume <= 0f) { StopVoices(); return; }
            if (voices == null) return;
            float[] gains = Gains();
            for (int i = 0; i < voices.Length && i < gains.Length; i++)
                if (voices[i] != null && voices[i].isPlaying) voices[i].volume = gains[i] * volume;
        }

        private void StopVoices()
        {
            if (voices == null) return;
            for (int i = 0; i < voices.Length; i++)
                if (voices[i] != null) voices[i].Stop();
        }
    }
}
