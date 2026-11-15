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
        RejectInvalidInput = 6,
        /// <summary>
        /// The item is in reach and of an allowed kind, but its ledger gain truncates
        /// to zero, so committing it would resolve to "no row" and the item would
        /// vanish with no score, no size change and no event. Refused visibly instead.
        /// </summary>
        RejectNoGain = 7
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
    /// (+Y up); the volumes this layer compares are cubic metres (the `WorldM3` suffix).
    /// Bounds are the item's full axis-aligned extents, not half extents; the size gate
    /// multiplies the three of them, so it compares the item's AABB volume, and this
    /// port keeps exactly that comparison. The bounding-sphere radius used for contact is
    /// derived from the half extents, `sqrt((x/2)^2 + (y/2)^2 + (z/2)^2)`, as the
    /// model does. Values are in caller-defined value units; the model's item value
    /// is a float, and the ledger's gain is an integer of the same unit (see
    /// <see cref="TryAbsorbValueToLedgerGain"/>).
    /// </para>
    /// <para>
    /// The size gate is a VOLUME gate relative to the ball (Docs/OPEN_DECISIONS.md (l),
    /// which supersedes (h)): an item is absorbable when its AABB volume `x*y*z` is at
    /// most the ball's own volume `(pi/6) * semanticDiameter^3` divided by the absorb
    /// volume ratio (10 - the ball must be ten times the item's volume), bounded above
    /// by a serialized safety ceiling in cubic metres.
    /// <see cref="TryComputeAbsorbVolumeLimit"/> resolves that one number;
    /// <see cref="EvaluateAbsorbWithBallSize"/> and
    /// <see cref="EvaluateAbsorbWithValueAndBallSize"/> are the gameplay entry points
    /// that use it. "Too large" has exactly one definition here: the gate and the
    /// host-side blocking predicate both ask <see cref="IsVolumeBeyondLimit"/>.
    /// </para>
    /// <para>
    /// Why volume and not the longest edge. The original's gate is
    /// `V_ball &gt;= 10 * V_item`, i.e. `a*b*c &lt;= (pi/6) * D^3 / 10` with D the ball's
    /// semantic diameter (Docs/Research/KatamariBallRules/ABSORB_SIZE_RATIO.md, evidence
    /// level: medium - one community data-mining statement of the 10x constant, nine
    /// back-solved item volumes, three independent side observations). A linear ratio
    /// cannot express that quantity: edges that differ by 6x differ in volume by 216x, so
    /// one 0.30 m cube weighs as much as 216 of the 0.05 m ones, and a slender prop (a
    /// bat, a pencil) passes a volume gate that a longest-edge gate refuses. The AABB
    /// volume is the original's own quantity and it counts empty space, so a sphere-like
    /// prop is over-counted by `6/pi` (~1.91x) and needs a ball ~1.24x larger - faithful
    /// to the original, not a bug.
    /// </para>
    /// <para>
    /// Two size quantities are still not interchangeable: the round model compares the
    /// item's declared `pickup_diameter_m` (a length), while this layer compares the
    /// bounds' AABB volume, so the two produce the same verdict only when the author data
    /// fills bounds consistently with the declared pickup diameter.
    /// </para>
    /// <para>
    /// The reach comparison uses the *collision radius* and never the semantic
    /// diameter. Those are different quantities in this project (see
    /// <c>SizeModelMath.TryDerive</c>): the semantic diameter is the narrative size a
    /// player reads, it may differ from the physical metres, and the model's `eligible`
    /// is called with the ball radius. Reach has no diameter parameter, so a caller
    /// cannot pass a diameter as if it were a radius. The size gate is the one place a
    /// semantic diameter appears, and it is consumed there as a diameter (cubed, inside
    /// <see cref="TryComputeAbsorbVolumeLimit"/>) and never added to reach. The item's
    /// bounding-sphere radius used by reach and its AABB volume used by the size gate are
    /// also two different quantities, read from the same `bounds`: a slender prop can have
    /// a large radius and a tiny volume.
    /// </para>
    /// <para>
    /// Contact test. `distance &lt;= collisionRadius + contactRadius + itemRadius`, where
    /// itemRadius is the item's bounding-sphere radius. This is the model's deliberately
    /// loose bounds proxy: it grants contact when the bounding sphere overlaps the ball's
    /// reach. It models no occlusion, no velocity plausibility and no item state machine,
    /// and the model's README says the same. Comparisons keep the model's strictness:
    /// the size gate rejects only a strictly larger AABB volume, the growth cap rejects a
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
        /// failing condition: not absorbable; kind outside the allowed set; AABB volume
        /// strictly greater than the volume limit; collision radius already at or
        /// above maxGrowthRadiusWorldM; contact distance strictly greater than the reach.
        /// Absorb is the model's `eligible` returning true. Allowed kinds are an ordinal
        /// text set supplied by the caller; the model uses a frozenset and no default
        /// tuning is invented here. `collisionRadiusWorldM` is the ball's collision
        /// radius, not its semantic diameter. A negative collision radius, negative
        /// extent, null id or non-finite value is RejectInvalidInput; the model's registry
        /// rejects negative value/extent and a negative query radius, but `eligible`
        /// itself assumes they were already checked. An extent triple whose product
        /// overflows float is also RejectInvalidInput: it is an unrepresentable volume,
        /// not a large item.
        /// <para>
        /// `maxItemVolumeWorldM3` is the volume limit the caller has already resolved for
        /// this step, and it is the only size limit this method applies. The gameplay path
        /// resolves it with <see cref="TryComputeAbsorbVolumeLimit"/> (the ball's own
        /// volume divided by the absorb volume ratio, bounded by the serialized ceiling)
        /// and calls <see cref="EvaluateAbsorbWithBallSize"/>; calling this method
        /// directly with the serialized ceiling is the absolute-gate behaviour and is what
        /// a caller who deliberately wants a fixed cap uses. Passing the resolved limit
        /// here keeps the gate itself in one place.
        /// </para>
        /// <para>
        /// The contact geometry (the item's bounding-sphere radius and the reach sum) is
        /// <see cref="TryComputeReach"/>; this method only compares the centre distance
        /// against it, so a caller that needs the number the gate used asks for the same
        /// function instead of re-deriving it.
        /// </para>
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
            float maxItemVolumeWorldM3,
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
                !KatamariMath.IsFiniteFloat(maxItemVolumeWorldM3) ||
                !KatamariMath.IsFiniteFloat(maxGrowthRadiusWorldM))
                return AbsorbOutcome.RejectInvalidInput;
            if (itemBounds.x < 0f || itemBounds.y < 0f || itemBounds.z < 0f)
                return AbsorbOutcome.RejectInvalidInput;
            if (collisionRadiusWorldM < 0f) return AbsorbOutcome.RejectInvalidInput;

            float itemVolumeWorldM3;
            if (!TryComputeItemVolume(itemBounds, out itemVolumeWorldM3))
                return AbsorbOutcome.RejectInvalidInput;

            if (!itemAbsorbable) return AbsorbOutcome.RejectNotAbsorbable;
            if (!IsKindAllowed(allowedKinds, allowedKindCount, itemKind))
                return AbsorbOutcome.RejectKindNotAllowed;

            if (IsVolumeBeyondLimit(itemVolumeWorldM3, maxItemVolumeWorldM3))
                return AbsorbOutcome.RejectItemTooLarge;
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

            float itemRadius;
            float reach;
            if (!TryComputeReach(itemBounds, collisionRadiusWorldM, contactRadiusWorldM,
                out itemRadius, out reach))
                return AbsorbOutcome.RejectInvalidInput;
            return distance <= reach ? AbsorbOutcome.Absorb : AbsorbOutcome.RejectOutOfReach;
        }

        /// <summary>
        /// The contact geometry both the gate and every report of the gate's threshold use:
        /// the item's bounding-sphere radius and the ball's reach.
        /// </summary>
        /// <remarks>
        /// <para>
        /// `itemRadius = sqrt((x/2)^2 + (y/2)^2 + (z/2)^2)` over the item's FULL extents, and
        /// `reach = collisionRadiusWorldM + contactRadiusWorldM + itemRadius`. Both are
        /// metres and are only summed: no scaling, no falloff, no epsilon. This is the
        /// deliberate bounds proxy described in the class remarks, and this method is the
        /// only place it exists: <see cref="EvaluateAbsorb"/> decides with it, and a caller
        /// that reports the threshold it refused with (the assembly layer's rejection
        /// diagnostics) asks for the same number instead of re-deriving a second copy that
        /// can drift.
        /// </para>
        /// <para>
        /// False means the inputs are not a legal item geometry at all: a non-finite bounds
        /// component, radius or contact margin, a negative extent, a negative collision
        /// radius (all of which the gate reports as invalid input), or a non-representable
        /// intermediate. Both out values are then zero. Which side of `reach` a candidate
        /// falls on is NOT decided here; the comparison stays the gate's.
        /// </para>
        /// </remarks>
        public static bool TryComputeReach(Vector3 itemBounds, float collisionRadiusWorldM,
            float contactRadiusWorldM, out float itemRadiusM, out float reachM)
        {
            itemRadiusM = 0f;
            reachM = 0f;
            if (!KatamariMath.IsFiniteVector(itemBounds)) return false;
            if (!KatamariMath.IsFiniteFloat(collisionRadiusWorldM)) return false;
            if (!KatamariMath.IsFiniteFloat(contactRadiusWorldM)) return false;
            if (itemBounds.x < 0f || itemBounds.y < 0f || itemBounds.z < 0f) return false;
            if (collisionRadiusWorldM < 0f) return false;

            float halfX = itemBounds.x * 0.5f;
            float halfY = itemBounds.y * 0.5f;
            float halfZ = itemBounds.z * 0.5f;
            float itemRadiusSquared = halfX * halfX + halfY * halfY + halfZ * halfZ;
            if (!KatamariMath.IsFiniteFloat(itemRadiusSquared)) return false;
            float itemRadius = Mathf.Sqrt(itemRadiusSquared);

            float reach = collisionRadiusWorldM + contactRadiusWorldM + itemRadius;
            if (!KatamariMath.IsFiniteFloat(reach)) return false;
            itemRadiusM = itemRadius;
            reachM = reach;
            return true;
        }

        /// <summary>
        /// The item's AABB volume, `x * y * z`, the quantity the size gate compares.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cubic metres, from the item's full extents. `false` for a non-finite or
        /// negative side, and for a product that overflows float: an unrepresentable
        /// volume is an invalid input, not a large item, so the gate reports
        /// <see cref="AbsorbOutcome.RejectInvalidInput"/> instead of silently letting a
        /// sum of infinities decide.
        /// </para>
        /// <para>
        /// This is the only place the item's volume is derived. The gate and
        /// <see cref="IsBeyondAbsorbVolumeLimit"/> both call it, so the blocking set and
        /// the absorb gate cannot disagree about how big an item is.
        /// </para>
        /// </remarks>
        public static bool TryComputeItemVolume(Vector3 itemBounds, out float volumeWorldM3)
        {
            volumeWorldM3 = 0f;
            if (!KatamariMath.IsFiniteVector(itemBounds)) return false;
            if (itemBounds.x < 0f || itemBounds.y < 0f || itemBounds.z < 0f) return false;
            float volume = itemBounds.x * itemBounds.y * itemBounds.z;
            if (!KatamariMath.IsFiniteFloat(volume)) return false;
            volumeWorldM3 = volume;
            return true;
        }

        /// <summary>
        /// The size gate's comparison itself: an item volume strictly above the limit is
        /// too large. The one comparison in this project.
        /// </summary>
        /// <remarks>
        /// Inclusive at equality, matching the model's `&lt;=`: an item whose volume is
        /// exactly the limit is absorbable. No epsilon is added, so a boundary value that
        /// differs only in the last bit may still fall on the other side.
        /// </remarks>
        public static bool IsVolumeBeyondLimit(float itemVolumeWorldM3, float limitVolumeWorldM3)
        {
            return itemVolumeWorldM3 > limitVolumeWorldM3;
        }

        /// <summary>
        /// Resolves the size limit for one step: the ball's own volume divided by the
        /// absorb volume ratio, bounded above by the serialized safety ceiling.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Docs/OPEN_DECISIONS.md (l). The original's gate is `V_ball &gt;= ratio *
        /// V_item` (Docs/Research/KatamariBallRules/ABSORB_SIZE_RATIO.md), so the runtime
        /// compares volumes and the ball side of the comparison is
        /// `ballVolume = (pi/6) * D^3` with `D = ballSemanticDiameterWorldM`. That is the
        /// same semantic diameter the motor reports as the ball's size and derives from
        /// the confirmed volume (SizeModelMath.TryDerive), never the collision radius.
        /// `maxItemVolumeWorldM3` survives only as a ceiling: it is the serialized
        /// field's absolute bound in cubic metres, and the effective limit is
        /// `min(ceiling, ballVolume / ratio)`, so a config keeps one obviously-too-large
        /// item from being eaten by a tiny ball even if the ratio is mis-set low.
        /// </para>
        /// <para>
        /// The retired fields were a length ratio (`absorbSizeRatio`) and a length
        /// ceiling (`maxItemSizeWorldM = 8 m`); a length ceiling next to a volume gate
        /// would truncate at 8 m of longest edge, which is not the same statement as any
        /// volume bound. The defaults are now `absorbVolumeRatio = 10` and
        /// `maxItemVolumeWorldM3 = 512` (an 8 m cube).
        /// </para>
        /// <para>
        /// A ratio at or below zero, a non-finite ratio, diameter or ceiling, and a
        /// negative diameter are refused rather than clamped: ratio 0 would silently
        /// disable every absorb, and the round model's constructor rejects a
        /// non-positive ratio for the same reason (round_model.py:1405-1409).
        /// A zero diameter is accepted and yields a zero limit, which refuses every
        /// item that has any volume at all.
        /// </para>
        /// <para>
        /// The cube and the division are checked for finiteness, so an overflowing
        /// intermediate is a refusal instead of an infinity that would absorb everything.
        /// </para>
        /// </remarks>
        public static bool TryComputeAbsorbVolumeLimit(
            float ballSemanticDiameterWorldM,
            float absorbVolumeRatio,
            float maxItemVolumeWorldM3,
            out float limitWorldM3)
        {
            limitWorldM3 = 0f;
            if (!KatamariMath.IsFiniteFloat(ballSemanticDiameterWorldM)) return false;
            if (!KatamariMath.IsFiniteFloat(absorbVolumeRatio)) return false;
            if (!KatamariMath.IsFiniteFloat(maxItemVolumeWorldM3)) return false;
            if (ballSemanticDiameterWorldM < 0f) return false;
            if (absorbVolumeRatio <= 0f) return false;

            float ballVolumeWorldM3 = Mathf.PI / 6f * ballSemanticDiameterWorldM
                * ballSemanticDiameterWorldM * ballSemanticDiameterWorldM;
            if (!KatamariMath.IsFiniteFloat(ballVolumeWorldM3)) return false;
            float relative = ballVolumeWorldM3 / absorbVolumeRatio;
            if (!KatamariMath.IsFiniteFloat(relative)) return false;
            limitWorldM3 = relative < maxItemVolumeWorldM3 ? relative : maxItemVolumeWorldM3;
            return true;
        }

        /// <summary>
        /// 物件是否"太大"到吸不进，也就是是否应当挡路。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 这是 <see cref="EvaluateAbsorb"/> 尺寸门的同一个比较：取 bounds 的 AABB 体积，
        /// 只有严格更大才算超出，`volume == limit` 仍然可吸（门的 `&gt;` 与 round model
        /// 的 `&lt;=` 是同一边界）。宿主用它筛挡路集，与门共用
        /// <see cref="TryComputeItemVolume"/> 与 <see cref="IsVolumeBeyondLimit"/> 这一处
        /// 定义、不会漂移。
        /// </para>
        /// <para>
        /// 非有限 bounds、负尺寸、体积溢出和非有限 limit 一律返回 true。门上这些输入的
        /// 结论是 <see cref="AbsorbOutcome.RejectInvalidInput"/> 而不是 Absorb，而 NaN 与
        /// 任何数比较都是假：若在这里返回 false，坏数据的道具会既不挡也吸不动，正是挡路
        /// 方案要修的那个症状。limit 为负是合法输入（直径 0 的球给 0，再小就是负数），
        /// 此时任何非负体积都超出。
        /// </para>
        /// </remarks>
        public static bool IsBeyondAbsorbVolumeLimit(Vector3 itemBounds, float limitVolumeWorldM3)
        {
            if (!KatamariMath.IsFiniteFloat(limitVolumeWorldM3)) return true;
            float itemVolumeWorldM3;
            if (!TryComputeItemVolume(itemBounds, out itemVolumeWorldM3)) return true;
            return IsVolumeBeyondLimit(itemVolumeWorldM3, limitVolumeWorldM3);
        }

        /// <summary>
        /// <see cref="EvaluateAbsorb"/> with the volume limit resolved from the ball's own
        /// size (relative gate, Docs/OPEN_DECISIONS.md (l)).
        /// </summary>
        /// <remarks>
        /// The gate order is unchanged: an invalid ratio, diameter, ceiling or geometry
        /// is RejectInvalidInput before any gate runs, then not absorbable, kind, size,
        /// growth cap and reach, first failure wins. The item-too-large outcome keeps its
        /// code (<see cref="AbsorbOutcome.RejectItemTooLarge"/>); what it is compared
        /// against is now a volume. The comparison is inclusive at equality: an item whose
        /// AABB volume equals `min(ceiling, ballVolume / ratio)` is absorbable, matching
        /// the model's `&lt;=` (this port adds no epsilon, so a boundary value that differs
        /// only in the last bit may still fall on the other side).
        /// </remarks>
        public static AbsorbOutcome EvaluateAbsorbWithBallSize(
            bool itemAbsorbable,
            string itemKind,
            string[] allowedKinds,
            int allowedKindCount,
            Vector3 itemBounds,
            Vector3 itemPosition,
            Vector3 ballPosition,
            float collisionRadiusWorldM,
            float contactRadiusWorldM,
            float maxItemVolumeWorldM3,
            float maxGrowthRadiusWorldM,
            float ballSemanticDiameterWorldM,
            float absorbVolumeRatio)
        {
            float limitWorldM3;
            if (!TryComputeAbsorbVolumeLimit(ballSemanticDiameterWorldM, absorbVolumeRatio,
                maxItemVolumeWorldM3, out limitWorldM3))
                return AbsorbOutcome.RejectInvalidInput;
            return EvaluateAbsorb(itemAbsorbable, itemKind, allowedKinds, allowedKindCount,
                itemBounds, itemPosition, ballPosition, collisionRadiusWorldM,
                contactRadiusWorldM, limitWorldM3, maxGrowthRadiusWorldM);
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
        /// <see cref="EvaluateAbsorb"/> plus the ledger's gain precondition.
        /// </summary>
        /// <remarks>
        /// An absorb whose value truncates to zero units would pass the geometry gate,
        /// take a claim, resolve to no commit row, and disappear without a trace: no
        /// score, no size change, no reject, no event. The rule "an item is worth at
        /// least one unit" belongs to the data, so a violation is reported as
        /// <see cref="AbsorbOutcome.RejectNoGain"/> instead of being swallowed.
        /// The geometry verdict is returned unchanged when it is already a refusal, so a
        /// gain problem never masks a geometry refusal and a geometry refusal is never
        /// reported as a gain problem. `maxItemVolumeWorldM3` here is the
        /// already-resolved volume limit, exactly as in <see cref="EvaluateAbsorb"/>.
        /// </remarks>
        public static AbsorbOutcome EvaluateAbsorbWithValue(
            bool itemAbsorbable,
            string itemKind,
            string[] allowedKinds,
            int allowedKindCount,
            Vector3 itemBounds,
            Vector3 itemPosition,
            Vector3 ballPosition,
            float collisionRadiusWorldM,
            float contactRadiusWorldM,
            float maxItemVolumeWorldM3,
            float maxGrowthRadiusWorldM,
            float itemValue)
        {
            AbsorbOutcome geometry = EvaluateAbsorb(itemAbsorbable, itemKind, allowedKinds,
                allowedKindCount, itemBounds, itemPosition, ballPosition, collisionRadiusWorldM,
                contactRadiusWorldM, maxItemVolumeWorldM3, maxGrowthRadiusWorldM);
            return ApplyValueGate(geometry, itemValue);
        }

        /// <summary>
        /// <see cref="EvaluateAbsorbWithBallSize"/> plus the ledger's gain precondition.
        /// </summary>
        /// <remarks>
        /// The gameplay entry point: the volume limit comes from the ball's current
        /// semantic diameter (Docs/OPEN_DECISIONS.md (l)) and the gain check runs only after the
        /// geometry gate has already returned Absorb, so the two concerns keep the order
        /// pinned by <see cref="EvaluateAbsorbWithValue"/>.
        /// </remarks>
        public static AbsorbOutcome EvaluateAbsorbWithValueAndBallSize(
            bool itemAbsorbable,
            string itemKind,
            string[] allowedKinds,
            int allowedKindCount,
            Vector3 itemBounds,
            Vector3 itemPosition,
            Vector3 ballPosition,
            float collisionRadiusWorldM,
            float contactRadiusWorldM,
            float maxItemVolumeWorldM3,
            float maxGrowthRadiusWorldM,
            float ballSemanticDiameterWorldM,
            float absorbVolumeRatio,
            float itemValue)
        {
            AbsorbOutcome geometry = EvaluateAbsorbWithBallSize(itemAbsorbable, itemKind,
                allowedKinds, allowedKindCount, itemBounds, itemPosition, ballPosition,
                collisionRadiusWorldM, contactRadiusWorldM, maxItemVolumeWorldM3,
                maxGrowthRadiusWorldM, ballSemanticDiameterWorldM, absorbVolumeRatio);
            return ApplyValueGate(geometry, itemValue);
        }

        // The gain precondition both value entry points share, written once so the two
        // cannot drift about which refusal wins.
        private static AbsorbOutcome ApplyValueGate(AbsorbOutcome geometry, float itemValue)
        {
            if (geometry != AbsorbOutcome.Absorb) return geometry;
            int gain;
            if (!TryAbsorbValueToLedgerGain(itemValue, out gain)) return AbsorbOutcome.RejectNoGain;
            if (gain <= 0) return AbsorbOutcome.RejectNoGain;
            return AbsorbOutcome.Absorb;
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
