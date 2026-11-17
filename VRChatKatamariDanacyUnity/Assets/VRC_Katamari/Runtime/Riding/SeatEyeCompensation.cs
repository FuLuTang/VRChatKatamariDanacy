using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// Puts the seated rider's EYE on the follow frame's eye point, and their WAIST on the top
    /// of the ball, by measuring the seated head and hips against the seat once per ride.
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
    /// Resets restore the seat's authored local position, so a refused or unmeasured ride
    /// leaves the scene exactly as authored rather than at a half-applied offset.
    /// </para>
    /// <para>
    /// The waist (<see cref="RideWaistMath"/>). The same sampling window also reads the
    /// Hips bone, which gives this avatar's <c>kappa</c> (head-to-hips height per metre of eye
    /// height) for <see cref="BallRideStation"/> to size the rider by. With a usable waist the
    /// seat's height is computed so that the hips sit on the top of the ball (the ball is read
    /// from <c>motor</c> each frame; its top relative to the rig is the only moving input),
    /// while its horizontal position still puts the head on the eye point. Without one - the
    /// hips unreadable, implausible, or no eye height to scale by - the seat only aligns the
    /// eye, from the head offset measured at sit-down.
    /// </para>
    /// <para>
    /// A rider whose size follows the ball (RIDE_GEOMETRY.md section 3) is measured ONCE, at the
    /// eye height it had then, and from then on the seat is computed, not measured: VRChat
    /// scales the avatar's root, so the seated offsets are that one measurement per metre of
    /// eye height times the current eye height (<see cref="ApplyEyeHeight"/>). That keeps the
    /// "measure once, then frozen" rule that makes VR comfortable (a seat that chased the head
    /// every frame would drag the world around) while the person grows; nothing the player's
    /// head does after the sampling window reaches the seat. The limits and the plausibility
    /// rail are scaled by the same factor, because a 23 m rider's seated head is 11 m above the
    /// seat. The proportionality is an inference to verify on a real client
    /// (<see cref="GetCurrentEyeErrorMeters"/> reads the live error).
    /// </para>
    /// <para>
    /// Timing: the head and the hips are read in <c>PostLateUpdate</c>, which VRChat runs after
    /// the IK solve, so the bone positions are not a frame old; the seat is written there too.
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

        // The ball whose top the hips are placed on. Unwired, the waist cannot be placed and the
        // seat only aligns the eye.
        [SerializeField] private KatamariBallMotor motor;

        // Frames to wait after the sit before sampling, then frames to average. Sitting IK,
        // the station placement and the avatar load are not settled on the sit frame.
        [SerializeField] private int settleFrames = 12;
        [SerializeField] private int sampleFrames = 8;

        // Smoothing of the applied offset, the project's usual 1 - exp(-2*pi*hz*dt). A moving
        // seat is a moving viewpoint; 8 Hz is a short glide, not a snap, and matches the
        // comfort-level filtering the follow frame itself uses.
        [SerializeField] private float filterHz = 8f;

        // Guards, not tuning: a seated eye height is roughly 0.4-0.9 m on real hardware, so this
        // is a rail just past a tall seated avatar, not a value the product needs. 1.3 m is chosen
        // to pair with the plausibility rail: the tallest head this project has measured is
        // ClientSim's 1.204159 m (2026-11-15) and it must be compensated in FULL, because a clamp
        // below a real measurement leaves the camera visibly high. The plausibility check in
        // SampleOnce is what catches a datum near a standing eye height - clamping that one would
        // land the camera on the wrong side of the eye point. What the extra headroom costs is
        // third person only: the avatar's lower body sinks deeper into the ground. It never moves
        // the local player's own viewpoint, which is the thing being corrected.
        [SerializeField] private float verticalLimitMeters = 1.3f;
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
        private Vector3 hipsSampleSum;
        private int hipsSampleCount;
        private bool hipsBroken;
        private Vector3 measuredHeadInRigMeters;
        private Vector3 measuredHipsInRigMeters;
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

        // The waist: kappa is the measured head-to-hips height per metre of eye height, and
        // waistReason says why the hips are not used (RideWaistMath.ReasonOk when they are).
        private bool hasWaist;
        private float kappa;
        private int waistReason;

        private VRCPlayerApi localPlayer;

        private void Start()
        {
            state = StateIdle;
            hipsSampleSum = Vector3.zero;
            hipsSampleCount = 0;
            hipsBroken = false;
            measuredHipsInRigMeters = Vector3.zero;
            hasWaist = false;
            kappa = 0f;
            waistReason = RideWaistMath.ReasonOk;
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
            hipsSampleSum = Vector3.zero;
            hipsSampleCount = 0;
            hipsBroken = false;
            measuredHeadInRigMeters = Vector3.zero;
            measuredHipsInRigMeters = Vector3.zero;
            measuredEyeAboveSeatMeters = 0f;
            uncompensatedMeters = 0f;
            lastEyeErrorMeters = 0f;
            hasMeasuredEyeHeight = false;
            hasWaist = false;
            kappa = 0f;
            waistReason = RideWaistMath.ReasonOk;
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
            hipsSampleSum = Vector3.zero;
            hipsSampleCount = 0;
            hipsBroken = false;
            measuredHeadInRigMeters = Vector3.zero;
            measuredHipsInRigMeters = Vector3.zero;
            seatOffsetMeters = Vector3.zero;
            currentEyeHeight = 0f;
            measureEyeHeight = 0f;
            hasMeasuredEyeHeight = false;
            hasWaist = false;
            kappa = 0f;
            waistReason = RideWaistMath.ReasonOk;
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

        /// <summary>The measured seated eye height above the seat, in metres.</summary>
        /// <remarks>Diagnostic seam for a lab probe or an acceptance run; gameplay never reads it.</remarks>
        public float GetMeasuredEyeAboveSeatMeters()
        {
            return measuredEyeAboveSeatMeters;
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

        /// <summary>
        /// The head-to-eye-point distance measured when the glide locked, in metres; the
        /// number an acceptance run checks against ~0.
        /// </summary>
        public float GetLastEyeErrorMeters()
        {
            return lastEyeErrorMeters;
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
        /// True when the locked measurement has a usable waist: <see cref="GetKappa"/> is this
        /// avatar's number and the seat puts its hips on the top of the ball. False means the
        /// seat only aligns the eye and the rider's size is not to be changed.
        /// </summary>
        public bool HasWaist()
        {
            return state == StateLocked && hasWaist;
        }

        /// <summary>
        /// The measured head-to-hips height per metre of eye height (<see cref="RideWaistMath"/>);
        /// zero without a waist.
        /// </summary>
        public float GetKappa()
        {
            return hasWaist ? kappa : 0f;
        }

        /// <summary>
        /// Why the hips are not used (a <c>RideWaistMath.Reason*</c> code); zero while they are
        /// or before a measurement locked.
        /// </summary>
        public int GetWaistReason()
        {
            return waistReason;
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

        // PostLateUpdate, not Update or LateUpdate: VRChat runs it after the IK solve, so the
        // head and the hips are read at this frame's pose; the motor writes the rig pose in
        // Update, so the rig is at this frame's eye point before the seat is placed from it.
        public override void PostLateUpdate()
        {
            if (state == StateIdle) return;
            if (startRefusal != RefusalNone) return;

            if (state == StateLocked)
            {
                // The measurement is frozen; only the ball's top moves the seat.
                if (!hasWaist) return;
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
            SampleHips();
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
            ResolveWaist();
            applyFrames = 0;
            state = StateApplying;
        }

        // The hips are read in the same frame as the head, so both come from one IK pose. A
        // frame whose hips are unusable spoils the whole waist (hipsBroken), not just the
        // sample: the mean of a partly missing reading is not a measurement.
        private void SampleHips()
        {
            if (hipsBroken) return;
            Vector3 hipsWorld = Vector3.zero;
            if (localPlayer != null)
            {
                hipsWorld = localPlayer.GetBonePosition(HumanBodyBones.Hips);
            }
            if (localPlayer == null || !RideWaistMath.IsUsableBonePosition(hipsWorld))
            {
                MarkHipsBroken(RideWaistMath.ReasonHipsMissing);
                return;
            }
            Vector3 hipsInRig;
            Vector3 hipsInSeat;
            int reason;
            if (!SeatEyeMath.TrySampleHeadOffset(hipsWorld, seatAnchor.position,
                seatAnchor.rotation, followRigRoot.rotation, out hipsInRig, out hipsInSeat, out reason))
            {
                MarkHipsBroken(RideWaistMath.ReasonNotFinite);
                return;
            }
            float distance = Mathf.Sqrt(hipsInRig.x * hipsInRig.x + hipsInRig.y * hipsInRig.y +
                hipsInRig.z * hipsInRig.z);
            if (!SeatEyeMath.IsPlausibleHeadDistanceForEyeHeight(distance, currentEyeHeight))
            {
                MarkHipsBroken(RideWaistMath.ReasonHipsImplausible);
                return;
            }
            hipsSampleSum = hipsSampleSum + hipsInRig;
            hipsSampleCount = hipsSampleCount + 1;
        }

        private void MarkHipsBroken(int reason)
        {
            hipsBroken = true;
            waistReason = reason;
        }

        // Decides, once per measurement, whether the waist can be used; the answer is final
        // until the next BeginSeated.
        private void ResolveWaist()
        {
            hasWaist = false;
            kappa = 0f;
            if (!hipsBroken && hipsSampleCount < sampleCount) MarkHipsBroken(RideWaistMath.ReasonHipsMissing);
            if (!hipsBroken && motor == null) MarkHipsBroken(RideWaistMath.ReasonNoBall);
            if (!hipsBroken && !hasMeasuredEyeHeight) MarkHipsBroken(RideWaistMath.ReasonNoEyeHeight);
            if (!hipsBroken)
            {
                measuredHipsInRigMeters = hipsSampleSum * (1f / hipsSampleCount);
                float measured;
                int reason;
                if (RideWaistMath.TryMeasureKappa(measuredHeadInRigMeters, measuredHipsInRigMeters,
                    measureEyeHeight, out measured, out reason))
                {
                    kappa = measured;
                    hasWaist = true;
                    waistReason = RideWaistMath.ReasonOk;
                }
                else
                {
                    MarkHipsBroken(reason);
                }
            }
            if (!hasWaist && logResults)
            {
                Debug.Log("[seat] no waist (reason=" + waistReason + "): the seat only aligns the eye");
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
        // (and hips) scaled to the current eye height, the vertical placed on the ball's top when
        // there is a waist, then SeatEyeMath's limited, low-passed solution, written to the seat.
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
            if (hasWaist)
            {
                if (motor == null) return false;
                float target = RideWaistMath.WaistTargetInRig(motor.GetCenterWorld().y,
                    motor.GetSemanticDiameterWorldM() * 0.5f, followRigRoot.position.y);
                solveFor = RideWaistMath.EffectiveHeadInRig(head, measuredHipsInRigMeters * factor,
                    true, target);
                verticalLimit = RideWaistMath.UnlimitedMeters;
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
            if (!KatamariMath.IsFiniteVector(position)) return false;
            // An unimplemented datum reports the default struct, i.e. exactly the origin.
            // A real head is never at the origin in an ordinary world, and refusing is the
            // safe side: a wrong offset is worse than no offset.
            if (position.x == 0f && position.y == 0f && position.z == 0f) return false;
            headWorldPosition = position;
            return true;
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
