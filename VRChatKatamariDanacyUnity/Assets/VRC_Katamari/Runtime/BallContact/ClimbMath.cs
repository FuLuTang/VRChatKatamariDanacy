using UnityEngine;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// The climb channel as a parameterised model: height cap, velocity gate, the
    /// creep shape, the sqrt fade and the vertical climb rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The climb is parameterised by TIME, not by a fixed speed: H_cap is proportional
    /// to the radius, so a bigger ball climbs faster in m/s but reaches its cap in the
    /// same seconds. v_up0 = 1.718 * H_cap / T_climb is the initial vertical rate whose
    /// sqrt fade brings the ball to 98% of H_cap after T_climb (the 1.718 = 1/(1-sqrt(0.02))
    /// comes from inverting t(h) = 2H/v0 * (1 - sqrt(1 - h/H))). m(theta) = cos + m0*sin
    /// keeps a 90 deg wall at the creep floor, and the sqrt fade (not a linear one)
    /// still leaves about a third of the speed at 0.9 H_cap, so the ball can actually
    /// arrive.
    /// </para>
    /// <para>
    /// Every number is OUR design (PROVISIONAL, see <see cref="ContactTuning"/>); the
    /// only anchored one is k_h. These are total functions of finite inputs: the guards
    /// that the model has are kept (a non-positive push/gate/cap gives zero speed, a
    /// non-positive cap gives no fade), and the solver validates radius/dt once per step
    /// instead of each helper repeating it.
    /// </para>
    /// </remarks>
    public static class ClimbMath
    {
        /// <summary>
        /// H_cap = k_h * (2r + lambda * L_eff): how far the centre may rise.
        /// </summary>
        /// <remarks>
        /// The protrusion term is clamped at zero because a pin that is inside the core
        /// adds nothing; the cap that limits L_eff is the shape layer's business, not
        /// this formula's.
        /// </remarks>
        public static float ClimbHeightCap(float radius, float protrusionM = 0f,
            float kHeight = ContactTuning.KHeight,
            float lambdaProtrusion = ContactTuning.LambdaProtrusion)
        {
            return kHeight * (2f * radius + lambdaProtrusion * Mathf.Max(0f, protrusionM));
        }

        /// <summary>
        /// v_req(theta) = v_min * sin(theta)^alpha: the continuous velocity gate.
        /// </summary>
        /// <remarks>
        /// There is no angle whitelist; the gate tightens continuously with the angle, so
        /// a 90 deg wall needs v_min and a 50 deg slab needs less. A negative angle sine
        /// (impossible for a classified wall) would give a negative requirement, which is
        /// harmless because the gate is compared with a nonnegative v_gate.
        /// </remarks>
        public static float RequiredSpeed(float thetaDeg,
            float vMin = ContactTuning.VMinGate, float alpha = ContactTuning.GateAlpha)
        {
            return vMin * Mathf.Pow(Mathf.Sin(thetaDeg * Mathf.Deg2Rad), alpha);
        }

        /// <summary>
        /// m(theta) = cos(theta) + m0 * sin(theta): 1 at 0 deg, m0 at 90 deg.
        /// </summary>
        public static float ClimbShape(float thetaDeg, float m0 = ContactTuning.M0)
        {
            float radians = thetaDeg * Mathf.Deg2Rad;
            return Mathf.Cos(radians) + m0 * Mathf.Sin(radians);
        }

        /// <summary>
        /// sqrt(1 - h/H_cap): the climb speed reaches zero only at the cap.
        /// </summary>
        public static float ClimbFade(float heightM, float hCapM)
        {
            if (hCapM <= 0f) return 0f;
            return Mathf.Sqrt(Mathf.Clamp(1f - heightM / hCapM, 0f, 1f));
        }

        /// <summary>
        /// The vertical climb rate at this push-in, gate speed, angle and height.
        /// </summary>
        /// <remarks>
        /// v_up = clamp(p, 0, 1) * rate(v_gate) * (m(theta)/m(90)) * v_up0 * fade(h).
        /// rate is the gate speed over 2 m/s clamped to [0.75, 1.5], which is how a dash
        /// (a higher target speed) makes the climb 50% faster without touching H_cap. A
        /// non-positive climb time would divide by zero in the model; it returns zero here
        /// instead, which is the only guard the port adds.
        /// </remarks>
        public static float ClimbSpeed(float pushIn, float vGate, float thetaDeg,
            float heightM, float hCapM, float climbTimeS = ContactTuning.ClimbTimeS)
        {
            if (pushIn <= 0f || vGate <= 0f || hCapM <= 0f) return 0f;
            if (climbTimeS <= 0f) return 0f;
            float rateFactor = Mathf.Clamp(vGate / ContactTuning.ClimbVGateRef,
                ContactTuning.ClimbRateMin, ContactTuning.ClimbRateMax);
            float vUp0 = ContactTuning.ClimbTimeNorm * hCapM / climbTimeS;
            return Mathf.Clamp(pushIn, 0f, 1f) * rateFactor
                * (ClimbShape(thetaDeg) / ClimbShape(90f))
                * vUp0 * ClimbFade(heightM, hCapM);
        }

        /// <summary>
        /// The complete climb predicate: enough push, gate speed, below the cap, no
        /// cooldown. Lock state is the caller's, because "one attempt per push" is state.
        /// </summary>
        public static bool IsClimbEligible(float pushIn, float vGate, float thetaDeg,
            float heightM, float hCapM, float cooldownS)
        {
            return pushIn >= ContactTuning.PMin && cooldownS <= 0f && heightM < hCapM
                && vGate >= RequiredSpeed(thetaDeg);
        }
    }
}
