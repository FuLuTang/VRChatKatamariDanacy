using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// The outcome of one absorb decision: either the items are in contact and the item
    /// may be absorbed, or the model's first failing gate, in the model's gate order.
    /// </summary>
    /// <remarks>
    /// Port of the boolean gates in `eligible()` in Docs/Design/PickupAbsorb/
    /// pickup_absorb.py. The model returns a single bool and applies the gates in this
    /// exact order, so the first failing gate is the reported reason. InvalidInput is a
    /// port-level guard, not a model gate: the model assumes a registered item with
    /// non-negative extents and a non-negative ball radius, and this layer says so
    /// instead of silently computing nonsense.
    /// </remarks>
    public enum AbsorbOutcome
    {
        Absorb = 0,
        RejectNotAbsorbable = 1,
        RejectKindNotAllowed = 2,
        RejectItemTooLarge = 3,
        RejectBallAtGrowthCap = 4,
        RejectOutOfReach = 5,
        RejectInvalidInput = 6
    }

    /// <summary>
    /// The size-threshold absorb decision, the ball growth-radius mapping and the
    /// ledger-facing gain conversion, as stateless math over value inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of Docs/Design/PickupAbsorb/pickup_absorb.py, the DRAFT offline reference
    /// model. The real-time contact test is a reach comparison, not a sweep or a
    /// physics query: the caller supplies the ball's collision radius in metres, the
    /// item's world bounds and both world positions.
    /// </para>
    /// <para>
    /// Units and quantities. Lengths are metres in the project's single world space
    /// (+Y up). Bounds are the item's full axis-aligned extents, not half extents;
    /// the model's size gate compares the largest full extent against
    /// `max_item_size_world_m`, and this port keeps exactly that comparison. The
    /// bounding-sphere radius used for contact is derived from the half extents,
    /// `sqrt((x/2)^2 + (y/2)^2 + (z/2)^2)`, as the model does. Values are in
    /// caller-defined volume units; the model's item value is a float, and the ledger's
    /// gain is an integer of the same unit (see
    /// <see cref="TryAbsorbValueToLedgerGain"/>).
    /// </para>
    /// <para>
    /// The reach comparison uses the *collision radius* and never the semantic
    /// diameter. Those are different quantities in this project (see
    /// <c>SizeModelMath.TryDerive</c>): the semantic diameter is the narrative size a
    /// player reads, it may differ from the physical metres, and the model's `eligible`
    /// is called with the ball radius. No diameter parameter exists in this layer, so a
    /// caller cannot accidentally pass a diameter as if it were a radius.
    /// </para>
    /// <para>
    /// Contact test. `distance &lt;= collisionRadius + contactRadius + itemRadius`, where
    /// itemRadius is the item's bounding-sphere radius. This is the model's deliberately
    /// loose bounds proxy: it grants contact when the bounding sphere overlaps the ball's
    /// reach. It models no occlusion, no velocity plausibility and no item state machine,
    /// and the model's README says the same. Comparisons keep the model's strictness:
    /// the size gate rejects only a strictly larger extent, the growth cap rejects a
    /// radius that has already reached the cap, and a distance exactly equal to the reach
    /// is contact.
    /// </para>
    /// <para>
    /// This layer owns no state and allocates nothing. It deliberately does not port the
    /// model's UniformGrid `SpatialRegistry` (a broadphase container, and Udon has no
    /// List/Dictionary to back it) or the model's `Ball` confirmed/predicted dicts
    /// (which would be a second ledger; the confirmed ledger is
    /// <see cref="VRCKatamari.Economy.GrowthLedgerMath"/>). Candidate collection from a
    /// spatial structure is a host-side concern that feeds this decision; the decision
    /// itself is pure.
    /// </para>
    /// <para>
    /// Failure is a bool return or a reject outcome, never an exception. Every entry
    /// point validates all inputs before computing, so a non-finite coordinate or an
    /// unrepresentable intermediate is rejected rather than compared.
    /// </para>
    /// </remarks>
    public static class PickupAbsorbMath
    {
        /// <summary>
        /// Decides whether one item may be absorbed by the ball right now.
        /// </summary>
        /// <remarks>
        /// The gates run in the model's order, so the returned value is the model's first
        /// failing condition: not absorbable; kind outside the allowed set; largest full
        /// extent strictly greater than maxItemSizeWorldM; collision radius already at or
        /// above maxGrowthRadiusWorldM; contact distance strictly greater than the reach.
        /// Absorb is the model's `eligible` returning true. Allowed kinds are an ordinal
        /// text set supplied by the caller; the model uses a frozenset and no default
        /// tuning is invented here. `collisionRadiusWorldM` is the ball's collision
        /// radius, not its semantic diameter. A negative collision radius, negative
        /// extent, null id or non-finite value is RejectInvalidInput; the model's registry
        /// rejects negative value/extent and a negative query radius, but `eligible`
        /// itself assumes they were already checked.
        /// </remarks>
        public static AbsorbOutcome EvaluateAbsorb(
            bool itemAbsorbable,
            string itemKind,
            string[] allowedKinds,
            int allowedKindCount,
            Vector3 itemBounds,
            Vector3 itemPosition,
            Vector3 ballPosition,
            float collisionRadiusWorldM,
            float contactRadiusWorldM,
            float maxItemSizeWorldM,
            float maxGrowthRadiusWorldM)
        {
            if (itemKind == null || allowedKinds == null) return AbsorbOutcome.RejectInvalidInput;
            if (allowedKindCount < 0 || allowedKindCount > allowedKinds.Length)
                return AbsorbOutcome.RejectInvalidInput;
            if (!KatamariMath.IsFiniteVector(itemBounds) || !KatamariMath.IsFiniteVector(itemPosition) ||
                !KatamariMath.IsFiniteVector(ballPosition))
                return AbsorbOutcome.RejectInvalidInput;
            if (!KatamariMath.IsFiniteFloat(collisionRadiusWorldM) ||
                !KatamariMath.IsFiniteFloat(contactRadiusWorldM) ||
                !KatamariMath.IsFiniteFloat(maxItemSizeWorldM) ||
                !KatamariMath.IsFiniteFloat(maxGrowthRadiusWorldM))
                return AbsorbOutcome.RejectInvalidInput;
            if (itemBounds.x < 0f || itemBounds.y < 0f || itemBounds.z < 0f)
                return AbsorbOutcome.RejectInvalidInput;
            if (collisionRadiusWorldM < 0f) return AbsorbOutcome.RejectInvalidInput;

            if (!itemAbsorbable) return AbsorbOutcome.RejectNotAbsorbable;
            if (!IsKindAllowed(allowedKinds, allowedKindCount, itemKind))
                return AbsorbOutcome.RejectKindNotAllowed;

            float largestExtent = Mathf.Max(itemBounds.x, Mathf.Max(itemBounds.y, itemBounds.z));
            if (largestExtent > maxItemSizeWorldM) return AbsorbOutcome.RejectItemTooLarge;
            if (collisionRadiusWorldM >= maxGrowthRadiusWorldM)
                return AbsorbOutcome.RejectBallAtGrowthCap;

            float dx = itemPosition.x - ballPosition.x;
            float dy = itemPosition.y - ballPosition.y;
            float dz = itemPosition.z - ballPosition.z;
            if (!KatamariMath.IsFiniteFloat(dx) || !KatamariMath.IsFiniteFloat(dy) ||
                !KatamariMath.IsFiniteFloat(dz))
                return AbsorbOutcome.RejectInvalidInput;
            float distanceSquared = dx * dx + dy * dy + dz * dz;
            if (!KatamariMath.IsFiniteFloat(distanceSquared)) return AbsorbOutcome.RejectInvalidInput;
            float distance = Mathf.Sqrt(distanceSquared);

            float halfX = itemBounds.x * 0.5f;
            float halfY = itemBounds.y * 0.5f;
            float halfZ = itemBounds.z * 0.5f;
            float itemRadiusSquared = halfX * halfX + halfY * halfY + halfZ * halfZ;
            if (!KatamariMath.IsFiniteFloat(itemRadiusSquared))
                return AbsorbOutcome.RejectInvalidInput;
            float itemRadius = Mathf.Sqrt(itemRadiusSquared);

            float reach = collisionRadiusWorldM + contactRadiusWorldM + itemRadius;
            if (!KatamariMath.IsFiniteFloat(reach)) return AbsorbOutcome.RejectInvalidInput;
            return distance <= reach ? AbsorbOutcome.Absorb : AbsorbOutcome.RejectOutOfReach;
        }

        /// <summary>
        /// Derives the ball's growth radius from its confirmed value.
        /// </summary>
        /// <remarks>
        /// The model's Ball.radius: `min(maxRadius, baseRadius + growthPerValue *
        /// confirmedValue)`, capped so a huge confirmed total cannot push the ball past
        /// the caller's maximum. The model validates its constructor arguments (all
        /// non-negative, base at most max) and rejects a negative confirmed value; that
        /// validation is preserved. `confirmedValueUnits` must be the *confirmed* total
        /// only: feeding it a predicted total would grow the ball from unconfirmed data.
        /// The radius is metres; the value is in volume units. When the sum is not
        /// representable as a float the model's `min` yields the cap, and so does this
        /// port; no accepted input can produce a NaN or a radius above the cap.
        /// </remarks>
        public static bool TryComputeGrowthRadius(
            float baseRadiusWorldM,
            float growthPerValueWorldM,
            float maxRadiusWorldM,
            float confirmedValueUnits,
            out float radiusWorldM)
        {
            radiusWorldM = 0f;
            if (!KatamariMath.IsFiniteFloat(baseRadiusWorldM) ||
                !KatamariMath.IsFiniteFloat(growthPerValueWorldM) ||
                !KatamariMath.IsFiniteFloat(maxRadiusWorldM) ||
                !KatamariMath.IsFiniteFloat(confirmedValueUnits)) return false;
            if (baseRadiusWorldM < 0f || growthPerValueWorldM < 0f || maxRadiusWorldM < 0f)
                return false;
            if (baseRadiusWorldM > maxRadiusWorldM) return false;
            if (confirmedValueUnits < 0f) return false;

            float sum = baseRadiusWorldM + growthPerValueWorldM * confirmedValueUnits;
            if (!KatamariMath.IsFiniteFloat(sum))
            {
                radiusWorldM = maxRadiusWorldM;
                return true;
            }
            radiusWorldM = sum < maxRadiusWorldM ? sum : maxRadiusWorldM;
            return true;
        }

        /// <summary>
        /// Converts one absorbed item's float value into the ledger's integer gain.
        /// </summary>
        /// <remarks>
        /// The two DRAFT models use different numeric domains: the pickup model's
        /// `Item.value` is a float and the ledger's event gain is an int of the same
        /// volume unit. Neither model defines a rounding rule, and inventing one would
        /// silently change a total, so a non-integral value, a negative value, a
        /// non-finite value and a value outside the 32-bit integer range are all
        /// rejected. An accepted value maps 1:1 with no scaling and no rounding, and the
        /// result is only the accounting input: it becomes real value only once
        /// <see cref="VRCKatamari.Economy.GrowthLedgerMath.TryResolveClaims"/> or
        /// <see cref="VRCKatamari.Economy.GrowthLedgerMath.TryApplyCommit"/> commits the
        /// matching event key.
        /// </remarks>
        public static bool TryAbsorbValueToLedgerGain(float itemValue, out int gain)
        {
            gain = 0;
            if (!KatamariMath.IsFiniteFloat(itemValue) || itemValue < 0f) return false;
            // 2^31 is exactly representable as a float; comparing against it first keeps
            // the integer cast below inside the representable range.
            if (itemValue >= 2147483648f) return false;
            int truncated = (int)itemValue;
            // A fractional value has no defined rounding policy in either model.
            if ((float)truncated != itemValue) return false;
            gain = truncated;
            return true;
        }

        private static bool IsKindAllowed(string[] allowedKinds, int allowedKindCount, string kind)
        {
            for (int index = 0; index < allowedKindCount; index++)
            {
                if (allowedKinds[index] == null) continue;
                if (string.CompareOrdinal(allowedKinds[index], kind) == 0) return true;
            }
            return false;
        }
    }
}
