// RuntimeDraft only: ordinary C# candidate, not a MonoBehaviour/component.
// NOT compiled in Unity or UdonSharp. Expanded AABB is conservative at corners.
using UnityEngine;

namespace VrcKatamari.RuntimeDraft
{
    public struct SweepObstacleDraft
    {
        public bool isBox;
        public Vector3 normal;
        public float planeOffset;
        public Vector3 minimum;
        public Vector3 maximum;
    }

    public struct SweepContactDraft
    {
        public float time;
        public Vector3 center;
        public Vector3 approximatePoint;
        public Vector3 normal;
        public int obstacleIndex;
        public float penetrationDepth;
    }

    public static class SweepRuntimeDraft
    {
        private const float Epsilon = 1e-6f;

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static float Component(Vector3 value, int axis)
        {
            if (axis == 0) return value.x;
            if (axis == 1) return value.y;
            return value.z;
        }

        private static Vector3 AxisNormal(int axis, float sign)
        {
            if (axis == 0) return new Vector3(sign, 0f, 0f);
            if (axis == 1) return new Vector3(0f, sign, 0f);
            return new Vector3(0f, 0f, sign);
        }

        private static bool Length(Vector3 value, out float length)
        {
            length = 0f;
            if (!Finite(value)) return false;
            float scale = Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z)));
            if (scale == 0f) return true;
            float x = value.x / scale;
            float y = value.y / scale;
            float z = value.z / scale;
            length = scale * Mathf.Sqrt(x*x + y*y + z*z);
            return Finite(length);
        }

        private static bool UnitNormal(Vector3 value, out Vector3 normal, out float length)
        {
            normal = Vector3.zero;
            if (!Length(value, out length) || length <= 0f) return false;
            normal = value / length;
            return Finite(normal);
        }

        private static bool ValidateObstacle(SweepObstacleDraft obstacle, float radius)
        {
            if (obstacle.isBox)
            {
                if (!Finite(obstacle.minimum) || !Finite(obstacle.maximum)) return false;
                if (obstacle.minimum.x > obstacle.maximum.x ||
                    obstacle.minimum.y > obstacle.maximum.y ||
                    obstacle.minimum.z > obstacle.maximum.z) return false;
                Vector3 lo = obstacle.minimum - Vector3.one * radius;
                Vector3 hi = obstacle.maximum + Vector3.one * radius;
                return Finite(lo) && Finite(hi);
            }
            Vector3 unit;
            float normalLength;
            if (!UnitNormal(obstacle.normal, out unit, out normalLength) || !Finite(obstacle.planeOffset)) return false;
            return Finite(obstacle.planeOffset / normalLength);
        }

        private static bool TryHit(Vector3 start, Vector3 delta, float radius,
            SweepObstacleDraft obstacle, out float time, out Vector3 normal,
            out float penetration)
        {
            time = 0f;
            normal = Vector3.zero;
            penetration = 0f;
            if (!obstacle.isBox)
            {
                float normalLength;
                if (!UnitNormal(obstacle.normal, out normal, out normalLength)) return false;
                float offset = obstacle.planeOffset / normalLength;
                float distance = Vector3.Dot(start, normal) - offset - radius;
                float normalMotion = Vector3.Dot(delta, normal);
                if (!Finite(offset) || !Finite(distance) || !Finite(normalMotion)) return false;
                if (distance < -Epsilon)
                {
                    penetration = -distance;
                    time = 0f;
                    return true;
                }
                if (normalMotion >= -Epsilon) return false;
                float candidate = -distance / normalMotion;
                if (!Finite(candidate) || candidate < -Epsilon || candidate > 1f + Epsilon) return false;
                time = Mathf.Clamp01(candidate);
                return true;
            }

            Vector3 lo = obstacle.minimum - Vector3.one * radius;
            Vector3 hi = obstacle.maximum + Vector3.one * radius;
            bool inside = start.x >= lo.x - Epsilon && start.x <= hi.x + Epsilon &&
                start.y >= lo.y - Epsilon && start.y <= hi.y + Epsilon &&
                start.z >= lo.z - Epsilon && start.z <= hi.z + Epsilon;
            if (inside)
            {
                float bestDepth = float.PositiveInfinity;
                int bestAxis = 0;
                float bestSign = -1f;
                for (int axis = 0; axis < 3; axis++)
                {
                    float value = Component(start, axis);
                    float lowDepth = value - Component(lo, axis);
                    float highDepth = Component(hi, axis) - value;
                    if (lowDepth < bestDepth)
                    {
                        bestDepth = lowDepth;
                        bestAxis = axis;
                        bestSign = -1f;
                    }
                    if (highDepth < bestDepth)
                    {
                        bestDepth = highDepth;
                        bestAxis = axis;
                        bestSign = 1f;
                    }
                }
                normal = AxisNormal(bestAxis, bestSign);
                if (bestDepth > Epsilon)
                {
                    penetration = bestDepth;
                    return true;
                }
                return Vector3.Dot(delta, normal) < -Epsilon;
            }

            float enter = float.NegativeInfinity;
            float leave = float.PositiveInfinity;
            Vector3 enterNormal = Vector3.zero;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = Component(start, axis);
                float movement = Component(delta, axis);
                float lower = Component(lo, axis);
                float upper = Component(hi, axis);
                if (Mathf.Abs(movement) <= Epsilon)
                {
                    if (origin < lower || origin > upper) return false;
                    continue;
                }
                float first = (lower - origin) / movement;
                float second = (upper - origin) / movement;
                float sign = movement > 0f ? -1f : 1f;
                if (first > second)
                {
                    float swap = first;
                    first = second;
                    second = swap;
                    sign = -sign;
                }
                if (first > enter)
                {
                    enter = first;
                    enterNormal = AxisNormal(axis, sign);
                }
                if (second < leave) leave = second;
            }
            if (!Finite(enter) || enter > leave + Epsilon || leave < 0f || enter > 1f) return false;
            time = Mathf.Clamp01(enter);
            normal = enterNormal;
            return true;
        }

        private static bool Penetrates(Vector3 position, float radius, SweepObstacleDraft obstacle)
        {
            if (!obstacle.isBox)
            {
                Vector3 normal;
                float length;
                if (!UnitNormal(obstacle.normal, out normal, out length)) return true;
                float offset = obstacle.planeOffset / length;
                return Vector3.Dot(position, normal) - offset < radius - Epsilon;
            }
            Vector3 lo = obstacle.minimum - Vector3.one * radius;
            Vector3 hi = obstacle.maximum + Vector3.one * radius;
            return position.x > lo.x + Epsilon && position.x < hi.x - Epsilon &&
                position.y > lo.y + Epsilon && position.y < hi.y - Epsilon &&
                position.z > lo.z + Epsilon && position.z < hi.z - Epsilon;
        }

        // No Rigidbody/Collider is read or written. False means invalid input;
        // outputs never mutate scene state.
        public static bool TrySweepSphere(Vector3 start, Vector3 displacement, float radius,
            SweepObstacleDraft[] obstacles, int maxContacts, float separationEpsilon,
            out Vector3 finalPosition, out Vector3 remainingDisplacement,
            out SweepContactDraft[] contacts, out int contactCount,
            out bool stuck, out bool blocked)
        {
            finalPosition = start;
            remainingDisplacement = Vector3.zero;
            contacts = null;
            contactCount = 0;
            stuck = false;
            blocked = false;
            if (!Finite(start) || !Finite(displacement) || !Finite(radius) || radius < 0f ||
                obstacles == null || maxContacts < 1 || !Finite(separationEpsilon) || separationEpsilon < 0f)
                return false;
            for (int i = 0; i < obstacles.Length; i++)
                if (!ValidateObstacle(obstacles[i], radius)) return false;

            Vector3 position = start;
            Vector3 remaining = displacement;
            float timeCursor = 0f;
            float timeScale = 1f;
            bool unresolved = false;
            bool exhausted = false;
            bool blockedByContact = false;
            contacts = new SweepContactDraft[maxContacts];

            for (int step = 0; step < maxContacts; step++)
            {
                int hitIndex = -1;
                float hitTime = float.PositiveInfinity;
                Vector3 hitNormal = Vector3.zero;
                float hitDepth = 0f;
                for (int i = 0; i < obstacles.Length; i++)
                {
                    float candidateTime;
                    Vector3 candidateNormal;
                    float candidateDepth;
                    bool hit = TryHit(position, remaining, radius, obstacles[i],
                        out candidateTime, out candidateNormal, out candidateDepth);
                    if (hit && (candidateTime < hitTime - Epsilon ||
                        (Mathf.Abs(candidateTime - hitTime) <= Epsilon && (hitIndex < 0 || i < hitIndex))))
                    {
                        hitIndex = i;
                        hitTime = candidateTime;
                        hitNormal = candidateNormal;
                        hitDepth = candidateDepth;
                    }
                }
                if (hitIndex < 0)
                {
                    position += remaining;
                    if (!Finite(position)) { unresolved = true; break; }
                    remaining = Vector3.zero;
                    break;
                }

                position += remaining * hitTime;
                if (!Finite(position)) { unresolved = true; break; }
                Vector3 residual = remaining * (1f - hitTime);
                position += hitNormal * (hitDepth + separationEpsilon);
                if (!Finite(position) || !Finite(residual)) { unresolved = true; break; }
                float inward = Vector3.Dot(residual, hitNormal);
                if (!Finite(inward)) { unresolved = true; break; }
                if (inward < 0f) residual -= hitNormal * inward;
                if (!Finite(residual)) { unresolved = true; break; }

                if (contactCount < contacts.Length)
                {
                    SweepContactDraft contact = new SweepContactDraft();
                    contact.time = timeCursor + timeScale * hitTime;
                    contact.center = position;
                    contact.approximatePoint = position - hitNormal * radius;
                    contact.normal = hitNormal;
                    contact.obstacleIndex = hitIndex;
                    contact.penetrationDepth = hitDepth;
                    contacts[contactCount++] = contact;
                }
                float beforeLength, afterLength;
                if (!Length(remaining, out beforeLength) || !Length(residual, out afterLength))
                {
                    unresolved = true;
                    break;
                }
                if (beforeLength > Epsilon && afterLength <= Epsilon) blockedByContact = true;
                if (hitTime <= Epsilon && beforeLength > Epsilon &&
                    Mathf.Abs(residual.x - remaining.x) <= Epsilon &&
                    Mathf.Abs(residual.y - remaining.y) <= Epsilon &&
                    Mathf.Abs(residual.z - remaining.z) <= Epsilon)
                {
                    remaining = residual;
                    unresolved = true;
                    break;
                }
                timeCursor += timeScale * hitTime;
                timeScale *= 1f - hitTime;
                remaining = residual;
                if (step == maxContacts - 1 && afterLength > Epsilon) exhausted = true;
            }

            if (exhausted) unresolved = true;
            for (int i = 0; i < obstacles.Length; i++)
                if (Penetrates(position, radius, obstacles[i])) unresolved = true;

            finalPosition = position;
            remainingDisplacement = remaining;
            stuck = unresolved;
            blocked = blockedByContact && !unresolved;
            return Finite(finalPosition) && Finite(remainingDisplacement);
        }
    }
}
