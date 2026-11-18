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
    /// item's size, its world rotation and both world positions.
    /// </para>
    /// <para>
    /// Units and quantities. Lengths are metres in the project's single world space
    /// (+Y up); the volumes this layer compares are cubic metres (the `WorldM3` suffix).
    /// The item's extent is its `itemSize`: its OWN three full edge lengths (the bounding box
    /// of its model along its own axes, the original's per-type `a, b, c`), not half extents.
    /// It does not change when the item is turned, and it is the only item geometry this
    /// layer reads: the size gate multiplies its three edges, so it compares the item's own
    /// box volume, and the contact test places that same box in the world with the item's
    /// rotation. The axis-aligned box of a TURNED item is larger than the item; it is never
    /// an input here. Values are in caller-defined value units; the
    /// model's item value is a float, and the ledger's gain is an integer of the same unit
    /// (see <see cref="TryAbsorbValueToLedgerGain"/>).
    /// </para>
    /// <para>
    /// The size gate is the ORIGINAL's, in the original's own quantities (owner decision
    /// 2026-11-16, Docs/OPEN_DECISIONS.md; PARITY_AUDIT G07/G08/G13). An item's volume is
    /// `V = k * a*b*c`, where `k` is the per-item factor the original authors per type
    /// (default 0.70 for 70.7% of its 1,425 types, thin sheets 8-16, fluffy 0.05-0.07).
    /// Its required ball diameter is `Req = cbrt(60 * V / pi)` - 60 = 6 * 10, the "ball is
    /// ten times the item's volume" rule - FLOORED to whole millimetres, and the item is
    /// absorbable iff the ball's whole-millimetre diameter is at least Req:
    /// `floor(1000*D) >= floor(1000*cbrt(60*k*a*b*c/pi))`.
    /// <see cref="TryComputePickupReqDiameterMm"/> and
    /// <see cref="TryFloorMillimetres"/> are the two halves of that rule;
    /// <see cref="IsBeyondAbsorbLimit"/> is its single predicate - the gate and every
    /// host-side blocking/support classification ask that one function. There is no
    /// serialized ratio and no serialized per-item volume ceiling any more; the retired
    /// fields are described where they used to live (KatamariBallMotor's pickup inputs).
    /// </para>
    /// <para>
    /// Why volume and not the longest edge. The original's gate is
    /// `V_ball &gt;= 10 * V_item` with `V_item = k*a*b*c` (Docs/Research/KatamariBallRules/
    /// ABSORB_SIZE_RATIO.md; PARITY_AUDIT G07/G08/G13). A linear ratio cannot express that
    /// quantity: edges that differ by 6x differ in volume by 216x, so one 0.30 m cube
    /// weighs as much as 216 of the 0.05 m ones, and a slender prop (a bat, a pencil)
    /// passes a volume gate that a longest-edge gate refuses. The AABB volume is the
    /// original's own base quantity (it counts empty space, which is exactly why the
    /// authors hand-tune `k` down to 0.3-0.7 for hollow furniture), so a sphere-like prop
    /// is over-counted by `6/pi` (~1.91x) and needs a ball ~1.24x larger - faithful to the
    /// original, not a bug.
    /// </para>
    /// <para>
    /// Why the comparison is in whole millimetres. The original quantizes the REQUIRED
    /// SIZE, not the volume (`Req Pickup Size` is an integer millimetre; 1,449/1,450 data
    /// rows are self-consistent within 1 mm), so the gate the player feels is
    /// "my displayed size is at least that many mm". Comparing volumes in float would
    /// reintroduce the rounding the original does not have, and would make the boundary
    /// depend on the order of two multiplications. <see cref="MmFloorEpsilon"/> is 1e-3 mm
    /// (one micrometre): float32's ulp at a 2.2 m diameter is 2.4e-4 mm, so the guard is
    /// large enough to repair the arithmetic and 1/1000 of the quantum, i.e. far below the
    /// 0.1 mm resolution of an authored AABB. The Python twin
    /// (placement_core.MM_EPSILON) uses the same value, so the two gates cannot disagree
    /// on an item that sits exactly on a boundary.
    /// </para>
    /// <para>
    /// Two size quantities are still not interchangeable: the round model compares the
    /// item's declared `pickup_diameter_m` (a length), while this layer compares the
    /// item size's box volume, so the two produce the same verdict only when the author
    /// data fills the size consistently with the declared pickup diameter.
    /// </para>
    /// <para>
    /// The contact comparison uses the *collision radius* and never the semantic
    /// diameter. Those are different quantities in this project (see
    /// <c>SizeModelMath.TryDerive</c>): the semantic diameter is the narrative size a
    /// player reads, it may differ from the physical metres. The contact test has no
    /// diameter parameter, so a caller cannot pass a diameter as if it were a radius. The
    /// size gate is the one place a semantic diameter appears, and it is consumed there as
    /// a DIAMETER (converted to whole millimetres inside <see cref="TryFloorMillimetres"/>)
    /// and never added to the contact limit. The box volume used by the size gate and the
    /// box distance used by the contact test are different quantities read from the same
    /// `itemSize`: a slender prop can reach far and weigh little.
    /// </para>
    /// <para>
    /// Contact test (the original's: the item's collision model touches the ball's).
    /// `boxDistance &lt;= collisionRadius + contactRadius`, where `boxDistance` is the
    /// shortest distance from the ball's centre to the item's oriented box (its own size,
    /// placed at its position with its world rotation), zero when the centre is inside the
    /// box. So an item is taken when its surface touches the ball, not when a bounding
    /// sphere around it does; a long thin item approached from the side is taken when its
    /// face reaches the ball. It models no occlusion, no velocity plausibility and no item
    /// state machine. Comparisons keep the model's strictness: the size gate accepts
    /// `ballMm == ReqMm` and rejects one millimetre less, the growth cap rejects a radius
    /// that has already reached the cap, and a distance exactly equal to the limit is
    /// contact. The box is assumed centred on the item's position (the prefab root sits at
    /// the model's centre; the sync measures only sizes).
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
        /// The per-item volume factor's default: the original's 0.70
        /// (`Volume = k * bbox`, 1,008 of 1,425 types).
        /// </summary>
        /// <remarks>
        /// A level's `defs[].volumeK` (or a prop's override) is authoritative; this value is
        /// what the authoring path writes when a type declares nothing and what a host
        /// falls back to for a scene synced before the column existed. It is never written
        /// into `placement.json`, so an existing file stays byte-identical.
        /// </remarks>
        public const float DefaultVolumeK = 0.70f;

        /// <summary>
        /// The original's per-item growth penalty: one absorb credits
        /// <c>penalty * k * a*b*c</c> to the ledger, and 0.8 covers 1,198 of 1,436 types
        /// (PARITY_AUDIT G06/F5). A per-item override travels in placement data
        /// (<c>volumePenalty</c>) and is priced into the host's growth column at import, so
        /// the runtime normally reads the finished number; this constant is the default the
        /// importer, the editor rule and the host fallback all agree on.
        /// </summary>
        public const float DefaultVolumePenalty = 0.8f;

        /// <summary>Smallest legal penalty: above 0, at most 1 (never recover more than the item).</summary>
        public const float MaxVolumePenalty = 1.0f;

        /// <summary>
        /// The upper bound of the one authoring rule for `k`: `0 &lt; k &lt;= 32`.
        /// </summary>
        /// <remarks>
        /// The original's hand-tuned factors reach 16.8 (thin sheets), so 32 is a ceiling
        /// with headroom, not a tuning knob: it catches the typo `70` for `0.70`, which
        /// would otherwise make a chair absorbable by a pinhead. Zero or negative is refused
        /// because it would price every item at zero volume.
        /// </remarks>
        public const float MaxVolumeK = 32f;

        /// <summary>
        /// The original's volume rule constant: the ball must be TEN times the item's
        /// volume, i.e. `(pi/6) * D^3 = ratio * V`, and `Req = cbrt(6 * ratio * V / pi)`.
        /// </summary>
        /// <remarks>
        /// 10 is the original's number (PARITY_AUDIT G07; raw_growth_mechanism §2). It is a
        /// constant, not a serialized field, because the owner decision of 2026-11-16 is
        /// "the absorb gate follows the original numbers exactly": a per-scene ratio would
        /// let a config silently stop being the original's gate.
        /// </remarks>
        public const float AbsorbVolumeRatio = 10f;

        /// <summary>
        /// A numerical sanity ceiling for one item's ORIGINAL volume, 1000 m3. Not a gate.
        /// </summary>
        /// <remarks>
        /// The retired field was `maxItemVolumeWorldM3 = 512` (an 8 m cube) used as
        /// `min(ceiling, ballVolume / ratio)` - a hard cap coupled to the ball's own size,
        /// which is exactly the coupling the original does not have. With the gate compared
        /// in whole millimetres the cap is unnecessary: an item bigger than the ball simply
        /// has a larger Req. 1000 m3 is ~1,792x the largest item the 2.20 m ball cap can
        /// absorb (`pi/6 * 2.2^3 / 10 = 0.558 m3`) and above any single prop in the content
        /// (the biggest level totals ~3,022 m3 ACROSS all props), so only a corrupt bounds
        /// triple can reach it; the constant keeps such a product from producing an
        /// infinity or a nonsense Req that a reader would trust.
        /// </remarks>
        /// <summary>
        /// Numerical sanity bound on one item's AABB volume, NOT a gate. With the global
        /// 30 m ball cap the largest legal item is <c>V_cap / 10 = 1,413.7 m^3</c>, so the
        /// old 1,000 m^3 sat BELOW a legal item; this is that bound with a 10x margin
        /// (14,137 m^3). A bounds triple that reaches it is broken data, and the verdict is
        /// "too large / blocking", never "infinite".
        /// </summary>
        public const float MaxItemVolumeSanityWorldM3 = 14137f;

        /// <summary>
        /// The boundary guard for the whole-millimetre floor: one micrometre, in mm.
        /// </summary>
        /// <remarks>
        /// Added before `floor`, so an exact millimetre boundary that float error places a
        /// hair below it still lands on the integer. 1e-3 mm is ~4x float32's ulp at a 2.2 m
        /// diameter, and 1/1000 of the quantum - far below the 0.1 mm resolution of authored
        /// AABB data - so it repairs arithmetic without promoting a genuinely smaller item.
        /// `placement_core.MM_EPSILON` is the same number, which keeps the data gate and the
        /// runtime gate identical on a knife-edge.
        /// </remarks>
        public const float MmFloorEpsilon = 0.001f;

        /// <summary>
        /// Decides whether one item may be absorbed by the ball right now.
        /// </summary>
        /// <remarks>
        /// The gates run in the model's order, so the returned value is the model's first
        /// failing condition: not absorbable; kind outside the allowed set; required
        /// diameter above the ball's whole-millimetre diameter; collision radius already at
        /// or above maxGrowthRadiusWorldM; contact distance strictly greater than the reach.
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
        /// The size gate is <see cref="IsBeyondAbsorbLimit"/> and nothing else: the item's
        /// `k * a*b*c` priced at `k`, the ball's semantic diameter, both floored to whole
        /// millimetres. `a, b, c` are the item's own size (`itemSize`), so the verdict does
        /// not depend on how the item is turned. `itemVolumeK` is the per-item factor (the
        /// original's `Volume = k * bbox`; 0.70 is the default the authoring path fills in,
        /// and the range 0 &lt; k &lt;= <see cref="MaxVolumeK"/> is validated here as an
        /// input).
        /// </para>
        /// <para>
        /// The contact geometry (the box distance and the limit it is compared with) is
        /// <see cref="TryComputeContact"/> over the same `itemSize` and the item's rotation;
        /// this method only compares the box distance against the limit it returns, so a
        /// caller that needs the numbers the gate used asks for the same function instead
        /// of re-deriving them.
        /// </para>
        /// </remarks>
        public static AbsorbOutcome EvaluateAbsorb(
            bool itemAbsorbable,
            string itemKind,
            string[] allowedKinds,
            int allowedKindCount,
            Vector3 itemSize,
            Quaternion itemRotation,
            Vector3 itemPosition,
            Vector3 ballPosition,
            float collisionRadiusWorldM,
            float contactRadiusWorldM,
            float itemVolumeK,
            float ballSemanticDiameterWorldM,
            float maxGrowthRadiusWorldM)
        {
            if (itemKind == null || allowedKinds == null) return AbsorbOutcome.RejectInvalidInput;
            if (allowedKindCount < 0 || allowedKindCount > allowedKinds.Length)
                return AbsorbOutcome.RejectInvalidInput;
            if (!IsUsableRotation(itemRotation)) return AbsorbOutcome.RejectInvalidInput;
            if (!KatamariMath.IsFiniteVector(itemSize) ||
                !KatamariMath.IsFiniteVector(itemPosition) ||
                !KatamariMath.IsFiniteVector(ballPosition))
                return AbsorbOutcome.RejectInvalidInput;
            if (!KatamariMath.IsFiniteFloat(collisionRadiusWorldM) ||
                !KatamariMath.IsFiniteFloat(contactRadiusWorldM) ||
                !KatamariMath.IsFiniteFloat(itemVolumeK) ||
                !KatamariMath.IsFiniteFloat(ballSemanticDiameterWorldM) ||
                !KatamariMath.IsFiniteFloat(maxGrowthRadiusWorldM))
                return AbsorbOutcome.RejectInvalidInput;
            if (itemSize.x < 0f || itemSize.y < 0f || itemSize.z < 0f)
                return AbsorbOutcome.RejectInvalidInput;
            if (collisionRadiusWorldM < 0f) return AbsorbOutcome.RejectInvalidInput;
            if (itemVolumeK <= 0f || itemVolumeK > MaxVolumeK)
                return AbsorbOutcome.RejectInvalidInput;
            if (ballSemanticDiameterWorldM < 0f) return AbsorbOutcome.RejectInvalidInput;

            // An unrepresentable product is an invalid input, not a large item: 1e20^3 is
            // infinity, and letting the gate below answer "too large" would describe broken
            // size data as a legitimate wall.
            float itemVolumeWorldM3;
            if (!TryComputeOriginalItemVolume(itemSize, itemVolumeK, out itemVolumeWorldM3))
                return AbsorbOutcome.RejectInvalidInput;

            // One size gate: the gate and every host-side wall/floor classification call
            // this same predicate, so "beyond the absorb limit" cannot mean two things.
            bool beyondLimit = IsBeyondAbsorbLimit(itemSize, itemVolumeK,
                ballSemanticDiameterWorldM);

            if (!itemAbsorbable) return AbsorbOutcome.RejectNotAbsorbable;
            if (!IsKindAllowed(allowedKinds, allowedKindCount, itemKind))
                return AbsorbOutcome.RejectKindNotAllowed;

            // Invalid geometry already returned above; what is left here is a legal item
            // that is either too large for this ball or beyond the sanity ceiling.
            if (beyondLimit) return AbsorbOutcome.RejectItemTooLarge;
            if (collisionRadiusWorldM >= maxGrowthRadiusWorldM)
                return AbsorbOutcome.RejectBallAtGrowthCap;

            float boxDistance;
            float contactLimit;
            if (!TryComputeContact(itemSize, itemRotation, itemPosition, ballPosition,
                collisionRadiusWorldM, contactRadiusWorldM, out boxDistance, out contactLimit))
                return AbsorbOutcome.RejectInvalidInput;
            return boxDistance <= contactLimit ? AbsorbOutcome.Absorb
                : AbsorbOutcome.RejectOutOfReach;
        }

        /// <summary>
        /// The contact geometry both the gate and every report of the gate's threshold use:
        /// the distance from the ball's centre to the item's box, and the limit it is
        /// compared with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// `boxDistanceM` is <see cref="TryComputeBoxDistance"/> of the ball's centre to the
        /// item's oriented box; `contactLimitM = collisionRadiusWorldM + contactRadiusWorldM`.
        /// Both are metres and are only compared: no scaling, no falloff, no epsilon. This is
        /// the only place the contact rule exists: <see cref="EvaluateAbsorb"/> decides with
        /// it, and a caller that reports the numbers it refused with (the assembly layer's
        /// rejection diagnostics and trace) asks for the same pair instead of re-deriving a
        /// second copy that can drift.
        /// </para>
        /// <para>
        /// False means the inputs are not a legal item geometry at all: a non-finite size,
        /// position, radius or contact margin, an unusable rotation, a negative size or
        /// collision radius (all of which the gate reports as invalid input), or a
        /// non-representable intermediate. Both out values are then zero. Which side of the
        /// limit a candidate falls on is NOT decided here; the comparison stays the gate's.
        /// </para>
        /// </remarks>
        public static bool TryComputeContact(Vector3 itemSize, Quaternion itemRotation,
            Vector3 itemPosition, Vector3 ballPosition, float collisionRadiusWorldM,
            float contactRadiusWorldM, out float boxDistanceM, out float contactLimitM)
        {
            boxDistanceM = 0f;
            contactLimitM = 0f;
            if (!KatamariMath.IsFiniteFloat(collisionRadiusWorldM)) return false;
            if (!KatamariMath.IsFiniteFloat(contactRadiusWorldM)) return false;
            if (collisionRadiusWorldM < 0f) return false;
            float limit = collisionRadiusWorldM + contactRadiusWorldM;
            if (!KatamariMath.IsFiniteFloat(limit)) return false;
            float distance;
            if (!TryComputeBoxDistance(itemSize, itemRotation, itemPosition, ballPosition,
                out distance)) return false;
            boxDistanceM = distance;
            contactLimitM = limit;
            return true;
        }

        /// <summary>
        /// The shortest distance from a world point to an oriented box, 0 when the point is
        /// inside or on it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The box has full edge lengths `boxSize` along its own axes, is centred on
        /// `boxCentre`, and is turned by `boxRotation` (a non-unit quaternion is normalised;
        /// the zero quaternion is not a rotation). The point is taken into the box's frame
        /// with the inverse rotation, and the distance is the length of what sticks out of
        /// the box on each axis: `sqrt(sum(max(|p_i| - size_i/2, 0)^2))`. That is exact in
        /// the face region (one axis out), the edge region (two) and the corner region
        /// (three). A zero edge is a legal flat or line box.
        /// </para>
        /// <para>
        /// Pure arithmetic over floats - no Quaternion operator and no allocation - so the
        /// same code runs in the Udon VM and in the offline test runner. False for a
        /// non-finite input, a negative size, an unusable rotation or an unrepresentable
        /// result, with <paramref name="distanceM"/> zero.
        /// </para>
        /// </remarks>
        public static bool TryComputeBoxDistance(Vector3 boxSize, Quaternion boxRotation,
            Vector3 boxCentre, Vector3 point, out float distanceM)
        {
            distanceM = 0f;
            if (!KatamariMath.IsFiniteVector(boxSize)) return false;
            if (!KatamariMath.IsFiniteVector(boxCentre)) return false;
            if (!KatamariMath.IsFiniteVector(point)) return false;
            if (boxSize.x < 0f || boxSize.y < 0f || boxSize.z < 0f) return false;
            if (!IsUsableRotation(boxRotation)) return false;

            float vx = point.x - boxCentre.x;
            float vy = point.y - boxCentre.y;
            float vz = point.z - boxCentre.z;
            if (!KatamariMath.IsFiniteFloat(vx) || !KatamariMath.IsFiniteFloat(vy) ||
                !KatamariMath.IsFiniteFloat(vz)) return false;

            // Inverse of the unit rotation (its conjugate): u = -xyz, then
            // v' = v + w*t + cross(u, t) with t = 2*cross(u, v).
            float norm = Mathf.Sqrt(boxRotation.x * boxRotation.x + boxRotation.y * boxRotation.y
                + boxRotation.z * boxRotation.z + boxRotation.w * boxRotation.w);
            float ux = -boxRotation.x / norm;
            float uy = -boxRotation.y / norm;
            float uz = -boxRotation.z / norm;
            float w = boxRotation.w / norm;
            float tx = 2f * (uy * vz - uz * vy);
            float ty = 2f * (uz * vx - ux * vz);
            float tz = 2f * (ux * vy - uy * vx);
            float px = vx + w * tx + (uy * tz - uz * ty);
            float py = vy + w * ty + (uz * tx - ux * tz);
            float pz = vz + w * tz + (ux * ty - uy * tx);

            float ox = Mathf.Max(Mathf.Abs(px) - boxSize.x * 0.5f, 0f);
            float oy = Mathf.Max(Mathf.Abs(py) - boxSize.y * 0.5f, 0f);
            float oz = Mathf.Max(Mathf.Abs(pz) - boxSize.z * 0.5f, 0f);
            float squared = ox * ox + oy * oy + oz * oz;
            if (!KatamariMath.IsFiniteFloat(squared)) return false;
            float distance = Mathf.Sqrt(squared);
            if (!KatamariMath.IsFiniteFloat(distance)) return false;
            distanceM = distance;
            return true;
        }

        // A rotation the contact maths may normalise: finite components with a non-zero
        // length. The zero quaternion has no direction, so it is invalid input, not identity.
        private static bool IsUsableRotation(Quaternion rotation)
        {
            if (!KatamariMath.IsFiniteQuaternion(rotation)) return false;
            float lengthSquared = rotation.x * rotation.x + rotation.y * rotation.y
                + rotation.z * rotation.z + rotation.w * rotation.w;
            if (!KatamariMath.IsFiniteFloat(lengthSquared)) return false;
            return lengthSquared > 1e-12f;
        }

        /// <summary>
        /// The item's box volume, `a * b * c`, the quantity the size gate compares.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cubic metres, from the item's own full edge lengths. `false` for a non-finite or
        /// negative side, and for a product that overflows float: an unrepresentable
        /// volume is an invalid input, not a large item, so the gate reports
        /// <see cref="AbsorbOutcome.RejectInvalidInput"/> instead of silently letting a
        /// sum of infinities decide.
        /// </para>
        /// <para>
        /// This is the only place the raw box volume is derived. The gate and every
        /// blocking/support classification reach it through one path - the raw product
        /// here, multiplied by the item's `k` in
        /// <see cref="TryComputeOriginalItemVolume"/> - so the blocking set and the absorb
        /// gate cannot disagree about how big an item is.
        /// </para>
        /// </remarks>
        public static bool TryComputeItemVolume(Vector3 itemSize, out float volumeWorldM3)
        {
            volumeWorldM3 = 0f;
            if (!KatamariMath.IsFiniteVector(itemSize)) return false;
            if (itemSize.x < 0f || itemSize.y < 0f || itemSize.z < 0f) return false;
            float volume = itemSize.x * itemSize.y * itemSize.z;
            if (!KatamariMath.IsFiniteFloat(volume)) return false;
            volumeWorldM3 = volume;
            return true;
        }

        /// <summary>
        /// The original's item volume: `V = k * a * b * c`.
        /// </summary>
        /// <remarks>
        /// <para>
        /// `k` is the per-item factor the original stores as `Volume / bbox`: 0.70 for
        /// 1,008 of its 1,425 types (70.7%), hand-tuned elsewhere - thin sheets 8-16,
        /// fluffy things 0.05-0.07, hollow furniture 0.3-0.5 (PARITY_AUDIT G13,
        /// `meta/solid_volume`, FIXED_POINT_VOLUME_IMPACT §2 O3). The authoring path fills
        /// <see cref="DefaultVolumeK"/> when a type declares nothing, so a scene written
        /// before this field existed plays at 0.70 without being re-synced.
        /// </para>
        /// <para>
        /// `false` for a non-finite or non-positive `k`, for `k` above
        /// <see cref="MaxVolumeK"/>, and for any size the raw product refuses. The range
        /// check is deliberate rather than a clamp: `k = 0` would price every item at zero
        /// volume, and a typo like 70 would make a chair absorbable by a pinhead.
        /// </para>
        /// </remarks>
        public static bool TryComputeOriginalItemVolume(Vector3 itemSize, float volumeK,
            out float volumeWorldM3)
        {
            volumeWorldM3 = 0f;
            if (!KatamariMath.IsFiniteFloat(volumeK)) return false;
            if (volumeK <= 0f || volumeK > MaxVolumeK) return false;
            float boxVolumeWorldM3;
            if (!TryComputeItemVolume(itemSize, out boxVolumeWorldM3)) return false;
            float volume = volumeK * boxVolumeWorldM3;
            if (!KatamariMath.IsFiniteFloat(volume)) return false;
            volumeWorldM3 = volume;
            return true;
        }

        /// <summary>
        /// Whole millimetres of a length, FLOORED with <see cref="MmFloorEpsilon"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The original quantizes the required size, not the volume: its `Req Pickup Size`
        /// is an integer millimetre, and the player-facing rule is "my displayed size (in
        /// whole mm) is at least that many mm". This is the only place a length becomes
        /// millimetres, used for both sides of the comparison.
        /// </para>
        /// <para>
        /// The epsilon is added BEFORE the floor so an exact boundary that float error
        /// places a hair below it is repaired: `floor(1000*0.3 + 1e-3)` is 300 even when
        /// the product lands at 299.99997. It is 1e-3 mm (one micrometre), about four times
        /// float32's ulp at a 2.2 m diameter (2.4e-4 mm) and a thousandth of the quantum,
        /// i.e. far below the 0.1 mm resolution of an authored AABB - it cannot promote an
        /// item that is genuinely smaller. It is also the exact value of
        /// `MM_EPSILON` in `Tools/levelbuild/placement_core.py`, so the data gate and this
        /// one agree on a knife-edge item.
        /// </para>
        /// <para>
        /// `false` for a non-finite or negative length, or a product that overflows float:
        /// those are invalid inputs, not "zero millimetres".
        /// </para>
        /// </remarks>
        public static bool TryFloorMillimetres(float metres, out float millimetres)
        {
            millimetres = 0f;
            if (!KatamariMath.IsFiniteFloat(metres)) return false;
            if (metres < 0f) return false;
            float scaled = metres * 1000f;
            if (!KatamariMath.IsFiniteFloat(scaled)) return false;
            millimetres = Mathf.Floor(scaled + MmFloorEpsilon);
            return true;
        }

        /// <summary>
        /// The original's `Req Pickup Size`: `floor(1000 * cbrt(60 * k * a*b*c / pi))` mm.
        /// </summary>
        /// <remarks>
        /// <para>
        /// `60 = 6 * 10`: the ball's own volume `(pi/6) * D^3` must be ten times the item's
        /// `k * a*b*c`, solved for D. The result is the FLOORED whole-millimetre diameter,
        /// which is what the original stores and compares - so the accepted ball is up to
        /// one millimetre smaller than the exact ten-times rule ("the required pickup size
        /// is generally a little bit less than 10 times bigger", raw_growth_mechanism §2;
        /// the Ant is the extreme case at ~8.8x).
        /// </para>
        /// <para>
        /// `false` for an illegal size or `k` (see
        /// <see cref="TryComputeOriginalItemVolume"/>) and for a volume above
        /// <see cref="MaxItemVolumeSanityWorldM3"/>. That ceiling is a numerical sanity
        /// bound, not a gameplay gate: at the 2.20 m ball cap the gate allows at most
        /// 0.558 m3 (pi/6 * 2.2^3 / 10), so 1000 m3 is ~1,792x the largest item a legal
        /// ball can absorb; it exists so a corrupt bounds triple (a 100 m "pebble") cannot
        /// produce an infinity or a nonsense Req that a reader would trust. A caller that
        /// sees `false` here must treat the item as NOT absorbable (blocking), never as
        /// absorbable.
        /// </para>
        /// </remarks>
        public static bool TryComputePickupReqDiameterMm(Vector3 itemSize, float volumeK,
            out float reqMm)
        {
            reqMm = 0f;
            float volumeWorldM3;
            if (!TryComputeOriginalItemVolume(itemSize, volumeK, out volumeWorldM3)) return false;
            if (volumeWorldM3 > MaxItemVolumeSanityWorldM3) return false;
            float exactDiameterM = Mathf.Pow(60f * volumeWorldM3 / Mathf.PI, 1f / 3f);
            if (!KatamariMath.IsFiniteFloat(exactDiameterM)) return false;
            return TryFloorMillimetres(exactDiameterM, out reqMm);
        }

        /// <summary>
        /// 物件是否"太大"到吸不进，也就是是否应当挡路。The ONE size predicate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// `reqMm &gt; ballMm` is exactly the refusal
        /// <see cref="EvaluateAbsorb"/> reports as
        /// <see cref="AbsorbOutcome.RejectItemTooLarge"/>: the item's
        /// `floor(1000*cbrt(60*k*a*b*c/pi))` against the ball's `floor(1000*D)`. The gate
        /// and the host-side blocking/support classification both call this function, so
        /// "beyond the absorb limit" has one definition. Equality is absorbable: an item
        /// whose Req is exactly the ball's whole-millimetre diameter is food.
        /// </para>
        /// <para>
        /// `true` (beyond) for every input the gate refuses as invalid: a non-finite size,
        /// negative extents, an overflowing product, illegal `k`, a non-finite or negative
        /// diameter, and a volume above the sanity ceiling. The gate reports those as
        /// <see cref="AbsorbOutcome.RejectInvalidInput"/> (or RejectItemTooLarge for the
        /// ceiling) rather than Absorb, and a NaN compares false with everything - returning
        /// false here would leave bad-data props neither blocking nor absorbable, which is
        /// the symptom the blocking feature exists to remove.
        /// </para>
        /// </remarks>
        public static bool IsBeyondAbsorbLimit(Vector3 itemSize, float volumeK,
            float ballSemanticDiameterWorldM)
        {
            float reqMm;
            if (!TryComputePickupReqDiameterMm(itemSize, volumeK, out reqMm)) return true;
            float ballMm;
            if (!TryFloorMillimetres(ballSemanticDiameterWorldM, out ballMm)) return true;
            return reqMm > ballMm;
        }

        /// <summary>The "no valid requirement" marker of <see cref="ResolveRequirementMmOrInvalid"/>.</summary>
        public const float RequirementInvalid = -1f;

        /// <summary>
        /// The item's Req in whole mm, or <see cref="RequirementInvalid"/> when
        /// <see cref="TryComputePickupReqDiameterMm"/> refuses the size/k. A host that tests
        /// the same item every scan resolves this once (it needs a cube root) and then asks
        /// <see cref="IsBeyondCachedRequirement"/>.
        /// </summary>
        public static float ResolveRequirementMmOrInvalid(Vector3 itemSize, float volumeK)
        {
            float reqMm;
            if (!TryComputePickupReqDiameterMm(itemSize, volumeK, out reqMm)) return RequirementInvalid;
            return reqMm;
        }

        /// <summary>
        /// <see cref="IsBeyondAbsorbLimit"/> from a cached requirement and the ball's already
        /// floored whole-mm diameter (<paramref name="ballMmValid"/> = the result of
        /// <see cref="TryFloorMillimetres"/>). Same verdict for every input: an invalid
        /// requirement or an invalid ball diameter is "beyond"; otherwise Req &gt; ballMm.
        /// </summary>
        public static bool IsBeyondCachedRequirement(float requirementMmOrInvalid,
            bool ballMmValid, float ballMm)
        {
            if (!ballMmValid) return true;
            if (requirementMmOrInvalid < 0f) return true;
            return requirementMmOrInvalid > ballMm;
        }

        /// <summary>
        /// Everything the size gate accepts at this ball: the item's Req in whole mm and the
        /// ball's whole-mm diameter, resolved once for a caller that reports them.
        /// </summary>
        /// <remarks>
        /// Read-only diagnostics/probe partner of
        /// <see cref="IsBeyondAbsorbLimit"/>: it answers "what numbers did the gate compare"
        /// with the same two functions the gate uses, so a HUD or a test never re-derives a
        /// second copy. `false` means one of the inputs is illegal, in which case the
        /// predicate above answers "beyond".
        /// </remarks>
        public static bool TryResolveAbsorbGate(Vector3 itemSize, float volumeK,
            float ballSemanticDiameterWorldM, out float reqMm, out float ballMm, out bool within)
        {
            reqMm = 0f;
            ballMm = 0f;
            within = false;
            if (!TryComputePickupReqDiameterMm(itemSize, volumeK, out reqMm)) return false;
            if (!TryFloorMillimetres(ballSemanticDiameterWorldM, out ballMm)) return false;
            within = reqMm <= ballMm;
            return true;
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
        /// reported as a gain problem. The other arguments are exactly
        /// <see cref="EvaluateAbsorb"/>'s, including the per-item factor and the ball's
        /// semantic diameter, so the gameplay path can use this one entry point.
        /// </remarks>
        public static AbsorbOutcome EvaluateAbsorbWithValue(
            bool itemAbsorbable,
            string itemKind,
            string[] allowedKinds,
            int allowedKindCount,
            Vector3 itemSize,
            Quaternion itemRotation,
            Vector3 itemPosition,
            Vector3 ballPosition,
            float collisionRadiusWorldM,
            float contactRadiusWorldM,
            float itemVolumeK,
            float ballSemanticDiameterWorldM,
            float maxGrowthRadiusWorldM,
            long itemGrowthMm3)
        {
            AbsorbOutcome geometry = EvaluateAbsorb(itemAbsorbable, itemKind, allowedKinds,
                allowedKindCount, itemSize, itemRotation, itemPosition, ballPosition,
                collisionRadiusWorldM, contactRadiusWorldM, itemVolumeK, ballSemanticDiameterWorldM,
                maxGrowthRadiusWorldM);
            return ApplyGrowthGate(geometry, itemGrowthMm3);
        }

        // The gain precondition both entry points share, written once so the two cannot
        // drift about which refusal wins. Since steps C+D the item's growth is a finished
        // mm^3 integer (priced once at import), so the only rule left is "a real gain".
        private static AbsorbOutcome ApplyGrowthGate(AbsorbOutcome geometry, long itemGrowthMm3)
        {
            if (geometry != AbsorbOutcome.Absorb) return geometry;
            long gain;
            if (!TryAbsorbGrowthToLedgerGain(itemGrowthMm3, out gain))
                return AbsorbOutcome.RejectNoGain;
            if (gain <= 0L) return AbsorbOutcome.RejectNoGain;
            return AbsorbOutcome.Absorb;
        }

        /// <summary>
        /// Prices one item's growth exactly as the original does: <c>penalty * k * a*b*c</c>
        /// in mm^3, rounded half-to-even.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the runtime twin of the editor's import-time pricing
        /// (<c>PickupFieldAuthoringRules</c> / <c>placement_core.item_growth_mm3</c>); the
        /// imported scene carries the result in <c>PickupFieldHost.propGrowthMm3</c>, so this
        /// fallback exists only for a host without that column. Float32 note: the editor
        /// computes in double and stores an exact long; this fallback multiplies in float, so
        /// a huge item's value can differ by a few thousand mm^3 (a millilitre) from the
        /// imported number. That is below the ledger's ability to matter and is documented
        /// rather than hidden.
        /// </para>
        /// <para>
        /// Refuses a non-finite/negative bounds triple, a non-positive volume, and a value
        /// outside the 64-bit quantum range; the caller reports RejectNoGain (never a silent
        /// zero).
        /// </para>
        /// </remarks>
        public static bool TryComputeGrowthMm3(Vector3 bounds, float volumeK,
            float volumePenalty, out long growthMm3)
        {
            growthMm3 = 0L;
            if (!KatamariMath.IsFiniteVector(bounds)) return false;
            if (bounds.x < 0f || bounds.y < 0f || bounds.z < 0f) return false;
            if (!KatamariMath.IsFiniteFloat(volumeK) || volumeK <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(volumePenalty) || volumePenalty <= 0f ||
                volumePenalty > MaxVolumePenalty)
            {
                return false;
            }

            float volumeM3 = bounds.x * bounds.y * bounds.z * volumeK * volumePenalty;
            if (!KatamariMath.IsFiniteFloat(volumeM3) || volumeM3 <= 0f) return false;
            float mm3 = volumeM3 * 1000000000f;
            if (!KatamariMath.IsFiniteFloat(mm3) || mm3 >= 9223372036854775808f) return false;
            long rounded = (long)Mathf.Round(mm3);
            if (rounded <= 0L) return false;
            growthMm3 = rounded;
            return true;
        }

        /// <summary>
        /// Accepts one item's already-priced growth (mm^3) as the ledger gain.
        /// </summary>
        /// <remarks>
        /// The gain IS the growth: since steps C+D the item's value is computed once, at
        /// import, as <c>round(penalty * k * a*b*c * 1e9)</c> and stored in the host's
        /// <c>long[] propGrowthMm3</c>. There is no unit conversion, no float truncation and
        /// no rounding left here; the only refusal is a non-positive gain (zero would pass
        /// the geometry gate, take a claim and vanish without a score), which the caller
        /// reports as <see cref="AbsorbOutcome.RejectNoGain"/>. The result is 64-bit because
        /// the ledger's columns are (Docs/OPEN_DECISIONS.md (r)); it becomes real value only
        /// when the ledger commits the matching event key.
        /// </remarks>
        public static bool TryAbsorbGrowthToLedgerGain(long itemGrowthMm3, out long gain)
        {
            gain = 0L;
            if (itemGrowthMm3 <= 0L) return false;
            gain = itemGrowthMm3;
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
