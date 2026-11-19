using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Hub;
using VRCKatamari.Riding;
using VRCKatamari.Story;

namespace VRCKatamari.BallLayers
{
    /// <summary>本地骑乘提示：位置和大小相对球，箭头指向推进方向。</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PushDirectionCue : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        // 跟随架保留静止时的推进朝向，不用球速或头朝向替代。
        [SerializeField] private Transform pushYawSource;
        [SerializeField] private BallRideStation rideStation;
        // 独立实验场可不接回合控制和大厅设置。
        [SerializeField] private StoryRoundController roundController;
        [SerializeField] private HubSettingsHost settings;
        // 独立根物体下的视觉子物体，祖先必须为单位缩放；原型长 1 m，尖端朝 +Z。
        [SerializeField] private Transform cueRoot;

        private bool cueVisible;

        private void Start()
        {
            if (cueRoot != null) cueRoot.gameObject.SetActive(false);
        }

        public override void PostLateUpdate()
        {
            if (cueRoot == null) return;
            if (!IsCueWanted())
            {
                ApplyVisibility(false);
                return;
            }

            Vector3 position;
            Quaternion rotation;
            float scale;
            if (!PushCueMath.TryEvaluate(motor.GetCenterWorld(), motor.GetSemanticDiameterWorldM() * 0.5f,
                pushYawSource.eulerAngles.y, out position, out rotation, out scale))
            {
                ApplyVisibility(false);
                return;
            }

            cueRoot.SetPositionAndRotation(position, rotation);
            cueRoot.localScale = Vector3.one * scale;
            ApplyVisibility(true);
        }

        private bool IsCueWanted()
        {
            return motor != null && pushYawSource != null && rideStation != null && rideStation.IsLocalPlayerSeated()
                && (roundController == null || roundController.GetState() == StoryRoundController.StateRunning)
                && (settings == null || settings.IsPushCueEnabled());
        }

        private void ApplyVisibility(bool visible)
        {
            if (cueVisible == visible) return;
            cueVisible = visible;
            cueRoot.gameObject.SetActive(visible);
        }
    }
}
