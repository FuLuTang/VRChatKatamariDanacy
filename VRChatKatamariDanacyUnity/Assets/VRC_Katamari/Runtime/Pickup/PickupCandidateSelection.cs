using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Pickup
{
    /// <summary>
    /// Which free props a pickup host offers to the motor's candidate queue: the nearest
    /// few that the ball could absorb at its CURRENT size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure and allocation-free, so the choice is tested without a GameObject. It is the
    /// per-prop half of <c>PickupFieldHost.ScanProps</c>: the host walks its props, calls
    /// <see cref="OfferProp"/> once per free prop, and afterwards makes the motor's queue
    /// agree with the result (queue what was selected, withdraw what was queued but is no
    /// longer selected).
    /// </para>
    /// <para>
    /// The queue holds only props the ball can actually take. The motor's queue is four
    /// slots and never drops a refused candidate, so a prop the gate refuses on size
    /// (<see cref="PickupAbsorbMath.IsBeyondAbsorbLimit"/>) would sit in a slot and be
    /// re-refused on every step, starving the small props behind it. Those props are not
    /// lost to the player: they stay solid obstacles through the host's separate obstacle
    /// path, which runs the same predicate and never touches the queue. The size test here
    /// IS that predicate - the item's own size and k against the motor's semantic diameter,
    /// both floored to whole millimetres - so "can be absorbed" has one definition. The
    /// ball grows, the host asks again at the new diameter on the next scan, and a prop
    /// that just became absorbable enters the selection by itself.
    /// </para>
    /// <para>
    /// Ranking is by squared centre distance, the host's historical key, over the props
    /// inside the activation radius. Reach, kind and growth cap stay the gate's business
    /// at step time: this layer only keeps a prop the gate would refuse on SIZE out of a
    /// slot, which is the one refusal that cannot change until the ball grows.
    /// </para>
    /// </remarks>
    public static class PickupCandidateSelection
    {
        /// <summary>The prop is farther than the activation radius (or its distance is not finite).</summary>
        public const int OfferOutOfRange = 0;

        /// <summary>In range, but the size gate refuses it at this ball diameter: no slot.</summary>
        public const int OfferBeyondLimit = 1;

        /// <summary>In range and within the size gate: ranked among the nearest.</summary>
        public const int OfferWithinLimit = 2;

        /// <summary>
        /// Offers one free prop to the bounded nearest-first selection.
        /// </summary>
        /// <remarks>
        /// The per-prop size and k are read only after the cheap distance test, so a scan
        /// over hundreds of props pays array reads for the handful inside the radius.
        /// </remarks>
        /// <param name="index">The prop's row: stored in <paramref name="indices"/> when it ranks, and the row read from the two per-prop arrays.</param>
        /// <param name="propPosition">World position of the prop.</param>
        /// <param name="ballCenter">World position of the ball centre.</param>
        /// <param name="activationRadiusM">Centre distance inside which a prop is considered.</param>
        /// <param name="ownSizes">Each prop's own edge lengths (a, b, c), never its turned world box.</param>
        /// <param name="volumeKs">Each prop's per-item volume factor k.</param>
        /// <param name="ballSemanticDiameterWorldM">The motor's current semantic diameter, metres.</param>
        /// <param name="indices">Selection rows, ascending by distance; caller-owned.</param>
        /// <param name="distancesSq">Squared distances parallel to <paramref name="indices"/>.</param>
        /// <param name="selected">How many entries the selection holds now.</param>
        /// <param name="limit">Selection size (the motor's queue capacity).</param>
        /// <param name="selectedAfter">The entry count after this offer.</param>
        /// <returns>One of the three <c>Offer*</c> codes; the host tallies "in range" and "within limit" from it.</returns>
        public static int OfferProp(int index, Vector3 propPosition, Vector3 ballCenter,
            float activationRadiusM, Vector3[] ownSizes, float[] volumeKs,
            float ballSemanticDiameterWorldM, int[] indices, float[] distancesSq,
            int selected, int limit, out int selectedAfter)
        {
            selectedAfter = selected;

            float dx = propPosition.x - ballCenter.x;
            float dy = propPosition.y - ballCenter.y;
            float dz = propPosition.z - ballCenter.z;
            float distSq = dx * dx + dy * dy + dz * dz;
            if (!KatamariMath.IsFiniteFloat(distSq)) return OfferOutOfRange;

            // The size test runs only inside the radius (it needs a cube root).
            bool beyondLimit = false;
            if (distSq <= activationRadiusM * activationRadiusM)
            {
                beyondLimit = PickupAbsorbMath.IsBeyondAbsorbLimit(ownSizes[index], volumeKs[index],
                    ballSemanticDiameterWorldM);
            }
            return OfferMeasured(index, distSq, activationRadiusM, beyondLimit, indices,
                distancesSq, selected, limit, out selectedAfter);
        }

        /// <summary>
        /// The decision core of <see cref="OfferProp"/> for a caller that already measured the
        /// squared centre distance (finite) and resolved the size verdict, e.g. from a cached
        /// requirement (<see cref="PickupAbsorbMath.IsBeyondCachedRequirement"/>).
        /// </summary>
        /// <remarks>
        /// <paramref name="beyondLimit"/> is only read for a row inside the radius. The
        /// selection is the nearest rows by (squared distance, row index): equal distances rank
        /// the lower row first, so the result does not depend on the order rows are offered in
        /// (the host walks grid cells, not rows 0..n-1).
        /// </remarks>
        public static int OfferMeasured(int index, float distSq, float activationRadiusM,
            bool beyondLimit, int[] indices, float[] distancesSq, int selected, int limit,
            out int selectedAfter)
        {
            selectedAfter = selected;
            if (!KatamariMath.IsFiniteFloat(distSq)) return OfferOutOfRange;
            if (distSq > activationRadiusM * activationRadiusM) return OfferOutOfRange;
            if (beyondLimit) return OfferBeyondLimit;

            selectedAfter = InsertNearest(index, distSq, indices, distancesSq, selected, limit);
            return OfferWithinLimit;
        }

        /// <summary>
        /// True when the centre or any own-box corner moved more than toleranceM since submit.
        /// Rotation is measured in metres at the corners, not by an arbitrary angle threshold.
        /// </summary>
        public static bool HasPoseMoved(Vector3 previousPosition, Quaternion previousRotation,
            Vector3 position, Quaternion rotation, Vector3 ownSize, float toleranceM)
        {
            float toleranceSq = toleranceM * toleranceM;
            Vector3 translation = position - previousPosition;
            if (translation.sqrMagnitude > toleranceSq) return true;
            if (rotation.x == previousRotation.x && rotation.y == previousRotation.y
                && rotation.z == previousRotation.z && rotation.w == previousRotation.w) return false;
            Vector3 half = ownSize * 0.5f;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new Vector3((corner & 1) == 0 ? -half.x : half.x,
                    (corner & 2) == 0 ? -half.y : half.y,
                    (corner & 4) == 0 ? -half.z : half.z);
                Vector3 delta = translation + rotation * local - previousRotation * local;
                if (delta.sqrMagnitude > toleranceSq) return true;
            }
            return false;
        }

        /// <summary>True when <paramref name="index"/> is among the first <paramref name="selected"/> rows.</summary>
        public static bool IsSelected(int index, int[] indices, int selected)
        {
            for (int i = 0; i < selected; i++)
            {
                if (indices[i] == index) return true;
            }
            return false;
        }

        /// <summary>
        /// Keeps the nearest rows seen so far, ascending by (squared distance, row index).
        /// </summary>
        /// <remarks>
        /// Insertion into two small caller-owned arrays: no allocation and no sort of the
        /// whole prop list. Returns the new number of selected entries; a row farther than
        /// the current farthest of a full selection is dropped. Equal distances rank the lower
        /// row first - what an ascending 0..n-1 walk with a strict comparison always produced -
        /// so any offer order gives the same selection.
        /// </remarks>
        public static int InsertNearest(int index, float distSq, int[] indices,
            float[] distancesSq, int selected, int limit)
        {
            if (limit <= 0) return selected;
            if (limit > indices.Length) limit = indices.Length;
            if (limit > distancesSq.Length) limit = distancesSq.Length;

            if (selected < limit)
            {
                int at = selected;
                while (at > 0 && Ranks(distSq, index, distancesSq[at - 1], indices[at - 1]))
                {
                    distancesSq[at] = distancesSq[at - 1];
                    indices[at] = indices[at - 1];
                    at--;
                }
                distancesSq[at] = distSq;
                indices[at] = index;
                return selected + 1;
            }

            if (!Ranks(distSq, index, distancesSq[limit - 1], indices[limit - 1])) return selected;
            int pos = limit - 1;
            while (pos > 0 && Ranks(distSq, index, distancesSq[pos - 1], indices[pos - 1]))
            {
                distancesSq[pos] = distancesSq[pos - 1];
                indices[pos] = indices[pos - 1];
                pos--;
            }
            distancesSq[pos] = distSq;
            indices[pos] = index;
            return selected;
        }

        // True when (distSq, index) ranks strictly before (otherDistSq, otherIndex).
        private static bool Ranks(float distSq, int index, float otherDistSq, int otherIndex)
        {
            if (distSq < otherDistSq) return true;
            if (distSq > otherDistSq) return false;
            return index < otherIndex;
        }
    }
}
