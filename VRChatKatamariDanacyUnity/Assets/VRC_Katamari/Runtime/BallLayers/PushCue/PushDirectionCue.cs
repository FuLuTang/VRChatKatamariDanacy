using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.Core;
using VRCKatamari.Hub;
using VRCKatamari.Riding;
using VRCKatamari.Story;

namespace VRCKatamari.BallLayers
{
    /// <summary>本地骑乘 HUD：用头部定位，但仍用操作参考架决定箭头朝向。</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PushDirectionCue : UdonSharpBehaviour
    {
        // 跟随架保留静止时的推进朝向，不用球速或头朝向替代。
        [SerializeField] private Transform pushYawSource;
        [SerializeField] private BallRideStation rideStation;
        // 独立实验场可不接回合控制和大厅设置。
        [SerializeField] private StoryRoundController roundController;
        [SerializeField] private HubSettingsHost settings;
        // 独立根物体下的视觉子物体，祖先必须为单位缩放；原型长 1 m，尖端朝 +Z。
        [SerializeField] private Transform cueRoot;

        [SerializeField, Range(25f, 30f)] private float deadZoneDegrees = PushCueMath.DefaultDeadZoneDegrees;
        [SerializeField, Range(0.3f, 0.5f)] private float catchUpSeconds = PushCueMath.DefaultCatchUpSeconds;
        [SerializeField, Range(1.2f, 2f)] private float distanceMeters = PushCueMath.DefaultDistanceMeters;
        [SerializeField, Range(10f, 15f)] private float elevationDegrees = PushCueMath.DefaultElevationDegrees;
        [SerializeField, Range(3f, 5f)] private float angularSizeDegrees = PushCueMath.DefaultAngularSizeDegrees;

        private bool cueVisible;
        private bool followingYaw;
        private float anchorYaw;
        private float lastHeadYaw;

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

            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player))
            {
                ApplyVisibility(false);
                return;
            }

            // VR 与桌面都读当前眼点，不能拿座位高度或固定 avatar 眼高代替。
            VRCPlayerApi.TrackingData head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            if (!KatamariMath.IsFiniteQuaternion(head.rotation))
            {
                ApplyVisibility(false);
                return;
            }

            float pushYaw = pushYawSource.eulerAngles.y;
            lastHeadYaw = PushCueMath.HeadYaw(head.rotation, cueVisible ? lastHeadYaw : pushYaw);
            if (!cueVisible)
            {
                anchorYaw = lastHeadYaw;
                followingYaw = false;
            }
            else
            {
                bool following;
                anchorYaw = PushCueMath.StepAnchorYaw(anchorYaw, lastHeadYaw, deadZoneDegrees,
                    catchUpSeconds, Time.deltaTime, followingYaw, out following);
                followingYaw = following;
            }

            Vector3 position;
            Quaternion rotation;
            float scale;
            if (!PushCueMath.TryEvaluate(head.position, anchorYaw, pushYaw, distanceMeters,
                elevationDegrees, angularSizeDegrees, out position, out rotation, out scale))
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
            return pushYawSource != null && rideStation != null && rideStation.IsLocalPlayerSeated()
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
