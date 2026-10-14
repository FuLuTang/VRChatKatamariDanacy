// RuntimeDraft only: ordinary C# data/algorithm draft; no MonoBehaviour/component.
// NOT compiled in Unity or UdonSharp. Unity quaternion semantics need the arrow probe.
using System;
using UnityEngine;

namespace VrcKatamari.RuntimeDraft
{
    public struct RadialContributionDraft
    {
        public string stableItemKey;       // caller includes stable spawn id + generation
        public Vector3[] localEndpoints;   // pre-baked points in the ball-local frame
        public bool isExtremeCandidate;
    }

    public static class RadialProxyRuntimeDraft
    {
        private const int DirectionCount = 32; // provisional, not a measured budget
        private const int CandidateLimit = 4;   // provisional, not a measured budget
        private const float UnitTolerance = 1e-5f;
        private const float MinimumQuaternionNorm = 1e-12f;

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Finite(Quaternion value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        }

        private static bool Unit(Quaternion input, out Quaternion unit)
        {
            unit = Quaternion.identity;
            if (!Finite(input)) return false;
            float scale = Mathf.Max(Mathf.Abs(input.x), Mathf.Max(Mathf.Abs(input.y),
                Mathf.Max(Mathf.Abs(input.z), Mathf.Abs(input.w))));
            if (!Finite(scale) || scale <= 0f) return false;
            float x = input.x / scale;
            float y = input.y / scale;
            float z = input.z / scale;
            float w = input.w / scale;
            float scaledNorm = Mathf.Sqrt(x*x + y*y + z*z + w*w);
            if (!Finite(scaledNorm) || scaledNorm <= 0f || scale <= MinimumQuaternionNorm / scaledNorm)
                return false;
            unit = new Quaternion(x / scaledNorm, y / scaledNorm,
                z / scaledNorm, w / scaledNorm);
            return Finite(unit);
        }

        private static bool UnitDirection(Vector3 input, out Vector3 unit)
        {
            unit = Vector3.zero;
            if (!Finite(input)) return false;
            float length = Mathf.Sqrt(input.x*input.x + input.y*input.y + input.z*input.z);
            if (!Finite(length) || length <= 0f || Mathf.Abs(length - 1f) > UnitTolerance) return false;
            unit = input / length;
            return Finite(unit);
        }

        // Explicit adaptation contract: p = d * length, not length on a guessed axis.
        public static bool TryDirectionLengthPoint(Vector3 direction, float length, out Vector3 point)
        {
            point = Vector3.zero;
            Vector3 unit;
            if (!UnitDirection(direction, out unit) || !Finite(length) || length < 0f) return false;
            point = unit * length;
            return Finite(point);
        }

        // Unity API transform is deliberately visible behind a probe gate. This is
        // not asserted equivalent to the Python host Hamilton transform.
        public static bool TryWorldPointToLocal(Vector3 worldPoint, Vector3 ballPosition,
            Quaternion ballRotation, out Vector3 localPoint)
        {
            localPoint = Vector3.zero;
            Quaternion rotation;
            if (!Finite(worldPoint) || !Finite(ballPosition) || !Unit(ballRotation, out rotation)) return false;
            Vector3 delta = worldPoint - ballPosition;
            if (!Finite(delta)) return false;
            localPoint = Quaternion.Inverse(rotation) * delta;
            return Finite(localPoint);
        }

        // Use this result to fill an on-scene arrow probe and compare with Python.
        public static Vector3 ProbePositiveY90Forward()
        {
            return Quaternion.AngleAxis(90f, Vector3.up) * Vector3.forward;
        }

        private static bool UnitVector(Vector3 value)
        {
            float length = Mathf.Sqrt(value.x*value.x + value.y*value.y + value.z*value.z);
            return Finite(value) && Finite(length) && Mathf.Abs(length - 1f) <= UnitTolerance;
        }

        // Full confirmed-ledger rebuild. The caller can invoke it after add/remove;
        // Python additionally covers incremental affected-direction recomputation.
        // Failure returns false and leaves caller-owned prior arrays untouched.
        public static bool TryRebuild(Vector3[] directions, float coreRadius,
            RadialContributionDraft[] ledger, int maxCandidates,
            out float[] supportLengths, out string[] sortedCandidateIds)
        {
            supportLengths = null;
            sortedCandidateIds = null;
            if (!Finite(coreRadius) || coreRadius < 0f || directions == null ||
                directions.Length != DirectionCount || ledger == null ||
                maxCandidates < 0 || maxCandidates > CandidateLimit) return false;
            for (int d = 0; d < directions.Length; d++)
                if (!UnitVector(directions[d])) return false;

            int candidateCount = 0;
            for (int item = 0; item < ledger.Length; item++)
            {
                if (String.IsNullOrWhiteSpace(ledger[item].stableItemKey) ||
                    ledger[item].localEndpoints == null || ledger[item].localEndpoints.Length == 0) return false;
                for (int earlier = 0; earlier < item; earlier++)
                    if (String.Equals(ledger[item].stableItemKey, ledger[earlier].stableItemKey,
                        StringComparison.Ordinal)) return false;
                if (ledger[item].isExtremeCandidate) candidateCount++;
                for (int point = 0; point < ledger[item].localEndpoints.Length; point++)
                    if (!Finite(ledger[item].localEndpoints[point])) return false;
            }
            if (candidateCount > maxCandidates) return false;

            float[] nextSupports = new float[DirectionCount];
            string[] nextCandidates = new string[candidateCount];
            int candidateIndex = 0;
            for (int item = 0; item < ledger.Length; item++)
            {
                if (ledger[item].isExtremeCandidate)
                {
                    string key = ledger[item].stableItemKey;
                    int insertAt = candidateIndex;
                    while (insertAt > 0 && String.CompareOrdinal(nextCandidates[insertAt - 1], key) > 0)
                    {
                        nextCandidates[insertAt] = nextCandidates[insertAt - 1];
                        insertAt--;
                    }
                    nextCandidates[insertAt] = key;
                    candidateIndex++;
                }
            }

            for (int d = 0; d < directions.Length; d++)
            {
                float support = coreRadius;
                for (int item = 0; item < ledger.Length; item++)
                {
                    for (int point = 0; point < ledger[item].localEndpoints.Length; point++)
                    {
                        float projection = Vector3.Dot(ledger[item].localEndpoints[point], directions[d]);
                        if (!Finite(projection)) return false;
                        if (projection > support) support = projection;
                    }
                }
                if (!Finite(support)) return false;
                nextSupports[d] = support;
            }
            supportLengths = nextSupports;
            sortedCandidateIds = nextCandidates;
            return true;
        }
    }
}
