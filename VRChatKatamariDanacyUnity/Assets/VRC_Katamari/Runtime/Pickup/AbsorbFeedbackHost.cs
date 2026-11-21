using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.Hub;
using VRCKatamari.Riding;

namespace VRCKatamari.Pickup
{
    /// <summary>本地骑乘球的确认反馈；不接触吸附判定或账本。</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class AbsorbFeedbackHost : UdonSharpBehaviour
    {
        [SerializeField] private BallRideStation ride;
        [SerializeField] private HubSettingsHost settings;
        [SerializeField] private LevelSwitchHost levelSwitch;
        // 0 为卷入音源，1、2 各独占一路叫声。
        [SerializeField] private AudioSource[] voices;
        [SerializeField] private AudioClip[] clips;
        // 兼容接线检查的字段名：卷入片段标 0，其余为升序排列的叫声编号。
        [SerializeField] private int[] clipClasses;
        [SerializeField] private int[] rollInStarts;
        [SerializeField] private int[] rollInCounts;

        private int hapticBand = -1;
        private float hapticDeadline;
        private int rollInRandomState = 197;
        private int screamRandomState = 389;
        private int previousRollInClip = -1;
        private int lastAudioLevel = -1;
        private int screamFrame0 = -1;
        private int screamFrame1 = -1;
        // 每个叫声只用它首个片段的下标记录截止时间；同号变体共用截止时间。
        private float[] screamDeadlines;
        private float lastVolume = -1f;
        private bool wasRiding;
        private int settingsRevision = -1;
        private float effectsVolume = 0.8f;
        private float haptics = 1f;

        private int confirmedCount;
        private int rollInPlayedCount;
        private int screamPlayedCount;
        private int screamDuplicateCount;
        private int screamPoolDropCount;
        private int hapticPulseCount;
        private int lastLoggedConfirmed;

        public void RecordConfirmed(int screamId, float relativeSize)
        {
            if (!IsRiding()) return;
            wasRiding = true;
            confirmedCount++;
            RefreshSettings();
            float now = Time.unscaledTime;
            RefreshVoiceVolume();
            PlayAudio(screamId, now);

            int band = AbsorbFeedbackMath.SizeBand(relativeSize);
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player != null && player.isLocal && player.IsUserInVR()
                && haptics > 0f && band > hapticBand)
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
            if (hapticBand < 0) return;
            float amplitude = AbsorbFeedbackMath.HapticAmplitude(hapticBand, haptics);
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
            hapticBand = -1;
            wasRiding = false;
            ClearAudio();
            // 保留振动截止时间；下车或重试不能绕过硬件频率上限。
            if (confirmedCount == lastLoggedConfirmed) return;
            lastLoggedConfirmed = confirmedCount;
            Debug.Log("[absorb-feedback] confirmed=" + confirmedCount
                + " rollInPlayed=" + rollInPlayedCount
                + " screamPlayed=" + screamPlayedCount
                + " screamDuplicates=" + screamDuplicateCount
                + " screamPoolDrops=" + screamPoolDropCount
                + " hapticPulses=" + hapticPulseCount);
        }

        public void OnDisable() { ClearFeedback(); }

        private bool IsRiding() { return ride != null && ride.IsLocalPlayerSeated() && !ride.IsExitPending(); }

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

        private int ChooseRollInClip(int level)
        {
            if (level < 0 || rollInStarts == null || rollInCounts == null
                || level >= rollInStarts.Length || level >= rollInCounts.Length) return -1;
            int start = rollInStarts[level];
            int count = rollInCounts[level];
            if (count <= 0 || start < 0 || start >= clips.Length || count > clips.Length - start) return -1;
            rollInRandomState = AbsorbFeedbackMath.NextRandomState(rollInRandomState);
            return start + AbsorbFeedbackMath.ClipRank(rollInRandomState, count, previousRollInClip - start);
        }

        private void PlayAudio(int screamId, float now)
        {
            if (effectsVolume <= 0f || voices == null || voices.Length != AbsorbFeedbackMath.VoiceCount
                || clips == null || clipClasses == null || clips.Length != clipClasses.Length) return;
            int level = levelSwitch == null ? -1 : levelSwitch.GetActiveLevel();
            if (level < 0) return;
            if (level != lastAudioLevel)
            {
                ClearAudio();
                lastAudioLevel = level;
            }

            int rollIn = ChooseRollInClip(level);
            if (rollIn >= 0 && (voices[0] == null || clips[rollIn] == null)) rollIn = -1;
            int scream = AbsorbFeedbackMath.FirstScreamClip(clipClasses, screamId);
            bool screamReady = false;
            int screamSlot = -1;
            int frame = Time.frameCount;
            if (scream >= 0)
            {
                if (screamDeadlines == null) screamDeadlines = new float[clips.Length];
                screamReady = AbsorbFeedbackMath.IsDue(now, screamDeadlines[scream]);
                if (!screamReady) screamDuplicateCount++;
                else
                {
                    screamSlot = AbsorbFeedbackMath.FreeScreamSlot(
                        voices[1] == null || screamFrame0 == frame || voices[1].isPlaying,
                        voices[2] == null || screamFrame1 == frame || voices[2].isPlaying);
                    if (screamSlot < 0) screamPoolDropCount++;
                }
            }

            int layers = AbsorbFeedbackMath.PlaybackLayers(rollIn, scream, screamReady, screamSlot);
            if ((layers & AbsorbFeedbackMath.RollInLayer) != 0)
            {
                // 不合并确认事件，也不让两路叫声抢占卷入音。
                voices[0].PlayOneShot(clips[rollIn], AbsorbFeedbackMath.RollInGain);
                previousRollInClip = rollIn;
                rollInPlayedCount++;
            }
            if ((layers & AbsorbFeedbackMath.ScreamLayer) != 0)
            {
                int variants = 1;
                while (scream + variants < clips.Length && clipClasses[scream + variants] == screamId)
                    variants++;
                screamRandomState = AbsorbFeedbackMath.NextRandomState(screamRandomState);
                AudioClip clip = clips[scream + AbsorbFeedbackMath.ClipRank(screamRandomState, variants, -1)];
                if (clip == null) return;
                AudioSource voice = voices[screamSlot + 1];
                voice.clip = clip;
                voice.Play();
                if (screamSlot == 0) screamFrame0 = frame;
                else screamFrame1 = frame;
                screamDeadlines[scream] = AbsorbFeedbackMath.NextScreamTime(now);
                screamPlayedCount++;
            }
        }

        private void RefreshVoiceVolume()
        {
            if (effectsVolume == lastVolume) return;
            lastVolume = effectsVolume;
            if (voices == null) return;
            for (int i = 0; i < voices.Length; i++)
            {
                AudioSource voice = voices[i];
                if (voice == null) continue;
                voice.volume = i == 0 ? effectsVolume : AbsorbFeedbackMath.ScreamGain * effectsVolume;
                if (effectsVolume <= 0f) voice.Stop();
            }
        }

        private void ClearAudio()
        {
            lastAudioLevel = -1;
            previousRollInClip = -1;
            screamFrame0 = -1;
            screamFrame1 = -1;
            if (screamDeadlines != null)
                for (int i = 0; i < screamDeadlines.Length; i++) screamDeadlines[i] = 0f;
            if (voices == null) return;
            for (int i = 0; i < voices.Length; i++)
                if (voices[i] != null) voices[i].Stop();
        }
    }
}
