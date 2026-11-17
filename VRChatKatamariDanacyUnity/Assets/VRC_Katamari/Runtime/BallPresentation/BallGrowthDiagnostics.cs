using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

namespace VRCKatamari.BallPresentation
{
    /// <summary>
    /// Three diagnostic readouts for "the ball is not visibly growing", all sampled from the
    /// motor's own getters at one instant and joinable by <c>t</c> and <c>absorbed</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written for the question in Docs/Design/BallPresentation/VISUAL_GROWTH_AUDIT.md
    /// section 4: the ledger provably grows the ball, the mesh is provably scaled from it,
    /// and yet the silhouette does not seem to change. Two lines read at different times, or a
    /// radius compared against a diameter, look like a disagreement, so every line below
    /// says which quantity it carries.
    /// </para>
    /// <para>
    /// <b>The three diameters, and who reads them.</b>
    /// <list type="bullet">
    /// <item><c>d_motor</c> = <c>2 * GetCollisionRadiusWorldM()</c>: the collision diameter,
    /// also used by the result board and star targets. The absorb size threshold and
    /// <c>[story] diameter</c> read the separate semantic getter. Both ratios are currently 1,
    /// so these diameters agree. HUD adds 0.0002 m; it is not an exact mesh-size gauge.</item>
    /// <item><c>d_vis</c> = <c>2 * GetCollisionRadiusWorldM()</c>: the current
    /// collision sphere diameter, which BallView writes whenever it changes. The normalized
    /// skin has the same equivalent-volume diameter; its bumpy AABB is not a diameter gauge.</item>
    /// <item><c>r_core</c> and <c>r_shell</c> are RADII. Mixing them with the diameters above
    /// reads as a false disagreement (a reported <c>core=0.6232</c> is a
    /// 1.312 m ball), so the keys carry their unit: <c>r_</c> for radii, <c>d_</c> for
    /// diameters.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>The lines.</b> Field order is fixed so a line can be quoted field by field; the two
    /// join keys (<c>t</c>, <c>absorbed</c>) come first on every line. <c>t</c> is this
    /// component's own clock since it first saw a started ball, the same construction
    /// <c>[story]</c> uses, so two components' clocks agree to within the frame that started
    /// them; <c>absorbed</c> is the motor's exact absorb count and is the reliable join key
    /// against <c>[story] absorbed=</c>.
    /// <list type="bullet">
    /// <item><c>[ballview] t= absorbed= mesh= d_vis= d_motor=</c> - <c>mesh</c> is measured
    /// through <c>lossyScale</c>, i.e. what the renderer gets, not what this script intended.
    /// <c>mesh</c> must match <c>d_vis</c> after BallView updates (within 0.1 mm).
    /// Unity does not order these Updates, so sample after both have run.</item>
    /// <item><c>[shell] t= absorbed= r_core= r_shell= swallowed= attached= d_motor=</c> -
    /// props are frozen at their contact pose and never pushed outward, so this measures
    /// whether the visible outer layer still sticks out of the core. <c>r_shell</c> is the
    /// furthest attached prop CENTRE, not an outer surface: one long object absorbed at its
    /// end keeps its world pose and sits far out (e.g. 1.2211 m on a 0.656 m ball), so
    /// <c>r_core / r_shell</c> is a LOWER bound on the visible fraction, dominated by the
    /// longest prop. <c>swallowed</c> counts prop centres inside the growing core, not
    /// fully hidden meshes; frozen older attachments can be engulfed as it grows. A centre
    /// distance needs no renderer lookup; the runtime never calls GetComponent.</item>
    /// <item><c>[eye] t= absorbed= app= d_core= dist=</c> - <c>app</c> is the core's apparent
    /// size (radians), <c>d_core</c> its diameter and <c>dist</c> the distance from the local
    /// player's head. It shrinks when the follow rig pulls the eye back with the radius, which
    /// is the audit's second engine. <c>app=none</c> means the head datum was unreadable (no
    /// local player, or a client that does not implement tracking), so there is no eye to
    /// measure from.</item>
    /// <item><c>[grow] t= absorbed= units= D= -&gt; (+x.x%)</c> - one line per absorbed
    /// batch, with the ledger's exact integer unit delta and the semantic diameter before and
    /// after. A frame that absorbs several props emits ONE line with the summed units, so
    /// <c>D</c> spans all of them. <c>before</c> is the diameter read on the previous frame
    /// (Unity does not order two behaviours' Updates), which is at most one frame plus the
    /// motor's own steps stale; the evidence for that being small is that the next
    /// <c>[story]</c> line reports the same <c>after</c> value.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Cost: the whole component is behind one serialized bool, and the first statement of
    /// <c>Update</c> is the return for it, so a disabled instance builds no strings, calls no
    /// motor getter and touches no transform. Enabled, it reports at most a few times a
    /// second (<c>logIntervalSeconds</c>, 0.5 s by default) with no allocation: the loops
    /// read transforms and numbers, and the only allocations are the log lines themselves.
    /// </para>
    /// <para>
    /// Read-only: it never writes a transform, never calls into the motor's setters and holds
    /// no gameplay state. It is a scene-side probe, so it lives beside the ball rather than in
    /// the motor.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BallGrowthDiagnostics : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;

        [Tooltip("The CoreMesh child BallView scales. Measured through lossyScale, so it reports the transform's real world size.")]
        [SerializeField] private Transform coreMesh;

        [Tooltip("The AttachmentRoot the pickup host parents absorbed props to. May be left empty: the shell line then reports the bare core.")]
        [SerializeField] private Transform attachmentRoot;

        [Header("Reporting")]
        [Tooltip("Off by default: each report builds three strings. Turn on for one diagnostic Play, then turn it back off.")]
        [SerializeField] private bool loggingEnabled = false;

        [Tooltip("Seconds between report lines. A non-positive value falls back to 0.5 s. Lower it to 0.1 s for one Play to follow a fast absorb sequence.")]
        [SerializeField] private float logIntervalSeconds = 0.5f;

        private bool ready;
        private float elapsed;
        private float nextLogTime;

        // Change detection for the per-absorb line. Both are seeded from the motor on the
        // first ready frame, so a ball that started before this component did not report the
        // absorbs that already happened as one giant line.
        private int lastAbsorbedCount;
        private long lastConfirmedUnits;
        private float lastDiameterM;

        public void Update()
        {
            // The whole feature, including every string it would build, is behind this.
            if (!loggingEnabled) return;

            if (!ready)
            {
                if (!MotorReady()) return;
                ready = true;
                elapsed = 0f;
                nextLogTime = 0f;
                lastAbsorbedCount = motor.GetAbsorbedCount();
                lastConfirmedUnits = motor.GetConfirmedLedgerVolumeUnits();
                lastDiameterM = motor.GetSemanticDiameterWorldM();
            }

            elapsed = elapsed + Time.deltaTime;
            TrackGrowth();

            // A scene author who types 0 must not turn this into a per-frame log; the on/off
            // switch is loggingEnabled, not the interval.
            float interval = logIntervalSeconds;
            if (interval <= 0f) interval = 0.5f;

            if (elapsed < nextLogTime) return;
            nextLogTime = elapsed + interval;
            Report();
        }

        /// <summary>True once the motor reports a started ball (diameter above zero).</summary>
        private bool MotorReady()
        {
            if (motor == null || coreMesh == null) return false;
            return motor.GetSemanticDiameterWorldM() > 0f;
        }

        /// <summary>
        /// One line per absorbed batch: the ledger units gained, the semantic diameter before
        /// and after, and the relative change.
        /// </summary>
        /// <remarks>
        /// The delta is the LEDGER's integer column (<c>GetConfirmedLedgerVolumeUnits()</c>,
        /// mm^3 since steps C+D), not a float reading: the exact difference of two longs is
        /// exact, while the float getters are display helpers. A log that read a float
        /// would silently change unit without changing its name.
        /// </remarks>
        private void TrackGrowth()
        {
            int absorbedCount = motor.GetAbsorbedCount();
            float diameterM = motor.GetSemanticDiameterWorldM();
            if (absorbedCount == lastAbsorbedCount)
            {
                lastDiameterM = diameterM;
                return;
            }

            long confirmedUnits = motor.GetConfirmedLedgerVolumeUnits();
            long gainedUnits = confirmedUnits - lastConfirmedUnits;
            float beforeM = lastDiameterM;
            if (!(beforeM > 0f)) beforeM = diameterM;
            float percent = 0f;
            if (beforeM > 0f) percent = (diameterM - beforeM) / beforeM * 100f;
            string sign = "";
            if (percent >= 0f) sign = "+";

            Debug.Log("[grow] t=" + ToTenth(elapsed)
                + " absorbed=" + absorbedCount
                + " units=" + gainedUnits
                + " D=" + ToTenThousandth(beforeM)
                + " -> " + ToTenThousandth(diameterM)
                + " (" + sign + ToTenth(percent) + "%)");

            lastAbsorbedCount = absorbedCount;
            lastConfirmedUnits = confirmedUnits;
            lastDiameterM = diameterM;
        }

        /// <summary>The three report lines, one frame's worth of reads.</summary>
        private void Report()
        {
            int absorbedCount = motor.GetAbsorbedCount();
            float visualCoreRadiusM = motor.GetCollisionRadiusWorldM();
            float physicalDiameterM = 2f * visualCoreRadiusM;
            float visualDiameterM = 2f * visualCoreRadiusM;
            Vector3 center = motor.GetCenterWorld();

            // mesh is measured, not intended: lossyScale is what the renderer gets, so a
            // parent that is not at scale 1 shows up here instead of hiding.
            float meshDiameterM = coreMesh.lossyScale.x;
            Debug.Log("[ballview] t=" + ToTenth(elapsed)
                + " absorbed=" + absorbedCount
                + " mesh=" + ToTenThousandth(meshDiameterM)
                + " d_vis=" + ToTenThousandth(visualDiameterM)
                + " d_motor=" + ToTenThousandth(physicalDiameterM));

            int attachedCount = 0;
            int swallowedCount = 0;
            float shellRadiusM = visualCoreRadiusM;
            if (attachmentRoot != null)
            {
                int propCount = attachmentRoot.childCount;
                for (int i = 0; i < propCount; i++)
                {
                    Transform prop = attachmentRoot.GetChild(i);
                    if (prop == null) continue;
                    // A hidden prop is not part of the silhouette the player sees.
                    if (!prop.gameObject.activeSelf) continue;
                    Vector3 offset = prop.position - center;
                    float distanceM = offset.magnitude;
                    if (!KatamariMath.IsFiniteFloat(distanceM)) continue;
                    attachedCount = attachedCount + 1;
                    if (distanceM < visualCoreRadiusM) swallowedCount = swallowedCount + 1;
                    if (distanceM > shellRadiusM) shellRadiusM = distanceM;
                }
            }
            Debug.Log("[shell] t=" + ToTenth(elapsed)
                + " absorbed=" + absorbedCount
                + " r_core=" + ToTenThousandth(visualCoreRadiusM)
                + " r_shell=" + ToTenThousandth(shellRadiusM)
                + " swallowed=" + swallowedCount
                + " attached=" + attachedCount
                + " d_motor=" + ToTenThousandth(physicalDiameterM));

            Vector3 headWorldPosition;
            float eyeDistanceM = 0f;
            float apparent = 0f;
            bool hasApparent = false;
            if (TryReadHeadPosition(out headWorldPosition))
            {
                eyeDistanceM = (center - headWorldPosition).magnitude;
                hasApparent = BallPresentationMath.TryApparentSize(visualDiameterM, eyeDistanceM,
                    out apparent);
            }
            if (!hasApparent)
            {
                Debug.Log("[eye] t=" + ToTenth(elapsed)
                    + " absorbed=" + absorbedCount
                    + " app=none"
                    + " d_core=" + ToTenThousandth(visualDiameterM));
                return;
            }
            Debug.Log("[eye] t=" + ToTenth(elapsed)
                + " absorbed=" + absorbedCount
                + " app=" + ToTenThousandth(apparent)
                + " d_core=" + ToTenThousandth(visualDiameterM)
                + " dist=" + ToThousandth(eyeDistanceM));
        }

        /// <summary>
        /// The local player's head datum: the same source (and the same refusals) as
        /// <c>SeatEyeCompensation</c>, so "distance from the camera" means the viewer's actual
        /// eye and not a scene camera the world does not have.
        /// </summary>
        private bool TryReadHeadPosition(out Vector3 headWorldPosition)
        {
            headWorldPosition = Vector3.zero;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null) return false;

            VRCPlayerApi.TrackingData headData =
                player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            Vector3 position = headData.position;
            if (!KatamariMath.IsFiniteVector(position)) return false;
            // An unimplemented datum reports the default struct, i.e. exactly the origin.
            if (position.x == 0f && position.y == 0f && position.z == 0f) return false;
            headWorldPosition = position;
            return true;
        }

        private float ToTenth(float value)
        {
            return Mathf.Floor(value * 10f) / 10f;
        }

        private float ToThousandth(float value)
        {
            return Mathf.Floor(value * 1000f) / 1000f;
        }

        private float ToTenThousandth(float value)
        {
            return Mathf.Floor(value * 10000f) / 10000f;
        }
    }
}
