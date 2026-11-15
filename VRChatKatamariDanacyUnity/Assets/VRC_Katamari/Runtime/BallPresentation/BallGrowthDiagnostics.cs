using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;

namespace VRCKatamari.BallPresentation
{
    /// <summary>
    /// Three diagnostic readouts for "the ball is not visibly growing": the mesh against the
    /// motor, the attached shell against the core, and the core's apparent size in the eye.
    /// Off by default, and free when off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written for the question in Docs/Design/BallPresentation/VISUAL_GROWTH_AUDIT.md
    /// section 4: the ledger provably grows the ball, the mesh is provably scaled from it,
    /// and yet the silhouette does not seem to change. No existing line separates those
    /// three possibilities - <c>StoryBallDiagnostics</c> reports the motor's diameter and the
    /// absorb count, but not the scale actually written to the mesh, not where the attached
    /// props ended up, and not how large the ball is in the viewer's eye.
    /// </para>
    /// <para>
    /// The lines, and what each one decides (field order is fixed so the lines can be
    /// grepped and quoted field by field):
    /// <list type="bullet">
    /// <item><c>[ballview] mesh=&lt;written world diameter&gt; d_motor=&lt;motor diameter&gt;</c>
    /// - the mesh is measured through <c>lossyScale</c>, i.e. what the renderer actually
    /// uses, not what this script intended. With the 0.95 visual radius and the spring both
    /// off, the two numbers must be equal to 1e-4 m; with them on, <c>mesh / d_motor</c>
    /// settles on 0.95 and dips while the spring catches up. A ratio that stays wrong, or a
    /// <c>mesh</c> that stops following <c>d_motor</c>, is the "visual never followed physics"
    /// hypothesis.</item>
    /// <item><c>[shell] core=&lt;visual core radius&gt; shell=&lt;furthest attached prop centre&gt;
    /// swallowed=&lt;count the core has closed over&gt; attached=&lt;count&gt;</c> - props are
    /// frozen at their contact pose and never pushed outward, so this is the measurement of
    /// whether the visible outer layer still sticks out of the core. <c>core / shell</c> well
    /// under 1 with <c>swallowed</c> most of <c>attached</c> is the audit's first-ranked
    /// explanation. <c>shell</c> is a prop CENTRE distance, not an outer surface: the
    /// silhouette is that much plus the prop's own half size, and a centre distance needs no
    /// renderer lookup (this project never calls GetComponent in runtime code).</item>
    /// <item><c>[eye] app=&lt;core diameter / eye distance&gt; d_core=&lt;visual core diameter&gt;
    /// dist=&lt;metres&gt;</c> - the core's apparent size, which is what "it did not grow" is
    /// actually about. <c>app</c> is radians for a small angle. It shrinks when the follow rig
    /// pulls the eye back with the radius, which is the audit's second engine.
    /// <c>app=none</c> means the local head datum was unreadable (no local player, or a
    /// client that does not implement tracking), so there is no eye to measure from.</item>
    /// <item><c>[grow] units=&lt;ledger units gained&gt; D=&lt;diameter before&gt; -&gt; &lt;after&gt;
    /// (+x.x%)</c> - one line per absorb (a frame that absorbs several emits one line with the
    /// summed units), so the percentage can be compared with the 1% readability line in the
    /// audit's section 2. It reads the MOTOR's diameter, so it stays valid with every
    /// presentation flag off.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Cost: the whole component is behind one serialized bool, and the first statement of
    /// <c>Update</c> is the return for it, so a disabled instance builds no strings, calls no
    /// motor getter and touches no transform. Enabled, it reports at most a few times a
    /// second (<c>logIntervalSeconds</c>, 0.5 s by default) with no allocation: the loops
    /// read transforms and ints, and the only allocations are the log lines themselves.
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

        [Tooltip("Seconds between report lines. A non-positive value falls back to 0.5 s.")]
        [SerializeField] private float logIntervalSeconds = 0.5f;

        // A distance below this is a degenerate eye position, not a real measurement.
        private const float MinEyeDistanceM = 0.0001f;

        private bool ready;
        private float elapsed;
        private float nextLogTime;

        // Change detection for the per-absorb line. Both are seeded from the motor on the
        // first ready frame, so a ball that started before this component did not report the
        // absorbs that already happened as one giant line.
        private int lastAbsorbedCount;
        private float lastConfirmedUnits;
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
                lastConfirmedUnits = motor.GetConfirmedVolumeUnits();
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
        /// One line per absorbed batch: how many ledger units arrived, the diameter before and
        /// after, and the relative change.
        /// </summary>
        /// <remarks>
        /// The motor steps in its own Update and Unity does not order two behaviours' Updates,
        /// so <c>before</c> is the diameter read on the PREVIOUS frame - at 50 Hz that is at
        /// most 20 ms stale, which is under one fixed step's worth of absorbs and far below the
        /// 1% line this number is compared against.
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

            float confirmedUnits = motor.GetConfirmedVolumeUnits();
            float gainedUnits = confirmedUnits - lastConfirmedUnits;
            float beforeM = lastDiameterM;
            if (!(beforeM > 0f)) beforeM = diameterM;
            float percent = 0f;
            if (beforeM > 0f) percent = (diameterM - beforeM) / beforeM * 100f;
            string sign = "";
            if (percent >= 0f) sign = "+";

            Debug.Log("[grow] units=" + ToThousandth(gainedUnits)
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
            float physicalDiameterM = motor.GetSemanticDiameterWorldM();
            // lossyScale, not localScale: this is the size the renderer gets, so a parent that
            // is not at scale 1 shows up here instead of hiding.
            float meshDiameterM = coreMesh.lossyScale.x;
            Debug.Log("[ballview] mesh=" + ToTenThousandth(meshDiameterM)
                + " d_motor=" + ToTenThousandth(physicalDiameterM));

            float coreRadiusM = motor.GetVisualCoreRadiusWorldM();
            Vector3 center = motor.GetCenterWorld();
            int attachedCount = 0;
            int swallowedCount = 0;
            float shellRadiusM = coreRadiusM;
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
                    if (distanceM < coreRadiusM) swallowedCount = swallowedCount + 1;
                    if (distanceM > shellRadiusM) shellRadiusM = distanceM;
                }
            }
            Debug.Log("[shell] core=" + ToTenThousandth(coreRadiusM)
                + " shell=" + ToTenThousandth(shellRadiusM)
                + " swallowed=" + swallowedCount
                + " attached=" + attachedCount);

            Vector3 headWorldPosition;
            if (!TryReadHeadPosition(out headWorldPosition))
            {
                Debug.Log("[eye] app=none");
                return;
            }
            float coreDiameterM = 2f * coreRadiusM;
            float eyeDistanceM = (center - headWorldPosition).magnitude;
            float apparent = 0f;
            if (eyeDistanceM > MinEyeDistanceM) apparent = coreDiameterM / eyeDistanceM;
            Debug.Log("[eye] app=" + ToTenThousandth(apparent)
                + " d_core=" + ToTenThousandth(coreDiameterM)
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
