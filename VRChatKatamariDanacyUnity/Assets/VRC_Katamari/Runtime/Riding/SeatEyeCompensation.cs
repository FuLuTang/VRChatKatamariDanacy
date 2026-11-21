using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// Puts the rider's EYE on the follow frame's eye point, and their FEET (the station anchor
    /// of a standing station) on the ground beside the ball, by measuring the head against the
    /// anchor once per ride.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bug this exists for: the follow frame (<see cref="VRCKatamari.BallLayers.FollowRigMath"/>)
    /// computes where the viewer's EYE should be, and the scene used to put the seat exactly
    /// there. A station carries the avatar's ROOT, not its eye, so the real camera (the
    /// avatar's own head camera - a scene has no Camera object at all) ended up one seated eye
    /// height above the intended point, i.e. "the viewpoint is too high after sitting down".
    /// The seat is therefore derived from the eye point: measure the head relative to the
    /// seat, then offset the seat so the head lands on the eye point. The math is
    /// <see cref="SeatEyeMath"/>; this behaviour owns the measurement TIMING, the transform
    /// write and the resets.
    /// </para>
    /// <para>
    /// Measurement timing is the whole VR argument, so it is fixed, not adaptive. After the
    /// local player sits there is a settling window (<c>settleFrames</c>) because sitting IK
    /// and the seat placement are not stable on the sit frame, and the ClientSim station
    /// manager moves the player as late as it can; then a short window of
    /// <c>sampleFrames</c> frames is averaged into ONE sample; then the seat glides to the
    /// offset over a handful of frames and FREEZES. The head is never chased afterwards: in
    /// VR the head moves with the real head, and a seat that followed it every frame would
    /// drag the whole world around and make the player sick. The same "one sample, then
    /// frozen" rule is what a desktop view gets, so both platforms are corrected by the same
    /// number, and re-measurement only happens on a new ride, on a respawn, or when the
    /// avatar changes while seated.
    /// </para>
    /// <para>
    /// Who measures what: this behaviour only reads the local player's head tracking datum,
    /// the rig root and the seat. It writes exactly one thing, the seat anchor's world
    /// position (resolved by Unity into its local position), as
    /// <c>rigRoot.position + rigRoot.rotation * offset</c> - the seat offset lives in the RIG
    /// frame so the rider keeps the same seat as the ball turns. It never touches the motor,
    /// the ball or the follow frame. <see cref="BallRideStation"/> is the only caller
    /// (<see cref="BeginSeated"/> / <see cref="EndSeated"/>), because it is the one place that
    /// knows the local player sat down; this behaviour deliberately does not listen to the
    /// station itself, so there is one owner of the seated state.
    /// </para>
    /// <para>
    /// The anchor is the VRCStation's own transform, the one VRChat carries a seated player
    /// with. Its <c>stationEnterPlayerLocation</c> must be moved with it and carry no authored
    /// offset: the player is put at the enter location when seating, so any offset between the
    /// two is measured as part of this rider's head offset and then clipped by the horizontal
    /// limit (the "sitting to the right of the ball" defect). The scene assembly
    /// (<c>RideUxAssembler.AssembleSeatLinks</c>) normalizes both poses onto the follow frame
    /// and reads the wiring back.
    /// </para>
    /// <para>
    /// Resets restore the seat's authored local position, so a refused or unmeasured ride
    /// leaves the scene exactly as authored rather than at a half-applied offset.
    /// </para>
    /// <para>
    /// The feet (<see cref="RideStanceMath"/>). The measured head height above the anchor, per
    /// metre of eye height, is this avatar's <c>eta</c>, which <see cref="BallRideStation"/>
    /// sizes the rider by. With a usable <c>eta</c> the seat's height is computed so that the
    /// anchor sits on the ball's lowest point (the ball is read from <c>motor</c> each frame;
    /// its bottom relative to the rig is the only moving input), while its horizontal position
    /// still puts the head on the eye point. Without one (a seated pose, no eye height to scale
    /// by) the seat only aligns the eye, from the head offset measured at the start.
    /// </para>
    /// <para>
    /// The hands. <see cref="GetHandGapMeters"/> reads the local player's tracked hands (the
    /// controllers in VR) against the ball's surface: the live check that the stance puts the
    /// ball within reach. It is a reading, nothing is moved by it.
    /// </para>
    /// <para>
    /// A rider whose size follows the ball (RIDE_GEOMETRY.md section 3) is measured ONCE, at the
    /// eye height it had then, and from then on the seat is computed, not measured: VRChat
    /// scales the avatar's root, so the seated offsets are that one measurement per metre of
    /// eye height times the current eye height (<see cref="ApplyEyeHeight"/>). That keeps the
    /// "measure once, then frozen" rule that makes VR comfortable (a seat that chased the head
    /// every frame would drag the world around) while the person grows; nothing the player's
    /// head does after the sampling window reaches the seat. The limits and the plausibility
    /// rail are scaled by the same factor, because a 15 m rider's head is 15 m above the
    /// anchor. The proportionality is an inference to verify on a real client
    /// (<see cref="GetCurrentEyeErrorMeters"/> reads the live error).
    /// </para>
    /// <para>
    /// Timing: the head and the hands are read in <c>PostLateUpdate</c>, which VRChat runs after
    /// the IK solve, so the readings are not a frame old; the seat is written there too.
    /// Only the local player is read.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class SeatEyeCompensation : UdonSharpBehaviour
    {
        // The transform the motor writes the eye point to (BallMotor's followRigRoot).
        [SerializeField] private Transform followRigRoot;

        // The station's own transform: the transform a VRCStation carries the seated avatar
        // with. It must sit under followRigRoot, or the rider would not be carried by the rig.
        // Deliberately NOT optional: a wrong default here would move the eye point itself.
        [SerializeField] private Transform seatAnchor;

        // The ball whose lowest point the feet are placed on. Unwired, the feet cannot be placed
        // and the seat only aligns the eye.
        [SerializeField] private KatamariBallMotor motor;

        // Frames to wait after the sit before sampling, then frames to average. Sitting IK,
        // the station placement and the avatar load are not settled on the sit frame.
        [SerializeField] private int settleFrames = 12;
        [SerializeField] private int sampleFrames = 8;

        // Smoothing of the applied offset, the project's usual 1 - exp(-2*pi*hz*dt). A moving
        // seat is a moving viewpoint; 8 Hz is a short glide, not a snap, and matches the
        // comfort-level filtering the follow frame itself uses.
        [SerializeField] private float filterHz = 8f;

        // Guards, not tuning, authored for a rider of SeatEyeMath.ReferenceEyeHeightMeters and
        // scaled with the eye height. The station is a standing one, so the head is about one eye
        // height above the anchor and must be compensated in full: the vertical limit is the same
        // number as the plausibility rail (SeatEyeMath.MaxPlausibleHeadAboveSeatMeters), which is
        // what catches a datum that is not the rider's head. With a measured stance the vertical
        // is not limited at all (the feet go to the ground); the limit only bounds the eye-only
        // fallback.
        [SerializeField] private float verticalLimitMeters = 2.1f;
        [SerializeField] private float horizontalLimitMeters = 0.35f;

        // A scene that does not trust the measured horizontal offset can keep the authored
        // backward seat position and compensate height only.
        [SerializeField] private bool verticalOnly = false;

        // Safety cap on the glide, so a filter that cannot converge cannot keep writing.
        [SerializeField] private int applyFrameLimit = 90;

        // Progress lines; refusals are logged regardless (a refused compensation is invisible
        // otherwise, and the seat would silently stay on the eye point).
        [SerializeField] private bool logResults = true;

        private const int StateIdle = 0;
        private const int StateSettling = 1;
        private const int StateSampling = 2;
        private const int StateApplying = 3;
        private const int StateLocked = 4;

        // This behaviour's own refusals (distinct from SeatEyeMath's reason table): 0 none,
        // 1 no rig root, 2 no seat anchor, 3 the seat is not under the rig, 4 no local player,
        // 5 no usable head datum, 6 the head datum is implausibly far from the seat,
        // 7 SeatEyeMath refused (its own reason is logged alongside).
        private const int RefusalNone = 0;
        private const int RefusalNoRigRoot = 1;
        private const int RefusalNoSeatAnchor = 2;
        private const int RefusalSeatNotUnderRig = 3;
        private const int RefusalNoLocalPlayer = 4;
        private const int RefusalNoHeadDatum = 5;
        private const int RefusalImplausibleHeadDatum = 6;
        private const int RefusalMath = 7;

        // A head further from the seat than the plausibility rail is not a SEATED head, so the
        // whole correction is refused rather than applied halfway: half of a wrong correction
        // lands the camera on the wrong side of the eye point, worse than the defect it was
        // meant to fix. The rail, its measured evidence (ClientSim 1.204 m, real hardware about
        // 0.85 m) and the reason it is a guard rather than a product value live with the shared
        // rule in SeatEyeMath.MaxPlausibleHeadAboveSeatMeters.

        // The glide is done when the applied offset is this close to its target.
        private const float SettledToleranceMeters = 0.001f;

        private int state;
        private int frameCounter;
        private int sampleCount;
        private int sampleFailures;
        private int applyFrames;

        private Vector3 sampleSum;
        private Vector3 measuredHeadInRigMeters;
        private Vector3 seatOffsetMeters;
        private Vector3 authoredLocalPosition;

        private float measuredEyeAboveSeatMeters;
        private float uncompensatedMeters;
        private float lastEyeErrorMeters;

        private int lastRefusal;
        private int startRefusal;

        // Rider size. currentEyeHeight is what the station last told us the avatar's eye height
        // is (zero: unscaled, absolute limits). The measurement is taken at measureEyeHeight; the
        // seat at another eye height scales the measured offsets by the ratio.
        private float currentEyeHeight;
        private float measureEyeHeight;
        private bool hasMeasuredEyeHeight;

        // The stance: eta is the measured head height above the anchor per metre of eye height,
        // and stanceReason says why it is not used (RideStanceMath.ReasonOk when it is).
        private bool hasStance;
        private float eta;
        private int stanceReason;

        private VRCPlayerApi localPlayer;

        private void Start()
        {
            state = StateIdle;
            hasStance = false;
            eta = 0f;
            stanceReason = RideStanceMath.ReasonOk;
            frameCounter = 0;
            sampleCount = 0;
            sampleFailures = 0;
            applyFrames = 0;
            sampleSum = Vector3.zero;
            measuredHeadInRigMeters = Vector3.zero;
            seatOffsetMeters = Vector3.zero;
            measuredEyeAboveSeatMeters = 0f;
            uncompensatedMeters = 0f;
            lastEyeErrorMeters = 0f;
            currentEyeHeight = 0f;
            measureEyeHeight = 0f;
            hasMeasuredEyeHeight = false;
            lastRefusal = RefusalNone;
            startRefusal = RefusalNone;
            localPlayer = Networking.LocalPlayer;

            if (settleFrames < 0) settleFrames = 0;
            if (sampleFrames < 1) sampleFrames = 1;
            if (applyFrameLimit < 1) applyFrameLimit = 1;
            if (filterHz < 0f) filterHz = 0f;

            if (followRigRoot == null) { startRefusal = RefusalNoRigRoot; }
            else if (seatAnchor == null) { startRefusal = RefusalNoSeatAnchor; }
            else if (!IsUnderRig(seatAnchor)) { startRefusal = RefusalSeatNotUnderRig; }

            if (startRefusal != RefusalNone)
            {
                // Refusals always log: without a line the seat would silently stay on the eye
                // point and the defect would come back as "the fix does nothing".
                Debug.Log("[seat] compensation refused reason=" + startRefusal +
                    " (no measurement, the seat stays as authored)");
                return;
            }

            authoredLocalPosition = seatAnchor.localPosition;
            if (logResults) Debug.Log("[seat] ready");
        }

        /// <summary>
        /// The local player sat down (or changed avatar while seated): measure again.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="BallRideStation"/>, which owns the seated state. Calling it
        /// again while seated restarts the measurement, and the glide starts from the offset
        /// already applied, so a re-measure cannot snap the rider.
        /// </remarks>
        public void BeginSeated()
        {
            if (startRefusal != RefusalNone)
            {
                Debug.Log("[seat] compensation refused reason=" + startRefusal +
                    " (no measurement, the seat stays as authored)");
                return;
            }

            if (localPlayer == null) localPlayer = Networking.LocalPlayer;
            if (localPlayer == null) { Refuse(RefusalNoLocalPlayer, "begin"); return; }

            Vector3 current;
            if (!TryReadSeatOffset(out current))
            {
                Refuse(RefusalMath, "begin");
                return;
            }

            // Start the glide from wherever the seat is now (the authored pose on the first
            // ride, the last offset on a re-measure), never from the rig origin.
            seatOffsetMeters = current;
            frameCounter = 0;
            sampleCount = 0;
            sampleFailures = 0;
            applyFrames = 0;
            sampleSum = Vector3.zero;
            measuredHeadInRigMeters = Vector3.zero;
            measuredEyeAboveSeatMeters = 0f;
            uncompensatedMeters = 0f;
            lastEyeErrorMeters = 0f;
            hasMeasuredEyeHeight = false;
            hasStance = false;
            eta = 0f;
            stanceReason = RideStanceMath.ReasonOk;
            state = StateSettling;

            if (logResults)
            {
                Debug.Log("[seat] measuring: settleFrames=" + settleFrames +
                    " sampleFrames=" + sampleFrames + " verticalOnly=" + verticalOnly);
            }
        }

        /// <summary>
        /// The local player is no longer in this seat (exit or respawn): put the seat back
        /// where the scene authored it and forget the measurement.
        /// </summary>
        public void EndSeated()
        {
            // Not guarded on the state: a ride whose measurement was refused part-way may
            // still have moved the seat, so the authored pose is restored unconditionally.
            bool leftEarly = state == StateSettling || state == StateSampling ||
                state == StateApplying;
            state = StateIdle;
            frameCounter = 0;
            sampleCount = 0;
            sampleFailures = 0;
            applyFrames = 0;
            sampleSum = Vector3.zero;
            measuredHeadInRigMeters = Vector3.zero;
            seatOffsetMeters = Vector3.zero;
            currentEyeHeight = 0f;
            measureEyeHeight = 0f;
            hasMeasuredEyeHeight = false;
            hasStance = false;
            eta = 0f;
            stanceReason = RideStanceMath.ReasonOk;
            if (seatAnchor != null && startRefusal == RefusalNone)
            {
                seatAnchor.localPosition = authoredLocalPosition;
            }
            if (logResults && leftEarly)
            {
                Debug.Log("[seat] measurement dropped (left the seat before it settled)");
            }
            else if (logResults)
            {
                Debug.Log("[seat] seat restored to authored offset");
            }
        }

        /// <summary>The offset currently written to the seat, in the rig frame.</summary>
        public Vector3 GetSeatOffsetMeters()
        {
            return seatOffsetMeters;
        }

        /// <summary>How much of the measured eye error the limits deliberately left in place.</summary>
        public float GetUncompensatedMeters()
        {
            return uncompensatedMeters;
        }

        /// <summary>The state machine's current state, for a lab probe (0 idle, 4 locked).</summary>
        public int GetState()
        {
            return state;
        }

        /// <summary>The last refusal code, 0 when nothing was refused.</summary>
        public int GetLastRefusal()
        {
            return lastRefusal;
        }

        /// <summary>
        /// Tells the seat the avatar's eye height, metres (zero: the avatar is not scaled by
        /// this world, absolute limits). Call it BEFORE <see cref="BeginSeated"/> so the
        /// measurement knows what size it was taken at, and do not change the eye height while
        /// <see cref="IsMeasuring"/>: a measurement across a size change is meaningless.
        /// </summary>
        public void SetEyeHeight(float eyeHeightMeters)
        {
            if (!KatamariMath.IsFiniteFloat(eyeHeightMeters) || eyeHeightMeters <= 0f)
            {
                currentEyeHeight = 0f;
                return;
            }
            currentEyeHeight = eyeHeightMeters;
        }

        /// <summary>True from <see cref="BeginSeated"/> until the seat has locked (or refused).</summary>
        public bool IsMeasuring()
        {
            return state == StateSettling || state == StateSampling || state == StateApplying;
        }

        /// <summary>True once a measurement has locked and the seat can follow the eye height.</summary>
        public bool IsTracking()
        {
            return state == StateLocked && hasMeasuredEyeHeight;
        }

        /// <summary>
        /// True when the locked measurement has a usable standing pose: <see cref="GetEta"/> is
        /// this avatar's number and the seat puts its feet on the ground. False means the seat
        /// only aligns the eye.
        /// </summary>
        public bool HasStance()
        {
            return state == StateLocked && hasStance;
        }

        /// <summary>
        /// The measured head height above the anchor per metre of eye height
        /// (<see cref="RideStanceMath"/>); zero without a stance.
        /// </summary>
        public float GetEta()
        {
            return hasStance ? eta : 0f;
        }

        /// <summary>
        /// Why the stance is not used (a <c>RideStanceMath.Reason*</c> code); zero while it is or
        /// before a measurement locked.
        /// </summary>
        public int GetStanceReason()
        {
            return stanceReason;
        }

        /// <summary>
        /// The avatar's eye height changed (the station just asked VRChat for it): move the
        /// seat to where the one measurement says the seat must be at this size, immediately.
        /// False when there is no measurement yet to scale.
        /// </summary>
        public bool ApplyEyeHeight(float eyeHeightMeters)
        {
            if (!IsTracking()) return false;
            if (!KatamariMath.IsFiniteFloat(eyeHeightMeters) || eyeHeightMeters <= 0f) return false;
            currentEyeHeight = eyeHeightMeters;
            float residual;
            return SolveSeat(eyeHeightMeters, 0f, 0f, true, out residual);
        }

        /// <summary>
        /// The distance from the local head to the eye point right now, metres; negative when
        /// the head datum cannot be read. The live check of the uniform-scaling assumption: it
        /// should stay near zero while the rider grows, and the number an acceptance run logs.
        /// </summary>
        public float GetCurrentEyeErrorMeters()
        {
            if (startRefusal != RefusalNone || followRigRoot == null) return -1f;
            Vector3 headWorld;
            if (!TryReadHeadPosition(out headWorld)) return -1f;
            return Vector3.Distance(headWorld, followRigRoot.position);
        }

        /// <summary>
        /// The nearer tracked hand's distance from the ball's surface right now, metres:
        /// negative inside the ball, zero touching it, positive infinity when no hand or no
        /// ball can be read. In VR the hands are the controllers, so this is
        /// the number that says whether the player can push the ball; on desktop it is the
        /// avatar's animated hands. Read it in or after <c>PostLateUpdate</c>.
        /// </summary>
        public float GetHandGapMeters()
        {
            if (localPlayer == null || motor == null) return Mathf.Infinity;
            Vector3 center = motor.GetCenterWorld();
            float radius = motor.GetSemanticDiameterWorldM() * 0.5f;
            float best = Mathf.Infinity;
            VRCPlayerApi.TrackingData left =
                localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand);
            if (IsUsableDatum(left.position))
            {
                best = Mathf.Min(best, RideViewMath.HandGapMeters(left.position, center, radius));
            }
            VRCPlayerApi.TrackingData right =
                localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand);
            if (IsUsableDatum(right.position))
            {
                best = Mathf.Min(best, RideViewMath.HandGapMeters(right.position, center, radius));
            }
            return best;
        }

        // PostLateUpdate, not Update or LateUpdate: VRChat runs it after the IK solve, so the
        // head and the hands are read at this frame's pose; the motor writes the rig pose in
        // Update, so the rig is at this frame's eye point before the seat is placed from it.
        public override void PostLateUpdate()
        {
            if (state == StateIdle) return;
            if (startRefusal != RefusalNone) return;

            if (state == StateLocked)
            {
                // The measurement is frozen; only the ball's lowest point moves the seat.
                if (!hasStance) return;
                float residual;
                SolveSeat(currentEyeHeight, 0f, 0f, true, out residual);
                return;
            }

            if (state == StateSettling)
            {
                frameCounter = frameCounter + 1;
                if (frameCounter < settleFrames) return;
                state = StateSampling;
                return;
            }

            if (state == StateSampling)
            {
                SampleOnce();
                return;
            }

            if (state == StateApplying)
            {
                ApplyOnce();
            }
        }

        private void SampleOnce()
        {
            Vector3 headWorld;
            if (!TryReadHeadPosition(out headWorld))
            {
                // A missing datum on one frame is not a refusal: the tracking datum can be
                // absent for a moment after a sit or an avatar load. A sustained absence is.
                sampleFailures = sampleFailures + 1;
                if (sampleFailures > sampleFrames * 4) Refuse(RefusalNoHeadDatum, "sample");
                return;
            }

            Vector3 headInRig;
            Vector3 headInSeat;
            int reason;
            if (!SeatEyeMath.TrySampleHeadOffset(headWorld, seatAnchor.position,
                seatAnchor.rotation, followRigRoot.rotation, out headInRig, out headInSeat, out reason))
            {
                sampleFailures = sampleFailures + 1;
                if (sampleFailures > sampleFrames * 4) Refuse(RefusalMath, "sample:" + reason);
                return;
            }

            float plausibility = Mathf.Sqrt(headInRig.x * headInRig.x +
                headInRig.y * headInRig.y + headInRig.z * headInRig.z);
            if (!SeatEyeMath.IsPlausibleHeadDistanceForEyeHeight(plausibility, currentEyeHeight))
            {
                // Refused instead of clamped: this is the wrong datum, and a clamped offset
                // from a wrong datum is still a wrong offset.
                Refuse(RefusalImplausibleHeadDatum, "sample:distance=" + plausibility);
                return;
            }

            sampleSum = sampleSum + headInRig;
            sampleCount = sampleCount + 1;
            measuredEyeAboveSeatMeters = headInSeat.y;
            if (sampleCount < sampleFrames) return;

            Vector3 mean;
            if (!SeatEyeMath.TryMeanHeadOffset(sampleSum, sampleCount, out mean, out reason))
            {
                Refuse(RefusalMath, "mean:" + reason);
                return;
            }
            measuredHeadInRigMeters = mean;
            measureEyeHeight = currentEyeHeight;
            hasMeasuredEyeHeight = measureEyeHeight > 0f;
            ResolveStance();
            applyFrames = 0;
            state = StateApplying;
        }

        // Decides, once per measurement, whether the standing pose can be used; the answer is
        // final until the next BeginSeated.
        private void ResolveStance()
        {
            hasStance = false;
            eta = 0f;
            stanceReason = RideStanceMath.ReasonOk;
            if (motor == null)
            {
                stanceReason = RideStanceMath.ReasonNoBall;
            }
            else
            {
                float measured;
                int reason;
                if (RideStanceMath.TryMeasureEta(measuredHeadInRigMeters,
                    hasMeasuredEyeHeight ? measureEyeHeight : 0f, out measured, out reason))
                {
                    eta = measured;
                    hasStance = true;
                }
                else
                {
                    stanceReason = reason;
                }
            }
            if (!hasStance && logResults)
            {
                Debug.Log("[seat] no standing stance (reason=" + stanceReason
                    + "): the seat only aligns the eye");
            }
        }

        private void ApplyOnce()
        {
            // A zero hertz means "no glide" HERE: snap to the target instead of holding the
            // authored seat forever. (SeatEyeMath's own 0 Hz is a hold, which is the right
            // primitive; this behaviour's knob means "how smooth", so 0 has to mean "instant".)
            bool snap = filterHz <= 0f;
            // Sampling is over: the measurement is frozen, so a VR head that keeps moving
            // cannot drag the seat (and the world) around every frame.
            float residual;
            if (!SolveSeat(measureEyeHeight, filterHz, Time.deltaTime, snap, out residual)) return;

            applyFrames = applyFrames + 1;
            if (residual <= SettledToleranceMeters || applyFrames >= applyFrameLimit)
            {
                Lock();
            }
        }

        // The one place that turns the frozen measurement into a seat offset: the measured head
        // scaled to the current eye height, the vertical placed on the ball's lowest point when
        // there is a stance, then SeatEyeMath's limited, low-passed solution, written to the seat.
        private bool SolveSeat(float eyeHeightMeters, float hertz, float dtSeconds, bool reset,
            out float residualMeters)
        {
            residualMeters = 0f;
            float factor = 1f;
            float limitScale = 1f;
            if (hasMeasuredEyeHeight && eyeHeightMeters > 0f)
            {
                factor = eyeHeightMeters / measureEyeHeight;
                limitScale = SeatEyeMath.LimitScaleForEyeHeight(eyeHeightMeters);
            }
            Vector3 head = measuredHeadInRigMeters * factor;
            float verticalLimit = verticalLimitMeters * limitScale;
            Vector3 solveFor = head;
            if (hasStance)
            {
                if (motor == null) return false;
                float feet = RideStanceMath.FeetTargetInRig(motor.GetCenterWorld().y,
                    motor.GetSemanticDiameterWorldM() * 0.5f, followRigRoot.position.y);
                solveFor = RideStanceMath.EffectiveHeadInRig(head, true, feet);
                verticalLimit = RideStanceMath.UnlimitedMeters;
            }

            Vector3 applied;
            float uncompensated;
            float residual;
            int reason;
            if (!SeatEyeMath.TryComputeSeatOffset(solveFor, seatOffsetMeters, verticalLimit,
                horizontalLimitMeters * limitScale, verticalOnly, hertz, dtSeconds, reset,
                out applied, out uncompensated, out residual, out reason))
            {
                Refuse(RefusalMath, "apply:" + reason);
                return false;
            }

            seatOffsetMeters = applied;
            uncompensatedMeters = uncompensated;
            residualMeters = residual;
            WriteSeat();
            return true;
        }

        private void Lock()
        {
            state = StateLocked;
            Vector3 headWorld;
            if (TryReadHeadPosition(out headWorld))
            {
                lastEyeErrorMeters = Vector3.Distance(headWorld, followRigRoot.position);
            }
            if (logResults)
            {
                Debug.Log("[seat] locked: eyeAboveSeat=" + measuredEyeAboveSeatMeters +
                    "m offset=(" + seatOffsetMeters.x + "," + seatOffsetMeters.y + "," +
                    seatOffsetMeters.z + ") left=" + uncompensatedMeters + "m eyeError=" +
                    lastEyeErrorMeters + "m frames=" + applyFrames);
            }
        }

        private void WriteSeat()
        {
            // World position, not localPosition: the offset is defined in the rig frame, and
            // resolving it through the parent chain means an intermediate rotated or scaled
            // node cannot turn the correction into a different direction.
            seatAnchor.position = followRigRoot.position + followRigRoot.rotation * seatOffsetMeters;
        }

        private bool TryReadSeatOffset(out Vector3 offsetMeters)
        {
            offsetMeters = Vector3.zero;
            if (followRigRoot == null || seatAnchor == null) return false;
            Vector3 delta = seatAnchor.position - followRigRoot.position;
            if (!KatamariMath.IsFiniteVector(delta)) return false;
            Vector3 offset = Quaternion.Inverse(followRigRoot.rotation) * delta;
            if (!KatamariMath.IsFiniteVector(offset)) return false;
            offsetMeters = offset;
            return true;
        }

        // The head datum is the local player's tracking head: VRChat's recommended "where the
        // player's head is" API, which for the local player comes straight from the tracking
        // manager and is the datum the avatar's own camera is placed from. The avatar's Head
        // BONE is deliberately not used as a fallback: it is documented to differ wildly
        // between avatars and to return (0,0,0) when the bone is missing, which is exactly the
        // kind of silent wrong number this layer must not compensate for.
        private bool TryReadHeadPosition(out Vector3 headWorldPosition)
        {
            headWorldPosition = Vector3.zero;
            if (localPlayer == null) return false;

            VRCPlayerApi.TrackingData headData =
                localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            Vector3 position = headData.position;
            if (!IsUsableDatum(position)) return false;
            headWorldPosition = position;
            return true;
        }

        // An unimplemented datum reports the default struct, i.e. exactly the origin. A real
        // head or hand is never at the origin in an ordinary world, and refusing is the safe
        // side: a wrong offset is worse than no offset.
        private bool IsUsableDatum(Vector3 position)
        {
            if (!KatamariMath.IsFiniteVector(position)) return false;
            return position.x != 0f || position.y != 0f || position.z != 0f;
        }

        // The seat has to be carried by the rig (that is how the rider follows the ball at
        // all), and it must not BE the rig: writing that transform would move the eye point
        // this whole layer exists to keep still.
        private bool IsUnderRig(Transform candidate)
        {
            if (candidate == null || followRigRoot == null) return false;
            if (candidate == followRigRoot) return false;
            Transform cursor = candidate.parent;
            for (int i = 0; i < 8; i++)
            {
                if (cursor == null) return false;
                if (cursor == followRigRoot) return true;
                cursor = cursor.parent;
            }
            return false;
        }

        private void Refuse(int code, string stage)
        {
            lastRefusal = code;
            // The last good offset is left in place on purpose: the rider is still sitting, so
            // a half-applied correction beats snapping the seat back mid-ride. EndSeated
            // restores the authored pose on the way out.
            state = StateIdle;
            Debug.Log("[seat] compensation refused reason=" + code + " stage=" + stage);
        }
    }
}
