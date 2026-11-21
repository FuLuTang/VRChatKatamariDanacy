using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.Core;
using VRCKatamari.Hub;
using VRCKatamari.Riding;
using VRCKatamari.Story;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// 本地骑乘 HUD：显示刚吸到的那一件（原作左下角圆框：慢转小模型 + 物件名）。
    /// 放在视线下方偏左，只跟头部 yaw，带死区并平滑追上。
    /// </summary>
    /// <remarks>
    /// 小模型是被吸物件网格的缩小副本：预先摆好的 MeshFilter/MeshRenderer 槽位借用原物件的
    /// sharedMesh/sharedMaterials，不新建网格、材质或相机。设计见
    /// Docs/Design/BallLayers/CatchName/CATCH_NAME_HUD.md。
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CatchNameDisplay : UdonSharpBehaviour
    {
        [SerializeField] private BallRideStation rideStation;
        // 独立实验场可不接回合控制和大厅设置。
        [SerializeField] private StoryRoundController roundController;
        [SerializeField] private HubSettingsHost settings;

        // 独立根物体下的视觉子物体，祖先必须为单位缩放。原型：圆底直径 1，中心在原点，面朝 -Z。
        [SerializeField] private Transform hudRoot;
        // hudRoot 下的转盘；槽位是它的直接子物体，网格为空。
        [SerializeField] private Transform modelPivot;
        [SerializeField] private MeshFilter[] modelFilters;
        [SerializeField] private MeshRenderer[] modelRenderers;
        // ≤5 字与更长名字两套版式（原作 Under5Text / Over5Text）。
        [SerializeField] private TextMeshProUGUI shortNameText;
        [SerializeField] private TextMeshProUGUI longNameText;
        // 可选：吸到瞬间从物件收向圆底的一条线；空则不画。
        [SerializeField] private LineRenderer catchLine;

        [SerializeField, Range(20f, 35f)] private float deadZoneDegrees = CatchNameMath.DefaultDeadZoneDegrees;
        [SerializeField, Range(0.3f, 0.6f)] private float catchUpSeconds = CatchNameMath.DefaultCatchUpSeconds;
        [SerializeField, Range(1.2f, 1.6f)] private float distanceMeters = CatchNameMath.DefaultDistanceMeters;
        [SerializeField, Range(-20f, -10f)] private float elevationDegrees = CatchNameMath.DefaultElevationDegrees;
        [SerializeField, Range(-20f, -10f)] private float yawOffsetDegrees = CatchNameMath.DefaultYawOffsetDegrees;
        [SerializeField, Range(5f, 10f)] private float angularSizeDegrees = CatchNameMath.DefaultAngularSizeDegrees;
        [SerializeField, Range(0f, 40f)] private float modelTiltDegrees = 20f;

        private bool hudVisible;
        private bool followingYaw;
        private float anchorYaw;
        private float lastHeadYaw;

        private bool hasCatch;
        private float catchTime;
        private int catchFrame = -1;
        private int catchPriority;
        private Transform catchSource;
        private string nameZh = "";
        private string nameEn = "";
        private string nameJa = "";
        private string shownName = "";
        private int shownChars = -1;
        private int settingsRevision = -1;
        private int language = HubSettingsMath.LangZh;
        private bool lowEffects;
        private bool loggedSlotOverflow;

        private void Start()
        {
            if (settings == null) language = HubSettingsMath.LanguageFromCode(VRCPlayerApi.GetCurrentLanguage());
            if (hudRoot != null) hudRoot.gameObject.SetActive(false);
            ClearModel();
            if (catchLine != null) catchLine.enabled = false;
        }

        /// <summary>
        /// 由道具宿主在每次确认吸附后调用（只在本地、只在收集中）。prop 是物件根；
        /// priority 为同帧比较用的显示优先级（原作 u8WndDispPrio，没有数据时传 0）。
        /// </summary>
        public void RecordCatch(Transform prop, string zh, string en, string ja,
            int priority)
        {
            if (prop == null || !IsRiding()) return;
            int frame = Time.frameCount;
            if (hasCatch && !CatchNameMath.Replaces(frame == catchFrame, catchPriority, priority)) return;
            hasCatch = true;
            catchFrame = frame;
            catchPriority = priority;
            catchTime = Time.time;
            catchSource = prop;
            nameZh = zh == null ? "" : zh;
            nameEn = en == null ? "" : en;
            nameJa = ja == null ? "" : ja;
            RefreshSettings();
            ApplyName();
            BuildModel(prop);
        }

        public override void PostLateUpdate()
        {
            if (hudRoot == null) return;
            if (!IsRiding() || !IsRoundRunning())
            {
                // 离开骑乘或回合结束：下一回合从空白开始。
                if (hasCatch) ForgetCatch();
                ApplyVisibility(false);
                return;
            }
            RefreshSettings();
            if (!hasCatch || (settings != null && !settings.IsCatchNameEnabled()))
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
            VRCPlayerApi.TrackingData head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            if (!KatamariMath.IsFiniteQuaternion(head.rotation))
            {
                ApplyVisibility(false);
                return;
            }

            lastHeadYaw = CatchNameMath.HeadYaw(head.rotation, lastHeadYaw);
            if (!hudVisible)
            {
                anchorYaw = lastHeadYaw;
                followingYaw = false;
            }
            else
            {
                bool following;
                anchorYaw = CatchNameMath.StepAnchorYaw(anchorYaw, lastHeadYaw, deadZoneDegrees,
                    catchUpSeconds, Time.deltaTime, followingYaw, out following);
                followingYaw = following;
            }

            float yaw = anchorYaw + yawOffsetDegrees;
            float distance = CatchNameMath.ScaledDistance(distanceMeters, player.GetAvatarEyeHeightAsMeters());
            Vector3 position;
            float diameter;
            if (!CatchNameMath.TryEvaluateHudPlacement(head.position, yaw, distance, elevationDegrees,
                angularSizeDegrees, out position, out diameter))
            {
                ApplyVisibility(false);
                return;
            }

            hudRoot.SetPositionAndRotation(position, CatchNameMath.FacingRotation(yaw, elevationDegrees));
            hudRoot.localScale = Vector3.one * diameter;
            ApplyVisibility(true);

            float elapsed = Time.time - catchTime;
            UpdateName(elapsed);
            if (modelPivot != null)
            {
                modelPivot.localRotation = Quaternion.Euler(-modelTiltDegrees, 0f, 0f)
                    * Quaternion.Euler(0f, CatchNameMath.ShownSpinDegrees(elapsed, lowEffects), 0f);
            }
            UpdateLine(elapsed, position);
        }

        private bool IsRiding()
        {
            return rideStation != null && rideStation.IsLocalPlayerSeated();
        }

        private bool IsRoundRunning()
        {
            return roundController == null || roundController.GetState() == StoryRoundController.StateRunning;
        }

        private void RefreshSettings()
        {
            if (settings == null) return;
            int revision = settings.GetRevision();
            if (revision == settingsRevision) return;
            settingsRevision = revision;
            lowEffects = settings.IsLowEffects();
            int next = settings.GetLanguage();
            if (next == language) return;
            language = next;
            if (hasCatch) ApplyName();
        }

        private void ApplyName()
        {
            shownName = CatchNameMath.PickName(language, nameZh, nameEn, nameJa);
            shownChars = -1;
            bool useShort = CatchNameMath.UsesShortLayout(shownName.Length);
            if (shortNameText != null) shortNameText.text = useShort ? shownName : "";
            if (longNameText != null) longNameText.text = useShort ? "" : shownName;
        }

        private void UpdateName(float elapsed)
        {
            int length = shownName.Length;
            int chars = CatchNameMath.IsNameShown(elapsed, length)
                ? CatchNameMath.VisibleCharacters(elapsed, length) : 0;
            if (chars == shownChars) return;
            shownChars = chars;
            if (shortNameText != null) shortNameText.maxVisibleCharacters = chars;
            if (longNameText != null) longNameText.maxVisibleCharacters = chars;
        }

        private void UpdateLine(float elapsed, Vector3 hudCenter)
        {
            if (catchLine == null) return;
            float progress = CatchNameMath.LineProgress(elapsed);
            bool draw = !lowEffects && catchSource != null && progress < 1f;
            if (catchLine.enabled != draw) catchLine.enabled = draw;
            if (!draw) return;
            Vector3 from = catchSource.position;
            catchLine.SetPosition(0, Vector3.Lerp(from, hudCenter, progress));
            catchLine.SetPosition(1, hudCenter);
        }

        // 槽位放在「物件根的朝向、世界米」坐标里，再整体平移到包围盒中心并缩进圆底。
        private void BuildModel(Transform prop)
        {
            ClearModel();
            if (modelPivot == null || modelFilters == null || modelRenderers == null) return;
            int slots = modelFilters.Length < modelRenderers.Length ? modelFilters.Length : modelRenderers.Length;
            MeshFilter[] sources = prop.GetComponentsInChildren<MeshFilter>(true);
            Quaternion inverseRoot = Quaternion.Inverse(prop.rotation);

            bool any = false;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            int used = 0;
            int skipped = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                MeshFilter source = sources[i];
                if (source == null || source.sharedMesh == null) continue;
                MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();
                if (sourceRenderer == null) continue;
                if (used >= slots)
                {
                    skipped++;
                    continue;
                }
                Transform part = source.transform;
                Vector3 offset = inverseRoot * (part.position - prop.position);
                Quaternion rotation = inverseRoot * part.rotation;
                Vector3 scale = part.lossyScale;

                Transform slot = modelFilters[used].transform;
                modelFilters[used].sharedMesh = source.sharedMesh;
                modelRenderers[used].sharedMaterials = sourceRenderer.sharedMaterials;
                slot.localPosition = offset;
                slot.localRotation = rotation;
                slot.localScale = scale;

                Bounds local = source.sharedMesh.bounds;
                Vector3 c = offset + rotation * Vector3.Scale(local.center, scale);
                Vector3 e = Vector3.Scale(local.extents, scale);
                Vector3 r = CatchNameMath.RotatedHalfExtents(rotation, e);
                if (!any)
                {
                    min = c - r;
                    max = c + r;
                    any = true;
                }
                else
                {
                    min = Vector3.Min(min, c - r);
                    max = Vector3.Max(max, c + r);
                }
                used++;
            }
            if (skipped > 0 && !loggedSlotOverflow)
            {
                loggedSlotOverflow = true;
                Debug.LogWarning("[catch-name] " + prop.name + " has " + (used + skipped)
                    + " mesh parts, HUD shows " + used + " (slot pool " + slots + ")");
            }
            if (!any) return;

            Vector3 center = 0.5f * (min + max);
            float fit = CatchNameMath.FitScale(min, max, CatchNameMath.DefaultModelFitDiameter);
            if (fit <= 0f)
            {
                ClearModel();
                return;
            }
            for (int i = 0; i < used; i++)
            {
                Transform slot = modelFilters[i].transform;
                slot.localPosition = (slot.localPosition - center) * fit;
                slot.localScale = slot.localScale * fit;
                modelRenderers[i].enabled = true;
            }
        }

        private void ClearModel()
        {
            if (modelFilters == null) return;
            for (int i = 0; i < modelFilters.Length; i++)
            {
                if (modelFilters[i] != null) modelFilters[i].sharedMesh = null;
                if (modelRenderers != null && i < modelRenderers.Length && modelRenderers[i] != null)
                    modelRenderers[i].enabled = false;
            }
        }

        private void ForgetCatch()
        {
            hasCatch = false;
            catchSource = null;
            catchFrame = -1;
            shownName = "";
            shownChars = -1;
            if (shortNameText != null) shortNameText.text = "";
            if (longNameText != null) longNameText.text = "";
            ClearModel();
            if (catchLine != null) catchLine.enabled = false;
        }

        private void ApplyVisibility(bool visible)
        {
            if (hudVisible == visible) return;
            hudVisible = visible;
            hudRoot.gameObject.SetActive(visible);
            if (!visible && catchLine != null) catchLine.enabled = false;
        }
    }
}
