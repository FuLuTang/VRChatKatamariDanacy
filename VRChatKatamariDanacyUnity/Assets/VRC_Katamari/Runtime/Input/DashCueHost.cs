using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.BallMotor;
using VRCKatamari.Hub;

namespace VRCKatamari.Input
{
    /// <summary>
    /// 冲刺提示（只影响表现）：蓄力开始、空转点、出射三个时刻放声音并震左手，被拒绝时两下短震。
    /// 每帧读马达阶段，阶段变化才触发；不写任何玩法状态。没有接 clip 时静默，只剩震动。
    /// 震动参数和「触感强度」设置的处理与 AbsorbFeedbackHost 一致（强度 0 不震；IsUserInVR 才震）。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DashCueHost : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private HubSettingsHost settings;
        [Tooltip("可为空：一路声音，按蓄力/空转/出射换 clip 与音高。")]
        [SerializeField] private AudioSource voice;
        [SerializeField] private AudioClip chargeClip;
        [SerializeField] private AudioClip spinClip;
        [SerializeField] private AudioClip launchClip;

        private int phase;
        private int refusePulsesLeft;
        private float refuseNextTime;
        private float lastVolume = -1f;

        private void Update()
        {
            float now = Time.unscaledTime;
            RefreshVolume();
            if (motor != null)
            {
                int next = DashCueMath.PhaseOf(motor.IsDashCharging(), motor.IsDashSpinning(), motor.IsDashing());
                int cue = DashCueMath.CueFor(phase, next);
                phase = next;
                if (cue == DashCueMath.CueStop) StopVoice();
                else if (cue != DashCueMath.CueNone) Play(cue);
            }
            if (refusePulsesLeft > 0 && now >= refuseNextTime)
            {
                Pulse(DashCueMath.RefuseDuration, DashCueMath.RefuseAmplitude);
                refusePulsesLeft--;
                refuseNextTime = now + DashCueMath.RefuseGapSeconds;
            }
        }

        /// <summary>输入层在蓄力被拒绝（喘气、没有寿命、已在冲刺）时调用：两下短震，并停掉蓄力音。</summary>
        public void NotifyRefused()
        {
            StopVoice();
            refusePulsesLeft = 2;
            refuseNextTime = 0f;
        }

        /// <summary>关门、离座、重生取消冲刺时调用：停声音，不再补发提示。</summary>
        public void NotifyCancelled()
        {
            StopVoice();
            refusePulsesLeft = 0;
            phase = DashCueMath.PhaseIdle;
        }

        public void OnDisable() { StopVoice(); }

        private void Play(int cue)
        {
            Pulse(DashCueMath.HapticDuration(cue), DashCueMath.HapticAmplitude(cue, HapticSetting()));
            if (voice == null) return;
            AudioClip clip = cue == DashCueMath.CueChargeStart ? chargeClip
                : cue == DashCueMath.CueSpin ? spinClip : launchClip;
            if (clip == null) return;
            voice.Stop();
            voice.pitch = DashCueMath.Pitch(cue);
            voice.clip = clip;
            voice.Play();
        }

        private void StopVoice()
        {
            if (voice != null) voice.Stop();
        }

        private float HapticSetting()
        {
            return settings == null ? 1f : settings.GetHaptics01();
        }

        private void Pulse(float duration, float amplitude)
        {
            if (!(amplitude > 0f)) return;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || !player.isLocal || !player.IsUserInVR()) return;
            player.PlayHapticEventInHand(VRC_Pickup.PickupHand.Left, duration, amplitude, 0.5f);
        }

        private void RefreshVolume()
        {
            if (voice == null) return;
            float volume = settings == null ? 0.8f : settings.GetEffectsVolume01();
            if (volume == lastVolume) return;
            lastVolume = volume;
            voice.volume = volume;
            if (volume <= 0f) voice.Stop();
        }
    }
}
