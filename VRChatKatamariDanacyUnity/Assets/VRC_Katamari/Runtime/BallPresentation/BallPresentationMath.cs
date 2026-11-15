using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallPresentation
{
    /// <summary>
    /// Presentation-only math for the core mesh: which diameter to draw, and how to catch
    /// up with a new one without a pop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent units live here, and both are pure functions so the EditMode tests
    /// can pin them without a scene.
    /// </para>
    /// <para>
    /// <b>Target diameter.</b> Docs/Design/BallMotor/CONTACT_MODEL_V2.md section 4A.2 makes
    /// the visual core a separate shape: <c>r_visual = 0.95 * r_phys</c>. The motor exposes
    /// that radius as <c>GetVisualCoreRadiusWorldM()</c>; the mesh is drawn at
    /// <c>2 * r_visual</c>, which keeps the 5% margin between what the player sees and what
    /// the contact solver uses. Nothing here ever feeds a size back: the caller only reads
    /// the motor's physical diameter and draws the result, so the absorb gate, the growth
    /// ledger, the follow-rig camera and every physics query keep reading the physical value.
    /// </para>
    /// <para>
    /// <b>Catch-up spring.</b> The ledger commits an absorb and the physical diameter jumps
    /// within one fixed step (the motor re-derives its size in the same step as the ledger
    /// commit), so a raw copy makes the ball pop. The step below is a critically damped
    /// (zeta = 1) spring, integrated with the closed form Unity's <c>SmoothDamp</c> uses:
    /// <c>omega = 2 / smoothTime</c>, <c>x = omega * dt</c>,
    /// <c>decay = 1 / (1 + x + 0.48x^2 + 0.235x^3)</c>. One scalar of velocity is enough
    /// state, and the caller owns it (this class keeps no fields: UdonSharp does not support
    /// static fields).
    /// </para>
    /// <para>
    /// Three rules make it read as growth instead of a wobble, and they are the reason the
    /// tests can be exact rather than approximate:
    /// <list type="number">
    /// <item>The spring never crosses the target. A crossing is a pop in the other
    /// direction, and a visual ball larger than the target would also be larger than the
    /// physical sphere it is supposed to sit inside.</item>
    /// <item>It SNAPS once within <see cref="SettleEpsilonM"/>, so the last value written is
    /// the exact target instead of the tail of an exponential (no permanent offset, and the
    /// caller's dead zone cannot leave a residue).</item>
    /// <item>A target that collapsed by more than half is a reset (a new round, a respawn),
    /// not a shrink: legitimate shrink is a bank loss of a few percent, so a fall that large
    /// snaps instead of being animated.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Time is caller-supplied (<c>Time.deltaTime</c> in the behaviour) so the same code is
    /// deterministic under test; a non-positive delta holds the current value and keeps the
    /// velocity, which is what a paused frame should do.
    /// </para>
    /// </remarks>
    public static class BallPresentationMath
    {
        // Where the spring snaps onto its target. 1e-5 m is a tenth of the 1e-4 m dead zone
        // BallView uses to skip invisible writes, so the settling write is exact and the
        // frames after it are no-ops.
        private const float SettleEpsilonM = 0.00001f;

        // Below this fraction of the current diameter a new target is a reset, not a shrink.
        // Bank shrink takes a few percent at most, so half is unambiguously a teleport.
        private const float ResetBelowCurrentRatio = 0.5f;

        /// <summary>
        /// The core mesh diameter to draw: <c>2 * r_visual</c> when the visual shape is
        /// enabled, the motor's own diameter when it is not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// False only when the physical diameter is unusable (non-finite or non-positive); a
        /// caller then keeps the scale it already had, which is also what the motor reports
        /// before its Start has derived a size.
        /// </para>
        /// <para>
        /// A non-positive or non-finite visual radius falls back to the physical diameter
        /// rather than failing: the motor's own getter already answers an underivable radius
        /// with the physical one, so refusing here would stop drawing the ball for a reason
        /// the player cannot see.
        /// </para>
        /// </remarks>
        public static bool TryCoreTargetDiameter(float physicalDiameterM, float visualCoreRadiusM,
            bool useVisualCoreRadius, out float targetDiameterM)
        {
            targetDiameterM = 0f;
            if (!KatamariMath.IsFiniteFloat(physicalDiameterM) || physicalDiameterM <= 0f)
                return false;
            if (!useVisualCoreRadius)
            {
                targetDiameterM = physicalDiameterM;
                return true;
            }
            if (!KatamariMath.IsFiniteFloat(visualCoreRadiusM) || visualCoreRadiusM <= 0f)
            {
                targetDiameterM = physicalDiameterM;
                return true;
            }
            float diameter = 2f * visualCoreRadiusM;
            if (!KatamariMath.IsFiniteFloat(diameter) || diameter <= 0f)
            {
                targetDiameterM = physicalDiameterM;
                return true;
            }
            targetDiameterM = diameter;
            return true;
        }

        /// <summary>
        /// Advances the visual diameter one frame towards the target.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Feed the returned <c>nextDiameterM</c> and <c>nextVelocityMps</c> back on the next
        /// call with the new target. <c>smoothTimeSeconds</c> is the catch-up time: the
        /// response is <c>1 - (1 + omega*t) * exp(-omega*t)</c>, so 0.08 s is 59% caught up at
        /// 80 ms, 96% at 200 ms, and inside <see cref="SettleEpsilonM"/> (a bit-exact snap) by
        /// about 0.42 s. Velocity is metres per second of diameter.
        /// </para>
        /// <para>
        /// False means an unusable argument (a non-finite value, a non-positive target or
        /// catch-up time); the out values are then the input current diameter with zero
        /// velocity, i.e. nothing to write. A non-positive CURRENT diameter is not an error:
        /// it is the first frame, and the step snaps to the target.
        /// </para>
        /// </remarks>
        public static bool TryStepCoreDiameter(float currentDiameterM, float velocityMps,
            float targetDiameterM, float deltaTimeSeconds, float smoothTimeSeconds,
            out float nextDiameterM, out float nextVelocityMps)
        {
            nextDiameterM = currentDiameterM;
            nextVelocityMps = 0f;
            if (!KatamariMath.IsFiniteFloat(currentDiameterM)) return false;
            if (!KatamariMath.IsFiniteFloat(velocityMps)) return false;
            if (!KatamariMath.IsFiniteFloat(targetDiameterM) || targetDiameterM <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(smoothTimeSeconds) || smoothTimeSeconds <= 0f)
                return false;

            // First frame or a reset: there is nothing to animate between the two values.
            if (currentDiameterM <= 0f ||
                targetDiameterM < currentDiameterM * ResetBelowCurrentRatio)
            {
                nextDiameterM = targetDiameterM;
                return true;
            }

            nextVelocityMps = velocityMps;

            // A paused or nonsense frame holds the value; it is not a data error.
            if (!KatamariMath.IsFiniteFloat(deltaTimeSeconds) || deltaTimeSeconds <= 0f)
                return true;

            float omega = 2f / smoothTimeSeconds;
            float x = omega * deltaTimeSeconds;
            float decay = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = currentDiameterM - targetDiameterM;
            float stepM = (velocityMps + omega * change) * deltaTimeSeconds;
            float velocity = (velocityMps - omega * stepM) * decay;
            float next = targetDiameterM + (change + stepM) * decay;

            // A crossing means the spring overshot; snap instead. This also covers the
            // settled case (next == target) so the velocity stops there.
            bool approachingUp = targetDiameterM > currentDiameterM;
            if (approachingUp == (next > targetDiameterM))
            {
                next = targetDiameterM;
                velocity = 0f;
            }
            if (Mathf.Abs(next - targetDiameterM) <= SettleEpsilonM)
            {
                next = targetDiameterM;
                velocity = 0f;
            }

            nextDiameterM = next;
            nextVelocityMps = velocity;
            return true;
        }
    }
}
