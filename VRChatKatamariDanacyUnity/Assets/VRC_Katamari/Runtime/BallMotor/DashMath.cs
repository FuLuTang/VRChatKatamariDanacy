using VRCKatamari.BallContact;
using VRCKatamari.Core;

namespace VRCKatamari.BallMotor
{
    /// <summary>
    /// The dash's rules: hold to charge, spinning in place once the charge is long enough,
    /// then release to fire the burst only if the charge reached the kickoff tier for the
    /// ball's size; one burst, a cooldown, and the original's boost energy and huff.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Owner 2026-11-18: the dash follows the original's Charge'n'Roll logic instead of a
    /// single button press. VR holds the LEFT trigger (<c>InputUse</c>, left hand only); PC
    /// holds Shift and releases it. The input layers only send press/release; every rule is
    /// here, so the two platforms cannot drift.
    /// </para>
    /// <para>
    /// <b>Original mechanics, and where the numbers come from.</b> The REROLL speedrun notes
    /// (S3 in Docs/Research/KatamariBallRules/ORIGINAL_BALL_INERTIA.md, row 5) describe the
    /// original at a fixed 30 Hz tick: one alternating stick push is a "gacha", counted only
    /// if the opposite one follows within 12 frames (0.4 s); a boost needs 5 gachas below
    /// 50 cm, 6 up to 5 m, 7 up to 10 m and 8 above 10 m; a spin starts at gacha count 3 and
    /// lasts until the count resets; boost energy is 240 frames (8 s) and drains by 1 per
    /// frame while the count is at least 3; after 100 idle frames (3.33 s) it recharges 18
    /// energy (0.6 s); at 0 the player huffs for 240 frames (8 s) with all boost inputs
    /// ignored, and the energy is refilled afterwards.
    /// </para>
    /// <para>
    /// <b>Charge time conversion (our mapping, written down because it is a choice).</b>
    /// One required gacha becomes one 0.4 s window of holding: the window is the longest gap
    /// the original still counts between two alternating pushes, so spending one window per
    /// required push reproduces the original's required input duration and gives no reward
    /// for mashing. Tiers: 5/6/7/8 windows = <b>2.0 / 2.4 / 2.8 / 3.2 s</b> of hold. The spin
    /// starts after 3 windows (1.2 s), matching "spin at gacha count 3", and the energy drain
    /// follows the same threshold. The 0.4 s after the release is the gacha timer's tail, so
    /// energy keeps draining while it runs, as in the original.
    /// </para>
    /// <para>
    /// <b>What is ours, not the original's (listed for the Lead/owner).</b> The REROLL notes
    /// give no burst speed multiplier, no burst duration and no cooldown; the burst keeps the
    /// project's earlier design values 1.6x / 0.45 s / 1.2 s
    /// (<see cref="ContactTuning.DashSpeedScale"/> etc., CONTACT_MODEL_V2 §3.8), and the
    /// 1.2 s cooldown is ours (the original spends energy instead). The spin rate is not
    /// published either: the original's spin speed comes from how fast the player alternates,
    /// so <see cref="SpinRateDegreesPerSecond"/> is a PROVISIONAL pick.
    /// </para>
    /// <para>
    /// The state is a small float array because UdonSharp has no user structs - the same
    /// reason the motor's other per-step state is stored in arrays.
    /// </para>
    /// </remarks>
    public static class DashMath
    {
        /// <summary>Floats in a dash state array.</summary>
        public const int StateLength = 8;

        /// <summary>State index: 1 while the button is held and the charge is running.</summary>
        public const int StateCharging = 0;

        /// <summary>State index: seconds the button has been held this charge.</summary>
        public const int StateChargeSeconds = 1;

        /// <summary>State index: seconds of burst left (0 = not dashing).</summary>
        public const int StateDashRemaining = 2;

        /// <summary>State index: seconds until the next charge may begin (0 = ready).</summary>
        public const int StateCooldownRemaining = 3;

        /// <summary>State index: boost energy left, in seconds of boost activity.</summary>
        public const int StateEnergySeconds = 4;

        /// <summary>State index: seconds of huff left (0 = not huffing).</summary>
        public const int StateHuffRemaining = 5;

        /// <summary>State index: seconds of post-release activity left (the gacha timer tail).</summary>
        public const int StateBoostTailSeconds = 6;

        /// <summary>State index: seconds since the last boost-energy use (for the recharge).</summary>
        public const int StateIdleSeconds = 7;

        /// <summary>The original simulation tick: all frame counts below are at 30 Hz.</summary>
        public const float OriginalTickSeconds = 1f / 30f;

        /// <summary>The gacha window, 12 frames: the longest gap that still counts as a chain.</summary>
        public const float GachaWindowSeconds = 12f * OriginalTickSeconds;

        /// <summary>
        /// Slack on the "full charge" compare. The charge time is accumulated one 50 Hz step
        /// at a time, and float32 accumulation over a 2-3.2 s charge drifts by a few 1e-6 s;
        /// the tolerance is 0.1 ms, far below anything a player could feel, so a charge that
        /// really ran the whole tier can never be misread as short.
        /// </summary>
        public const float ChargeToleranceSeconds = 1e-4f;

        /// <summary>Gachas after which the original starts spinning (and drains energy).</summary>
        public const int GachasForSpin = 3;

        /// <summary>Charge time at which the ball starts spinning: 3 gacha windows = 1.2 s.</summary>
        public const float SpinChargeSeconds = GachasForSpin * GachaWindowSeconds;

        /// <summary>Boost energy capacity, 240 frames = 8 s of boost activity.</summary>
        public const float EnergyCapacitySeconds = 240f * OriginalTickSeconds;

        /// <summary>Huff length, 240 frames = 8 s.</summary>
        public const float HuffSeconds = 240f * OriginalTickSeconds;

        /// <summary>Idle time before the original recharges, 100 frames = 3.33 s.</summary>
        public const float RechargeIdleSeconds = 100f * OriginalTickSeconds;

        /// <summary>Energy recharged per recharge event, 18 frames = 0.6 s.</summary>
        public const float RechargeEnergySeconds = 18f * OriginalTickSeconds;

        /// <summary>
        /// PROVISIONAL spin rate while charging. The original's spin speed is the player's
        /// alternation speed, which has no published value; 180 deg/s turns 1 to 1.6 times
        /// over a 2.0-3.2 s charge.
        /// </summary>
        public const float SpinRateDegreesPerSecond = 180f;

        // ---- Release outcomes (ReleaseCharge's return value) ----

        /// <summary>Nothing was charging (a release without a press, or the charge was cancelled).</summary>
        public const int ReleaseNone = 0;

        /// <summary>The charge was full: the burst started.</summary>
        public const int ReleaseDash = 1;

        /// <summary>The charge was short of the tier: no burst.</summary>
        public const int ReleaseNotFull = 2;

        /// <summary>
        /// The gachas the original needs for a boost at this ball diameter: 5 below 50 cm,
        /// 6 up to 5 m, 7 up to 10 m, 8 above. An unusable diameter uses the smallest tier.
        /// </summary>
        public static int ChargeGachasFor(float diameterM)
        {
            if (!KatamariMath.IsFiniteFloat(diameterM) || diameterM < 0.5f) return 5;
            if (diameterM < 5f) return 6;
            if (diameterM < 10f) return 7;
            return 8;
        }

        /// <summary>
        /// The hold time that tier needs: one 0.4 s gacha window per required gacha.
        /// </summary>
        public static float ChargeSecondsFor(float diameterM)
        {
            return ChargeGachasFor(diameterM) * GachaWindowSeconds;
        }

        /// <summary>Clears a dash state array (ready: no charge, full energy). False for a null or short array.</summary>
        public static bool TryReset(float[] state)
        {
            if (!IsValid(state)) return false;
            for (int i = 0; i < StateLength; i++) state[i] = 0f;
            state[StateEnergySeconds] = EnergyCapacitySeconds;
            return true;
        }

        /// <summary>
        /// Starts a charge. False (nothing changes) while already charging, dashing, cooling
        /// down, huffing, out of energy, or for a bad state array.
        /// </summary>
        public static bool TryBeginCharge(float[] state)
        {
            if (!IsValid(state)) return false;
            if (state[StateCharging] > 0f) return false;
            if (state[StateDashRemaining] > 0f) return false;
            if (state[StateCooldownRemaining] > 0f) return false;
            if (state[StateHuffRemaining] > 0f) return false;
            if (state[StateEnergySeconds] <= 0f) return false;
            state[StateCharging] = 1f;
            state[StateChargeSeconds] = 0f;
            return true;
        }

        /// <summary>Abandons a charge without a burst (gate closed, station exit, respawn).</summary>
        public static void CancelCharge(float[] state)
        {
            if (!IsValid(state)) return;
            state[StateCharging] = 0f;
            state[StateChargeSeconds] = 0f;
        }

        /// <summary>
        /// Ends a charge: full charge fires the burst and starts the cooldown; a short charge
        /// does nothing. Returns one of the <c>Release*</c> codes.
        /// </summary>
        public static int ReleaseCharge(float[] state, float diameterM)
        {
            if (!IsValid(state)) return ReleaseNone;
            if (state[StateCharging] <= 0f) return ReleaseNone;

            float required = ChargeSecondsFor(diameterM);
            bool full = state[StateChargeSeconds] + ChargeToleranceSeconds >= required;
            state[StateCharging] = 0f;
            state[StateChargeSeconds] = 0f;
            if (!full) return ReleaseNotFull;

            state[StateDashRemaining] = ContactTuning.DashSeconds;
            state[StateCooldownRemaining] = ContactTuning.DashCooldownS;
            state[StateBoostTailSeconds] = GachaWindowSeconds;
            return ReleaseDash;
        }

        /// <summary>
        /// Advances one fixed step: the charge, the burst, the cooldown, the post-release
        /// tail, the boost energy, the recharge and the huff. False (state untouched) for a
        /// bad array or a non-finite/negative delta; a zero delta changes nothing.
        /// </summary>
        public static bool TryStep(float[] state, float dt)
        {
            if (!IsValid(state)) return false;
            if (!KatamariMath.IsFiniteFloat(dt) || dt < 0f) return false;

            if (state[StateCharging] > 0f) state[StateChargeSeconds] = state[StateChargeSeconds] + dt;
            state[StateDashRemaining] = Decrement(state[StateDashRemaining], dt);
            state[StateCooldownRemaining] = Decrement(state[StateCooldownRemaining], dt);
            state[StateBoostTailSeconds] = Decrement(state[StateBoostTailSeconds], dt);

            if (state[StateHuffRemaining] > 0f)
            {
                // Huffing: all boost input is refused and the energy is refilled when it ends.
                state[StateHuffRemaining] = Decrement(state[StateHuffRemaining], dt);
                if (state[StateHuffRemaining] <= 0f)
                {
                    state[StateHuffRemaining] = 0f;
                    state[StateEnergySeconds] = EnergyCapacitySeconds;
                    state[StateIdleSeconds] = 0f;
                }
                return true;
            }

            // Boost activity drains energy: while the charge has reached the spin count,
            // while the burst runs, and for the gacha timer's tail after the release. That is
            // the original's "count >= 3" window, expressed in seconds.
            bool active = (state[StateCharging] > 0f
                    && state[StateChargeSeconds] >= SpinChargeSeconds)
                || state[StateDashRemaining] > 0f
                || state[StateBoostTailSeconds] > 0f;

            if (active)
            {
                state[StateIdleSeconds] = 0f;
                state[StateEnergySeconds] = Decrement(state[StateEnergySeconds], dt);
                if (state[StateEnergySeconds] <= 0f)
                {
                    // Out of energy: the charge is dropped and the huff begins. A burst that
                    // is already running is not cut short (the original's boost keeps going).
                    state[StateEnergySeconds] = 0f;
                    state[StateHuffRemaining] = HuffSeconds;
                    state[StateCharging] = 0f;
                    state[StateChargeSeconds] = 0f;
                }
                return true;
            }

            // Idle: after the original's 100 frames, one recharge event adds 18 frames.
            state[StateIdleSeconds] = state[StateIdleSeconds] + dt;
            if (state[StateIdleSeconds] >= RechargeIdleSeconds)
            {
                state[StateIdleSeconds] = state[StateIdleSeconds] - RechargeIdleSeconds;
                float energy = state[StateEnergySeconds] + RechargeEnergySeconds;
                state[StateEnergySeconds] = energy > EnergyCapacitySeconds
                    ? EnergyCapacitySeconds : energy;
            }
            return true;
        }

        /// <summary>Whether the button is held and the charge is running.</summary>
        public static bool IsCharging(float[] state)
        {
            if (!IsValid(state)) return false;
            return state[StateCharging] > 0f;
        }

        /// <summary>
        /// Whether the charge has reached the spin threshold: the ball spins in place from
        /// here on, as the original does from gacha count 3.
        /// </summary>
        public static bool IsSpinning(float[] state)
        {
            return IsCharging(state) && state[StateChargeSeconds] >= SpinChargeSeconds;
        }

        /// <summary>Seconds the button has been held this charge (0 when not charging).</summary>
        public static float ChargeSeconds(float[] state)
        {
            if (!IsValid(state) || state[StateCharging] <= 0f) return 0f;
            return state[StateChargeSeconds];
        }

        /// <summary>Whether the current charge has reached this diameter's kickoff tier.</summary>
        public static bool IsChargeFull(float[] state, float diameterM)
        {
            if (!IsCharging(state)) return false;
            return state[StateChargeSeconds] + ChargeToleranceSeconds >= ChargeSecondsFor(diameterM);
        }

        /// <summary>Whether a burst is running.</summary>
        public static bool IsDashing(float[] state)
        {
            if (!IsValid(state)) return false;
            return state[StateDashRemaining] > 0f;
        }

        /// <summary>
        /// The motion layer's speed multiplier: <see cref="ContactTuning.DashSpeedScale"/>
        /// while dashing, 1 otherwise (also 1 for an unusable state).
        /// </summary>
        public static float SpeedScale(float[] state)
        {
            return IsDashing(state) ? ContactTuning.DashSpeedScale : 1f;
        }

        /// <summary>Boost energy left in seconds of boost activity (0..<see cref="EnergyCapacitySeconds"/>).</summary>
        public static float EnergySeconds(float[] state)
        {
            if (!IsValid(state)) return 0f;
            return state[StateEnergySeconds];
        }

        /// <summary>Whether the huff is running: all charge input is refused until it ends.</summary>
        public static bool IsHuffed(float[] state)
        {
            if (!IsValid(state)) return false;
            return state[StateHuffRemaining] > 0f;
        }

        /// <summary>Whether a charge may begin now (ready, no burst, no cooldown, no huff, has energy).</summary>
        public static bool CanBeginCharge(float[] state)
        {
            if (!IsValid(state)) return false;
            return state[StateCharging] <= 0f
                && state[StateDashRemaining] <= 0f
                && state[StateCooldownRemaining] <= 0f
                && state[StateHuffRemaining] <= 0f
                && state[StateEnergySeconds] > 0f;
        }

        private static float Decrement(float value, float dt)
        {
            float next = value - dt;
            return next > 0f ? next : 0f;
        }

        private static bool IsValid(float[] state)
        {
            if (state == null || state.Length < StateLength) return false;
            for (int i = 0; i < StateLength; i++)
            {
                if (!KatamariMath.IsFiniteFloat(state[i]) || state[i] < 0f) return false;
            }
            return true;
        }
    }
}
