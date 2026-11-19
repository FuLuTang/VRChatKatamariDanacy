using TMPro;
using UdonSharp;
using UnityEngine;

namespace VRCKatamari.Diagnostics
{
    /// <summary>本地球性能取样。窗口在 LateUpdate 结算，避免把同一帧的三路 Update 分开。</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallPerformanceDiagnostics : UdonSharpBehaviour
    {
        [SerializeField] private float intervalSeconds = 5f;
        [SerializeField] private TextMeshProUGUI panelText;
        [Tooltip("只含读数，不含开关或采集器；不接面板也可只用日志。")]
        [SerializeField] private GameObject panelRoot;

        // 供三个宿主在取表/读时钟前检查。开关必须走 ToggleSampling。
        [System.NonSerialized] public bool sampling;
        private long windowStart;
        private double millisecondsPerTick;
        private float frameMs;
        private int windowNumber;
        private double[] sums;
        private float[] maxima;
        private float[] latest;
        private int[] counts;

        public void Start()
        {
            if (!sampling && panelRoot != null) panelRoot.SetActive(false);
        }

        public override void Interact()
        {
            ToggleSampling();
        }

        public void ToggleSampling()
        {
            if (sampling)
            {
                StopSampling();
                return;
            }
            if (counts == null)
            {
                sums = new double[BallPerformanceMath.SlotCount];
                maxima = new float[BallPerformanceMath.SlotCount];
                latest = new float[BallPerformanceMath.SlotCount];
                counts = new int[BallPerformanceMath.SlotCount];
            }
            BallPerformanceMath.ClearWindow(counts, sums);
            frameMs = 0f;
            millisecondsPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            windowStart = BallPerformanceMath.Timestamp();
            sampling = true;
            if (panelText != null) panelText.text = "性能诊断：正在收集首个窗口…";
            if (panelRoot != null) panelRoot.SetActive(true);
        }

        private void OnDisable()
        {
            if (sampling) StopSampling();
        }

        private void StopSampling()
        {
            sampling = false;
            if (panelRoot != null) panelRoot.SetActive(false);
            else if (panelText != null) panelText.text = "";
        }

        /// <summary>毫秒样本或数值样本；必须由调用方先检查 sampling。</summary>
        public void _Sample(int slot, float value)
        {
            BallPerformanceMath.Add(slot, value, sums, maxima, latest, counts);
            if (slot == BallPerformanceMath.MotorFrame || slot == BallPerformanceMath.PickupFrame
                || slot == BallPerformanceMath.MotionFrame) frameMs += value;
        }

        public void _SampleElapsed(int slot, long begin, long end)
        {
            _Sample(slot, BallPerformanceMath.Milliseconds(begin, end, millisecondsPerTick));
        }

        public void LateUpdate()
        {
            if (!sampling) return;

            _Sample(BallPerformanceMath.MeasuredFrame, frameMs);
            frameMs = 0f;
            long now = BallPerformanceMath.Timestamp();
            float seconds = BallPerformanceMath.Milliseconds(windowStart, now, millisecondsPerTick) * 0.001f;
            if (seconds < BallPerformanceMath.Interval(intervalSeconds)) return;

            windowNumber++;
            Report(seconds);
            BallPerformanceMath.ClearWindow(counts, sums);
            windowStart = now;
        }

        private string Timing(string label, int slot)
        {
            if (counts[slot] == 0) return label + "=—";
            // 每次均/峰；括号内是样本数，末尾是该项全窗摊到每帧的均值。
            return label + "=" + BallPerformanceMath.Average(sums[slot], counts[slot]).ToString("F3")
                + "/" + maxima[slot].ToString("F3") + "(" + counts[slot] + ")@"
                + BallPerformanceMath.Average(sums[slot], counts[BallPerformanceMath.MeasuredFrame]).ToString("F3");
        }

        private string Count(string label, int slot)
        {
            if (counts[slot] == 0) return label + "=—";
            return label + "=" + latest[slot] + "/"
                + BallPerformanceMath.Average(sums[slot], counts[slot]).ToString("F2") + "/" + maxima[slot];
        }

        private void Report(float elapsed)
        {
            string window = "窗口=" + windowNumber + " 窗长=" + elapsed.ToString("F2") + "秒";
            string totals = Timing("三路合计", BallPerformanceMath.MeasuredFrame) + " "
                + Timing("球主控", BallPerformanceMath.MotorFrame) + " "
                + Timing("拾取帧", BallPerformanceMath.PickupFrame) + " "
                + Timing("物件运动", BallPerformanceMath.MotionFrame);
            string motor = Timing("输入", BallPerformanceMath.Input) + " "
                + Timing("球运动", BallPerformanceMath.Motion) + " "
                + Timing("接触", BallPerformanceMath.Contact) + " "
                + Timing("吸附", BallPerformanceMath.Absorb) + " "
                + Timing("步末", BallPerformanceMath.StepTail) + " "
                + Timing("球姿态写入", BallPerformanceMath.PoseWrites);
            string contact = Timing("附着形状", BallPerformanceMath.AttachmentShape) + " "
                + Timing("场地近窗", BallPerformanceMath.ArenaNearObb) + " "
                + Timing("地形格", BallPerformanceMath.TerrainGrid) + " "
                + Timing("地形支撑", BallPerformanceMath.TerrainSupport);
            string pickup = Timing("附着事件", BallPerformanceMath.AttachmentEvents) + " "
                + Timing("道具障碍", BallPerformanceMath.PickupObstacles) + " "
                + Timing("近邻扫描", BallPerformanceMath.PickupScan) + " "
                + Timing("择候选", BallPerformanceMath.PickupSelect) + " "
                + Timing("提交候选", BallPerformanceMath.PickupSubmit);
            string ballCounts = Count("球径米", BallPerformanceMath.BallDiameter) + " "
                + Count("每帧步数", BallPerformanceMath.StepsPerFrame) + " "
                + Count("静态OBB", BallPerformanceMath.StaticObb) + " "
                + Count("已附着", BallPerformanceMath.Attached);
            string candidateCounts = Count("步内待审", BallPerformanceMath.PendingCandidates) + " "
                + Count("近邻候选", BallPerformanceMath.ScanCandidates) + " "
                + Count("选中候选", BallPerformanceMath.SelectedCandidates);
            string terrainCounts = Count("支撑遍历三角", BallPerformanceMath.TerrainSupportVisited) + " "
                + Count("支撑精测三角", BallPerformanceMath.TerrainSupportFullTests) + " "
                + Count("求解输入候选", BallPerformanceMath.TerrainSolveCandidates) + " "
                + Count("求解退回全扫", BallPerformanceMath.TerrainSolveFullScan);
            Debug.Log("[BallPerf] " + window + " | ms(均/峰(n)@帧均) " + totals + " | " + motor
                + " | " + contact + " | " + pickup + " | 数值(末/均/峰) " + ballCounts
                + " | " + candidateCounts + " | " + terrainCounts);
            if (panelText != null)
            {
                panelText.text = "性能诊断 · " + window + "\n毫秒：均/峰(样本数)@帧均\n" + totals
                    + "\n" + motor + "\n" + contact + "\n" + pickup + "\n数值：末/均/峰\n"
                    + ballCounts + "\n" + candidateCounts + "\n" + terrainCounts;
            }
        }
    }
}
