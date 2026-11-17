using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// The three shape layers of section 4A: the visual core, the rolling shape (core
    /// sphere plus ball-local needle supports) and the pickup shape (the bounding-box
    /// support function unioned with the pins).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invariants this file must keep (and the tests pin):
    /// <c>r_roll = 1.00 * r_phys</c>,
    /// <c>rollReach(u) = max(r_roll, max_i support_i * dot(dir_i, u))</c>,
    /// <c>pickupReach(u) = max(r_phys * (|ux| + |uy| + |uz|), rollReach(u))</c>, so
    /// <c>r_roll &lt;= rollReach(u) &lt;= pickupReach(u)</c> for every u. The visual core
    /// is not computed here: it is the level's start radius, fixed
    /// (<c>KatamariBallMotor.GetVisualCoreRadiusWorldM</c>), and never larger than r_roll.
    /// The union with the pins is what keeps the user-facing ordering "pickup reaches
    /// further than rolling" true even when a long object sticks out past the box corner.
    /// </para>
    /// <para>
    /// Consumers are separated on purpose. The pickup reach is a trigger volume: it only
    /// answers "can this be absorbed" and must never block, push or support. The rolling
    /// shape is the contact solver's shape. The visual core is presentation only. This
    /// file computes all three from one physical radius and never stores state, so a
    /// caller cannot accidentally feed a pickup reach into a contact query.
    /// </para>
    /// <para>
    /// Needle supports are ball-local support lengths (>= core radius, clamped to the
    /// same protrusion cap the radial proxy already uses). Only pins protruding more than
    /// 1 mm enter the contact set, longest first, at most 16 (every pin: every attached
    /// prop registers into the table, so a short pin facing the ground must not lose its
    /// slot to a longer one facing the sky). Tip offsets rotate with the
    /// ball's visual roll rotation; the rotation is checked with the radial proxy's own
    /// unit-quaternion rule so both layers accept exactly the same transforms.
    /// </para>
    /// <para>
    /// The reference model (shapes.py) rotates with an explicit 3x3 matrix because it has
    /// no Unity types; this port uses the project's Quaternion, and the needle-vs-box test
    /// adds the same OBB validation the core query uses (the model's shape-side helper has
    /// no validation, but both queries read the same table, so they share one contract).
    /// </para>
    /// </remarks>
    public static class BallShapeMath
    {
        // Degenerate distance: the model's 1e-9 double epsilon at float scale.
        private const float Epsilon = 1e-6f;

        // A tip sitting exactly on the inflated face is a contact with depth 0; this
        // margin absorbs the float error of the entry parameter. At 1e-5 m it is 1/1000
        // of the 1 cm tip radius, so it cannot invent a contact that is not there.
        private const float SegmentHitMargin = 1e-5f;

        /// <summary>
        /// The physics clamp on how far a pin may protrude:
        /// <c>min(0.35 * r, 0.30 m)</c>.
        /// </summary>
        public static float NeedleProtrusionCap(float physicalRadius)
        {
            return Mathf.Min(ContactTuning.NeedleProtrusionPerRadius * physicalRadius,
                ContactTuning.NeedleProtrusionWorldM);
        }

        /// <summary>
        /// Clamps one needle support into <c>[core, core + cap]</c>, the constructor rule
        /// of the model's RollingShape.
        /// </summary>
        /// <remarks>
        /// False for a non-finite support, a non-finite or non-positive core radius, or a
        /// non-finite result.
        /// </remarks>
        public static bool TryClampNeedleSupport(float supportLength, float coreRadius,
            out float clampedSupport)
        {
            clampedSupport = 0f;
            if (!KatamariMath.IsFiniteFloat(supportLength)) return false;
            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius <= 0f) return false;
            float cap = NeedleProtrusionCap(coreRadius);
            if (!KatamariMath.IsFiniteFloat(cap) || cap < 0f) return false;
            float value = Mathf.Clamp(supportLength, coreRadius, coreRadius + cap);
            if (!KatamariMath.IsFiniteFloat(value)) return false;
            clampedSupport = value;
            return true;
        }

        /// <summary>
        /// Indices of the pins with a real protrusion, longest first, index ascending on
        /// ties, at most <see cref="ContactTuning.CompactNeedleCap"/> of them.
        /// </summary>
        /// <remarks>
        /// False means an unusable argument: a null or too-short buffer, a count beyond the
        /// arrays, or a non-positive core radius. The needed capacity is
        /// <c>min(protruding count, CompactNeedleCap)</c>, so a caller may size the buffer for the pins it
        /// expects and still be told when that is not enough. The supports must already be
        /// clamped by <see cref="TryClampNeedleSupport"/>: the model's shape clamps in its
        /// constructor and this layer has no shape object, so clamping is a separate call
        /// that the caller makes once, not something this scan silently redoes.
        /// </remarks>
        public static bool TryCollectProtrudingNeedles(float[] needleSupports,
            int needleCount, float coreRadius, int[] compactIndices, out int compactCount)
        {
            compactCount = 0;
            if (needleSupports == null || compactIndices == null) return false;
            if (needleCount < 0 || needleCount > needleSupports.Length) return false;
            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius <= 0f) return false;
            float threshold = coreRadius + ContactTuning.NeedleProtrusionThresholdM;

            int protruding = 0;
            for (int i = 0; i < needleCount; i++)
            {
                float support = needleSupports[i];
                if (!KatamariMath.IsFiniteFloat(support)) return false;
                if (support > threshold) protruding++;
            }
            int needed = Mathf.Min(protruding, ContactTuning.CompactNeedleCap);
            if (compactIndices.Length < needed) return false;

            int count = 0;
            for (int i = 0; i < needleCount; i++)
            {
                if (needleSupports[i] <= threshold) continue;
                int position = count;
                while (position > 0 && ComesBefore(needleSupports, i,
                    compactIndices[position - 1]))
                {
                    compactIndices[position] = compactIndices[position - 1];
                    position--;
                }
                compactIndices[position] = i;
                count++;
                if (count == ContactTuning.CompactNeedleCap) break;
            }
            compactCount = count;
            return true;
        }

        /// <summary>
        /// World-space tip offsets from the ball centre, one per selected pin, in the
        /// selection's order.
        /// </summary>
        /// <remarks>
        /// False for a null/short buffer, a count beyond the arrays, a degenerate rotation
        /// (not unit length, checked with the radial proxy's rule) or a non-finite tip.
        /// </remarks>
        public static bool TryTipOffsetsWorld(Vector3[] needleDirections,
            float[] needleSupports, int[] protrudingIndices, int protrudingCount,
            Quaternion rotation, Vector3[] tipOffsets)
        {
            if (needleDirections == null || needleSupports == null ||
                protrudingIndices == null || tipOffsets == null) return false;
            if (protrudingCount < 0 || protrudingCount > protrudingIndices.Length ||
                protrudingCount > tipOffsets.Length) return false;
            if (!RadialProxyMath.IsUnitRotation(rotation)) return false;
            for (int k = 0; k < protrudingCount; k++)
            {
                int index = protrudingIndices[k];
                if (index < 0 || index >= needleDirections.Length ||
                    index >= needleSupports.Length) return false;
                Vector3 localOffset = new Vector3(needleDirections[index].x
                    * needleSupports[index], needleDirections[index].y * needleSupports[index],
                    needleDirections[index].z * needleSupports[index]);
                Vector3 tip = rotation * localOffset;
                if (!KatamariMath.IsFiniteVector(tip)) return false;
                tipOffsets[k] = tip;
            }
            return true;
        }

        /// <summary>
        /// Point-vs-OBB contact for one needle tip: normal, penetration depth and the
        /// closest point on the solid.
        /// </summary>
        /// <remarks>
        /// This is the reference model's static point test (shapes.py). Production contact
        /// solving must sweep the tip from its previous position to this one (the pin also
        /// moves by omega x r), which is contact-step work, not shape math, and is not
        /// part of this library yet.
        /// </remarks>
        public static bool TryNeedleTipContact(Vector3 center, Vector3 axisX, Vector3 axisY,
            Vector3 axisZ, Vector3 halfExtents, Vector3 tip, float tipRadius,
            out Vector3 normal, out float depth, out Vector3 point)
        {
            normal = Vector3.zero;
            depth = 0f;
            point = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(tip)) return false;
            if (!KatamariMath.IsFiniteFloat(tipRadius) || tipRadius < 0f) return false;
            if (!ObbContactMath.IsValidObb(center, axisX, axisY, axisZ, halfExtents))
                return false;

            Vector3 local = ObbContactMath.ToLocal(tip, center, axisX, axisY, axisZ);
            float halfX = halfExtents.x;
            float halfY = halfExtents.y;
            float halfZ = halfExtents.z;
            float clampedX = Mathf.Clamp(local.x, -halfX, halfX);
            float clampedY = Mathf.Clamp(local.y, -halfY, halfY);
            float clampedZ = Mathf.Clamp(local.z, -halfZ, halfZ);
            float ex = local.x - clampedX;
            float ey = local.y - clampedY;
            float ez = local.z - clampedZ;
            float distance = Mathf.Sqrt(ex * ex + ey * ey + ez * ez);
            if (distance > tipRadius) return false;

            if (distance > Epsilon)
            {
                float inverse = 1f / distance;
                float nx = ex * inverse;
                float ny = ey * inverse;
                float nz = ez * inverse;
                normal = new Vector3(
                    axisX.x * nx + axisY.x * ny + axisZ.x * nz,
                    axisX.y * nx + axisY.y * ny + axisZ.y * nz,
                    axisX.z * nx + axisY.z * ny + axisZ.z * nz);
                point = new Vector3(
                    center.x + clampedX * axisX.x + clampedY * axisY.x + clampedZ * axisZ.x,
                    center.y + clampedX * axisX.y + clampedY * axisY.y + clampedZ * axisZ.y,
                    center.z + clampedX * axisX.z + clampedY * axisY.z + clampedZ * axisZ.z);
                depth = tipRadius - distance;
                return true;
            }

            // The tip is inside: push it out through the nearest face.
            int bestAxis = 0;
            float bestGap = halfX - Mathf.Abs(local.x);
            float gapY = halfY - Mathf.Abs(local.y);
            if (gapY < bestGap)
            {
                bestGap = gapY;
                bestAxis = 1;
            }
            float gapZ = halfZ - Mathf.Abs(local.z);
            if (gapZ < bestGap)
            {
                bestGap = gapZ;
                bestAxis = 2;
            }
            if (bestAxis == 0)
            {
                float sign = local.x < 0f ? -1f : 1f;
                normal = new Vector3(axisX.x * sign, axisX.y * sign, axisX.z * sign);
            }
            else if (bestAxis == 1)
            {
                float sign = local.y < 0f ? -1f : 1f;
                normal = new Vector3(axisY.x * sign, axisY.y * sign, axisY.z * sign);
            }
            else
            {
                float sign = local.z < 0f ? -1f : 1f;
                normal = new Vector3(axisZ.x * sign, axisZ.y * sign, axisZ.z * sign);
            }
            point = new Vector3(tip.x + normal.x * bestGap, tip.y + normal.y * bestGap,
                tip.z + normal.z * bestGap);
            depth = tipRadius + bestGap;
            return true;
        }

        /// <summary>
        /// Swept needle tip vs one oriented box: the segment from the previous tip to
        /// this one, swept with the tip's own radius.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Why a segment and not a point. The tip's world velocity is
        /// <c>v_centre + omega x r_tip</c>: at 4 m/s of rolling with r_roll = 0.15 the
        /// angular rate is 26.7 rad/s, so a pin 0.45 m out moves at ~16 m/s and one
        /// 20 ms substep sweeps 0.32 m - farther than a thin wall. A point test
        /// tunnels through that. The segment test cannot: the entry interval is
        /// computed analytically.
        /// </para>
        /// <para>
        /// Two stages. (1) A slab clip of the local segment against the box inflated
        /// by <paramref name="tipRadius"/> gives the entry/exit interval of the
        /// conservative volume (box (+) sphere is the box with rounded edges and
        /// corners, so the inflated box is a superset). (2) Inside that interval the
        /// EXACT closest-point distance is sampled at the entry and at four interior
        /// points, and the first sample at or inside the tip radius is bisected 12
        /// times, so an edge/corner pass lands within 1/4096 of the interval (~0.1 mm
        /// on a 0.32 m sweep). A face contact is found by the entry sample alone, i.e.
        /// one query; the bisection only runs for an edge/corner pass.
        /// </para>
        /// <para>
        /// Reported depth is <c>tipRadius - distance</c>, never negative: the entry is
        /// the first touch, so a face contact has depth 0 and a tip that starts inside
        /// reports <c>tipRadius + gap</c>. <paramref name="fraction"/> is the entry
        /// parameter in [0, 1]. The still-tip case (previous == now) degenerates to
        /// <see cref="TryNeedleTipContact"/> exactly, which the tests pin.
        /// </para>
        /// <para>
        /// Honest limit: a graze whose true contact interval is shorter than a quarter
        /// of the entry chord can be missed between two samples; on a 1 cm tip that is
        /// a sub-millimetre corner contact. Face and clear edge contacts are exact.
        /// False also means unusable input (non-finite tip/radius, an invalid box).
        /// </para>
        /// </remarks>
        public static bool TryNeedleSegmentContact(Vector3 center, Vector3 axisX,
            Vector3 axisY, Vector3 axisZ, Vector3 halfExtents, Vector3 tipPrevious,
            Vector3 tipNow, float tipRadius, out Vector3 normal, out float depth,
            out Vector3 point, out float fraction)
        {
            normal = Vector3.zero;
            depth = 0f;
            point = Vector3.zero;
            fraction = 0f;
            if (!KatamariMath.IsFiniteVector(tipPrevious)) return false;
            if (!KatamariMath.IsFiniteVector(tipNow)) return false;
            if (!KatamariMath.IsFiniteFloat(tipRadius) || tipRadius < 0f) return false;
            if (!ObbContactMath.IsValidObb(center, axisX, axisY, axisZ, halfExtents))
                return false;

            Vector3 localPrevious = ObbContactMath.ToLocal(tipPrevious, center, axisX,
                axisY, axisZ);
            Vector3 localNow = ObbContactMath.ToLocal(tipNow, center, axisX, axisY, axisZ);
            float directionX = localNow.x - localPrevious.x;
            float directionY = localNow.y - localPrevious.y;
            float directionZ = localNow.z - localPrevious.z;

            float enter = 0f;
            float leave = 1f;
            if (!ClipSlab(localPrevious.x, directionX, halfExtents.x + tipRadius,
                ref enter, ref leave)) return false;
            if (!ClipSlab(localPrevious.y, directionY, halfExtents.y + tipRadius,
                ref enter, ref leave)) return false;
            if (!ClipSlab(localPrevious.z, directionZ, halfExtents.z + tipRadius,
                ref enter, ref leave)) return false;
            if (enter > leave) return false;

            float span = leave - enter;
            float previousFraction = enter;
            for (int sample = 0; sample < 5; sample++)
            {
                float candidate = enter;
                if (sample > 0) candidate = enter + span * (0.25f * (float)sample);
                Vector3 tip = new Vector3(
                    tipPrevious.x + (tipNow.x - tipPrevious.x) * candidate,
                    tipPrevious.y + (tipNow.y - tipPrevious.y) * candidate,
                    tipPrevious.z + (tipNow.z - tipPrevious.z) * candidate);
                Vector3 candidateNormal;
                Vector3 candidatePoint;
                float distance;
                int feature;
                if (!ObbContactMath.TryClosestPointOnObb(center, axisX, axisY, axisZ,
                    halfExtents, tip, out candidatePoint, out distance, out candidateNormal,
                    out feature)) return false;
                if (distance <= tipRadius + SegmentHitMargin)
                {
                    if (sample == 0)
                    {
                        normal = candidateNormal;
                        point = candidatePoint;
                        depth = SegmentDepth(tipRadius, distance);
                        fraction = candidate;
                        return true;
                    }
                    float low = previousFraction;
                    float high = candidate;
                    for (int iteration = 0; iteration < 12; iteration++)
                    {
                        float middle = 0.5f * (low + high);
                        Vector3 middleTip = new Vector3(
                            tipPrevious.x + (tipNow.x - tipPrevious.x) * middle,
                            tipPrevious.y + (tipNow.y - tipPrevious.y) * middle,
                            tipPrevious.z + (tipNow.z - tipPrevious.z) * middle);
                        Vector3 middleNormal;
                        Vector3 middlePoint;
                        float middleDistance;
                        int middleFeature;
                        if (!ObbContactMath.TryClosestPointOnObb(center, axisX, axisY,
                            axisZ, halfExtents, middleTip, out middlePoint,
                            out middleDistance, out middleNormal, out middleFeature))
                            return false;
                        if (middleDistance <= tipRadius + SegmentHitMargin)
                        {
                            high = middle;
                            distance = middleDistance;
                            candidateNormal = middleNormal;
                            candidatePoint = middlePoint;
                        }
                        else
                        {
                            low = middle;
                        }
                    }
                    normal = candidateNormal;
                    point = candidatePoint;
                    depth = SegmentDepth(tipRadius, distance);
                    fraction = high;
                    return true;
                }
                previousFraction = candidate;
            }
            return false;
        }

        /// <summary>
        /// Swept needle tip vs one one-sided plane: the segment from the previous tip
        /// to this one, with the tip radius.
        /// </summary>
        /// <remarks>
        /// Same convention as <see cref="ObbContactMath.TryPlaneContact"/> (the free
        /// side is <c>dot(normal, x) &gt;= offset</c>): false means no contact or
        /// unusable input, and the reported depth is <c>tipRadius - signedDistance</c>
        /// at the entry, never negative. A tip that already starts inside reports the
        /// start (fraction 0), exactly like the box sweep.
        /// </remarks>
        public static bool TryNeedleSegmentPlaneContact(Vector3 planeNormal,
            float planeOffset, Vector3 tipPrevious, Vector3 tipNow, float tipRadius,
            out Vector3 normal, out float depth, out Vector3 point, out float fraction)
        {
            normal = Vector3.zero;
            depth = 0f;
            point = Vector3.zero;
            fraction = 0f;
            if (!KatamariMath.IsFiniteVector(tipPrevious)) return false;
            if (!KatamariMath.IsFiniteVector(tipNow)) return false;
            if (!KatamariMath.IsFiniteFloat(tipRadius) || tipRadius < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(planeOffset)) return false;
            Vector3 unitNormal;
            float normalLength;
            if (!KatamariMath.TryNormalize(planeNormal, out unitNormal, out normalLength))
                return false;

            float start = unitNormal.x * tipPrevious.x + unitNormal.y * tipPrevious.y
                + unitNormal.z * tipPrevious.z - planeOffset;
            float end = unitNormal.x * tipNow.x + unitNormal.y * tipNow.y
                + unitNormal.z * tipNow.z - planeOffset;
            if (!KatamariMath.IsFiniteFloat(start) || !KatamariMath.IsFiniteFloat(end))
                return false;

            float candidate;
            if (start <= tipRadius + SegmentHitMargin)
            {
                candidate = 0f;
            }
            else if (end <= tipRadius)
            {
                float denominator = start - end;
                if (!KatamariMath.IsFiniteFloat(denominator) || denominator <= 0f)
                    return false;
                candidate = (start - tipRadius) / denominator;
                if (candidate < 0f) candidate = 0f;
                if (candidate > 1f) candidate = 1f;
            }
            else
            {
                return false;
            }

            Vector3 entry = new Vector3(
                tipPrevious.x + (tipNow.x - tipPrevious.x) * candidate,
                tipPrevious.y + (tipNow.y - tipPrevious.y) * candidate,
                tipPrevious.z + (tipNow.z - tipPrevious.z) * candidate);
            float signedDistance = unitNormal.x * entry.x + unitNormal.y * entry.y
                + unitNormal.z * entry.z - planeOffset;
            if (!KatamariMath.IsFiniteFloat(signedDistance)) return false;
            normal = unitNormal;
            point = new Vector3(entry.x - unitNormal.x * signedDistance,
                entry.y - unitNormal.y * signedDistance,
                entry.z - unitNormal.z * signedDistance);
            depth = SegmentDepth(tipRadius, signedDistance);
            fraction = candidate;
            return true;
        }

        /// <summary>
        /// <c>r - d</c>, clamped at zero: the entry of a sweep is the first touch, so a
        /// contact can never report a negative depth.
        /// </summary>
        private static float SegmentDepth(float tipRadius, float signedDistance)
        {
            float value = tipRadius - signedDistance;
            if (value < 0f) return 0f;
            return value;
        }

        /// <summary>
        /// One-axis clip of a segment against the slab <c>|origin + t*direction| &lt;= half</c>.
        /// </summary>
        /// <remarks>
        /// False means the segment misses the slab on this axis. An axis-parallel
        /// segment is inside when its origin is inside, which is why the still-tip case
        /// keeps the whole [0, 1] interval and degenerates to the point test.
        /// </remarks>
        private static bool ClipSlab(float origin, float direction, float half,
            ref float enter, ref float leave)
        {
            if (half < 0f) return false;
            float absoluteDirection = direction < 0f ? -direction : direction;
            if (absoluteDirection < 1e-9f)
            {
                float absoluteOrigin = origin < 0f ? -origin : origin;
                return absoluteOrigin <= half;
            }
            float first = (-half - origin) / direction;
            float second = (half - origin) / direction;
            float low = first < second ? first : second;
            float high = first < second ? second : first;
            if (!KatamariMath.IsFiniteFloat(low) || !KatamariMath.IsFiniteFloat(high))
                return false;
            if (low > enter) enter = low;
            if (high < leave) leave = high;
            return enter <= leave;
        }

        /// <summary>
        /// Support function of the cube that bounds the equal-volume sphere:
        /// <c>r * (|ux| + |uy| + |uz|)</c>, i.e. 1.0 r on an axis, 1.41 r on a face
        /// diagonal and 1.73 r at a corner.
        /// </summary>
        public static bool TryPickupBoxReach(Vector3 direction, float physicalRadius,
            out float reach)
        {
            reach = 0f;
            if (!KatamariMath.IsFiniteFloat(physicalRadius) || physicalRadius < 0f) return false;
            Vector3 unit;
            float length;
            if (!KatamariMath.TryNormalize(direction, out unit, out length)) return false;
            float value = physicalRadius * (Mathf.Abs(unit.x) + Mathf.Abs(unit.y) +
                Mathf.Abs(unit.z));
            if (!KatamariMath.IsFiniteFloat(value)) return false;
            reach = value;
            return true;
        }

        /// <summary>
        /// How far the rolling shape reaches along a world direction: the core radius or
        /// the longest projecting pin, whichever is larger.
        /// </summary>
        /// <remarks>
        /// The direction is normalised (a degenerate one refuses), and every pin counts,
        /// not just the compact set: this is a support function of the whole shape.
        /// </remarks>
        public static bool TryRollingReach(Vector3 direction, float coreRadius,
            Vector3[] needleDirections, float[] needleSupports, int needleCount,
            out float reach)
        {
            reach = 0f;
            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius < 0f) return false;
            if (needleDirections == null || needleSupports == null) return false;
            if (needleCount < 0 || needleCount > needleDirections.Length ||
                needleCount > needleSupports.Length) return false;
            Vector3 unit;
            float length;
            if (!KatamariMath.TryNormalize(direction, out unit, out length)) return false;
            float best = coreRadius;
            for (int i = 0; i < needleCount; i++)
            {
                Vector3 local = needleDirections[i];
                float projected = needleSupports[i] * (local.x * unit.x + local.y * unit.y +
                    local.z * unit.z);
                if (projected > best) best = projected;
            }
            if (!KatamariMath.IsFiniteFloat(best)) return false;
            reach = best;
            return true;
        }

        /// <summary>
        /// The pickup/trigger reach: the box support function unioned with the pins, so it
        /// is never smaller than the rolling reach.
        /// </summary>
        /// <remarks>
        /// A caller with no rolling shape passes a zero needle count and gets the box
        /// alone. The rolling shape's core radius is the physical radius here because the
        /// section 4A invariant fixes <c>r_roll = 1.00 * r_phys</c>; there is no second
        /// radius to pass. This value must never reach a contact query: pickup answers "can
        /// this be absorbed", never "does this block".
        /// </remarks>
        public static bool TryPickupReach(Vector3 direction, float physicalRadius,
            Vector3[] needleDirections, float[] needleSupports, int needleCount,
            out float reach)
        {
            reach = 0f;
            float boxReach;
            if (!TryPickupBoxReach(direction, physicalRadius, out boxReach)) return false;
            if (needleCount <= 0)
            {
                reach = boxReach;
                return true;
            }
            float rollingReach;
            if (!TryRollingReach(direction, physicalRadius, needleDirections, needleSupports,
                needleCount, out rollingReach)) return false;
            reach = Mathf.Max(boxReach, rollingReach);
            return true;
        }

        /// <summary>
        /// 0..1 grip from a protrusion: a protrusion of half a core radius already gives
        /// the full <paramref name="kGrip"/>.
        /// </summary>
        /// <remarks>
        /// Zero for a non-positive core radius (the model returns zero, it does not raise).
        /// </remarks>
        public static float GripFromProtrusion(float protrusionM, float coreRadius,
            float kGrip = ContactTuning.NeedleSteeperGrip)
        {
            if (!KatamariMath.IsFiniteFloat(protrusionM)) return 0f;
            if (!KatamariMath.IsFiniteFloat(coreRadius) || coreRadius <= 0f) return 0f;
            return Mathf.Clamp(protrusionM / (0.5f * coreRadius), 0f, 1f) * kGrip;
        }

        /// <summary>
        /// The climb speed gate with the needle grip discount: a half-radius protrusion
        /// halves v_req, so a light push can climb a wall a pin is hooked into.
        /// </summary>
        public static float RequiredSpeedWithGrip(float thetaDeg, float protrusionM,
            float coreRadius, float vMin = ContactTuning.VMinGate,
            float alpha = ContactTuning.GateAlpha,
            float kGrip = ContactTuning.NeedleSteeperGrip)
        {
            float baseSpeed = ClimbMath.RequiredSpeed(thetaDeg, vMin, alpha);
            return baseSpeed * (1f - GripFromProtrusion(protrusionM, coreRadius, kGrip));
        }

        private static bool ComesBefore(float[] supports, int candidate, int current)
        {
            if (supports[candidate] > supports[current]) return true;
            if (supports[candidate] < supports[current]) return false;
            return candidate < current;
        }
    }
}
