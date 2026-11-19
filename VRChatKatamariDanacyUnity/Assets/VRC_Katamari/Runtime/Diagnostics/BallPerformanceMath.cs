namespace VRCKatamari.Diagnostics
{
    /// <summary>取时与固定窗口统计；调用方持有并复用数组。各槽的单位由采样点决定。</summary>
    public static class BallPerformanceMath
    {
        public const int MotorFrame = 0;
        public const int Input = 1;
        public const int Motion = 2;
        public const int Contact = 3;
        public const int Absorb = 4;
        public const int StepTail = 5;
        public const int PoseWrites = 6;
        public const int AttachmentShape = 7;
        public const int ArenaNearObb = 8;
        public const int TerrainGrid = 9;
        public const int TerrainSupport = 10;
        public const int PickupFrame = 11;
        public const int AttachmentEvents = 12;
        public const int PickupObstacles = 13;
        public const int PickupScan = 14;
        public const int PickupSelect = 15;
        public const int PickupSubmit = 16;
        public const int MotionFrame = 17;
        public const int BallDiameter = 18;
        public const int StepsPerFrame = 19;
        public const int PendingCandidates = 20;
        public const int ScanCandidates = 21;
        public const int SelectedCandidates = 22;
        public const int StaticObb = 23;
        public const int TerrainSupportVisited = 24;
        public const int TerrainSupportFullTests = 25;
        public const int TerrainSolveCandidates = 26;
        public const int TerrainSolveFullScan = 27;
        public const int Attached = 28;
        public const int MeasuredFrame = 29;
        public const int SlotCount = 30;

        public static long Timestamp()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        // 时间戳先于跨行为提交；相邻阶段共用边界的读数。
        public static long Split(BallPerformanceDiagnostics probe, int slot, long begin)
        {
            long end = Timestamp();
            probe._SampleElapsed(slot, begin, end);
            return end;
        }

        public static float Milliseconds(long begin, long end, double millisecondsPerTick)
        {
            long delta = end - begin;
            return delta > 0L ? (float)(delta * millisecondsPerTick) : 0f;
        }

        public static void Add(int slot, float value, double[] sums, float[] maxima,
            float[] latest, int[] counts)
        {
            if (counts[slot] == 0 || value > maxima[slot]) maxima[slot] = value;
            sums[slot] += value;
            latest[slot] = value;
            counts[slot]++;
        }

        public static float Average(double sum, int count)
        {
            return count > 0 ? (float)(sum / count) : 0f;
        }

        public static void ClearWindow(int[] counts, double[] sums)
        {
            // 第一笔样本覆盖 max/latest；空槽由 count 判定，不输出上一窗的值。
            for (int i = 0; i < counts.Length; i++)
            {
                counts[i] = 0;
                sums[i] = 0.0;
            }
        }

        public static float Interval(float seconds)
        {
            return seconds >= 1f && seconds <= 60f ? seconds : 5f;
        }
    }
}
