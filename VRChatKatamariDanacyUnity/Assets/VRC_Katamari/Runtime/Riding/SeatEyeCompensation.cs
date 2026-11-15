using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.Core;

namespace VRCKatamari.Riding
{
    /// <summary>
    /// Puts the seated rider's EYE on the follow frame's eye point by measuring the seated
    /// head above the seat once per ride and moving the seat by that much.
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

        // Frames to wait after the sit before sampling, then frames to average. Sitting IK,
        // the station placement and the avatar load are not settled on the sit frame.
        [SerializeField] private int settleFrames = 12;
        [SerializeField] private int sampleFrames = 8;

        // Smoothing of the applied offset, the project's usual 1 - exp(-2*pi*hz*dt). A moving
        // seat is a moving viewpoint; 8 Hz is a short glide, not a snap, and matches the
        // comfort-level filtering the follow frame itself uses.
        [SerializeField] private float filterHz = 8f;

        // Guards, not tuning: a seated eye height is roughly 0.4-0.9 m, so the limit is a rail
        // just past a tall seated avatar, not a value the product needs. The plausibility check
        // in SampleOnce is what catches a datum near a standing eye height - clamping that one
        // would land the camera on the wrong side of the eye point.
        [SerializeField] private float verticalLimitMeters = 1f;
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

        // A head further than this from the seat is not a SEATED head. Every seated proportion
        // measured in this project's scale sits under a metre, and a standing eye height is
        // 1.5-1.8 m, so this threshold sits in the gap: past it the DATUM is wrong (a standing
        // eye height instead of the seated head), and the correction is refused rather than
        // applied halfway, because half of a wrong correction lands the camera on the wrong
        // side of the eye point - worse than the defect it was meant to fix.
        private const float MaxPlausibleHeadAboveSeatMeters = 1.2f;

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

        private VRCPlayerApi localPlayer;

        private void Start()
        {
            state = StateIdle;
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

        // LateUpdate, not Update: the motor writes the rig pose in Update, so the rig is at
        // this frame's eye point before the seat is placed relative to it.
        private void LateUpdate()
        {
            if (state == StateIdle || state == StateLocked) return;
            if (startRefusal != RefusalNone) return;

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
            if (plausibility > MaxPlausibleHeadAboveSeatMeters)
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
            applyFrames = 0;
            state = StateApplying;
        }

        private void ApplyOnce()
        {
            Vector3 applied;
            float uncompensated;
            float residual;
            int reason;
            // A zero hertz means "no glide" HERE: snap to the target instead of holding the
            // authored seat forever. (SeatEyeMath's own 0 Hz is a hold, which is the right
            // primitive; this behaviour's knob means "how smooth", so 0 has to mean "instant".)
            bool snap = filterHz <= 0f;
            // Sampling is over: the measurement is frozen, so a VR head that keeps moving
            // cannot drag the seat (and the world) around every frame.
            if (!SeatEyeMath.TryComputeSeatOffset(measuredHeadInRigMeters, seatOffsetMeters,
                verticalLimitMeters, horizontalLimitMeters, verticalOnly, filterHz,
                Time.deltaTime, snap, out applied, out uncompensated, out residual, out reason))
            {
                Refuse(RefusalMath, "apply:" + reason);
                return;
            }

            seatOffsetMeters = applied;
            uncompensatedMeters = uncompensated;
            WriteSeat();

            applyFrames = applyFrames + 1;
            if (residual <= SettledToleranceMeters || applyFrames >= applyFrameLimit)
            {
                Lock();
            }
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
