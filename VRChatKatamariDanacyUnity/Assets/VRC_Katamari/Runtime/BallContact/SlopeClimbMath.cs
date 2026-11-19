using UnityEngine;
using VRCKatamari.Motion;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// Project-owned slope stamina model. No height budget; wall climbing is separate.
    /// Structure informed by REROLL reverse-engineering (confidence C), independently
    /// implemented, not a translation. Values and evidence: TERRAIN_CLIMB 0.1.
    /// </summary>
    public static class SlopeClimbMath
    {
        public static bool IsSlope(float thetaDeg)
        {
            return thetaDeg > ContactTuning.ThetaRideDeg + ContactTuning.ThetaRideToleranceDeg
                && thetaDeg <= ContactTuning.ThetaSlopeMaxDeg;
        }

        public static float DrainRate(float thetaDeg, float diameterM, float uphillSeconds,
            float gateSpeed)
        {
            float size = Mathf.Sqrt(Mathf.Max(diameterM / ContactTuning.SlopeReferenceDiameterM, 0.01f));
            float steepness = Mathf.Clamp(thetaDeg / ContactTuning.SlopeDrainSaturationDeg, 0f, 1f);
            float fatigue = 1f + Mathf.Max(0f, uphillSeconds) * ContactTuning.SlopeFatiguePerSecond;
            // The SAME speed gate controls both speed and effort: dash gets more ascent
            // and less drain per second, without a second dash/stamina state machine.
            float effort = Mathf.Clamp(ContactTuning.ClimbVGateRef /
                Mathf.Max(gateSpeed, ContactTuning.ClimbVGateRef),
                1f / ContactTuning.SlopeEffortFloorScale, 1f);
            return ContactTuning.SlopeDrainPerSecond * steepness * fatigue * effort / size;
        }

        public static float UphillVerticalSpeed(float pushIn, float gateSpeed, float thetaDeg,
            float diameterM, float uphillSeconds)
        {
            float fatigue = Mathf.Max(ContactTuning.SlopeMinimumSpeedFactor,
                1f / (1f + Mathf.Max(0f, uphillSeconds) * ContactTuning.SlopeFatiguePerSecond));
            // Vertical rate = the wall channel's base rate v_up0 = 0.917 * D (0.275 m/s at
            // D = 0.30), times push-in and the dash rate factor (at most 1.5x), with NO height
            // fade and NO angle factor m(theta): a steeper slope is not faster. Evaluating the
            // wall formula at 90 deg is that exact base rate, because m(90)/m(90) = 1 and a
            // single implementation stays the owner of the rate family. thetaDeg only picks the
            // along-slope projection in Step; it does not scale the vertical rate here.
            return ClimbMath.ClimbSpeed(pushIn, gateSpeed, 90f, 0f,
                ContactTuning.KHeight * diameterM) * fatigue;
        }

        /// <summary>One substep; signed along speed is positive uphill, negative downhill.</summary>
        public static void Step(float thetaDeg, float diameterM, float pushIn, float gateSpeed,
            float dt, float restHoldSeconds, float restSpeedMps, ref float stamina,
            ref float uphillSeconds, ref float restSeconds, ref bool sliding, ref float along)
        {
            bool pushing = pushIn >= ContactTuning.PMin && gateSpeed >= ClimbMath.RequiredSpeed(thetaDeg);
            float sinTheta = Mathf.Sin(thetaDeg * Mathf.Deg2Rad);
            if (pushing && stamina > 0f)
            {
                // Midpoint integration makes the linear fatigue drain independent of substeps.
                float drain = DrainRate(thetaDeg, diameterM, uphillSeconds + dt * 0.5f, gateSpeed) * dt;
                uphillSeconds += dt;
                stamina = Mathf.Max(0f, stamina - drain);
                restSeconds = 0f;
                sliding = false;
                if (stamina > 0f)
                {
                    float target = UphillVerticalSpeed(pushIn, gateSpeed, thetaDeg, diameterM,
                        uphillSeconds) / Mathf.Max(sinTheta, ContactTuning.MinClimbSinTheta);
                    float rate = ContactTuning.ClimbRateLimit * dt;
                    along = target > along ? Mathf.Min(along + rate, target) : Mathf.Max(along - rate, target);
                    return;
                }
                along = 0f;
            }
            // Exhaustion removes effective uphill drive even if the player holds the stick.
            // Rest and slip use the existing latch, including the same 0.3 s delay.
            if (along > 0f) along = Mathf.Max(0f, along - ContactTuning.ClimbRateLimit * dt);
            float nextRest;
            bool nextSliding;
            RampSlideMath.TryStepRestLatch(restSeconds, sliding, Mathf.Abs(along), restSpeedMps,
                0f, true, dt, restHoldSeconds, out nextRest, out nextSliding);
            restSeconds = nextRest;
            sliding = nextSliding;
            if (sliding) along = Mathf.Max(along - ContactTuning.Gravity * sinTheta * dt,
                -ContactTuning.VSlideMax);
        }
    }
}
