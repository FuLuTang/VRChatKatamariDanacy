using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Story
{
    /// <summary>
    /// Read-only story-mode ball readout: one <c>[story]</c> line every N seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the permanent replacement for the acceptance readout that used to live in
    /// <c>Labs/StoryLevel/StoryLevelLabBootstrap.Report()</c>. The formal scene no longer
    /// carries the lab script, but P1/P2 Play acceptance still has no other source for
    /// <c>gap</c>, <c>support</c> / <c>supportId</c>, <c>stepLimit</c> and <c>falling</c>
    /// (Docs/Design/StoryMode/PICNIC_CORNER_LEVEL_ONE.md section 5.8 K1;
    /// Docs/Design/BallMotor/TERRAIN_SUPPORT.md section 3.6 item 3 and section 9 R11).
    /// </para>
    /// <para>
    /// The field set is the lab line's with ONE addition, and the order is otherwise
    /// unchanged: <c>t / center / heading / diameter / d_vis / bottom / gap / support /
    /// supportId / supportY / supports / stepLimit / falling / axes / absorbed / rejects</c>.
    /// Only the prefix differs (<c>[story]</c> instead of <c>[story-lab]</c>), because the
    /// scene tag says which scene produced the line, not which script. Keeping the order
    /// fixed matters: the acceptance notes quote these numbers field by field.
    /// </para>
    /// <para>
    /// <c>diameter</c> is <c>GetSemanticDiameterWorldM()</c>, the gameplay diameter: the
    /// absorb gate and the HUD read it (the HUD adds the original's +0.02 cm display fudge).
    /// <c>d_vis</c> is <c>2 * GetVisualCoreRadiusWorldM()</c>, the design's visual core
    /// (the level's fixed start diameter, CONTACT_MODEL_V2 4A.2) that the core mesh is drawn at; it
    /// does not change as the ball grows. Both are
    /// on the same line, read in the same frame, because the O6 full round compared a visual
    /// radius against a semantic diameter and read the 2x difference as a disagreement. The
    /// result board and the star targets evaluate the collision diameter
    /// (<c>2 * GetCollisionRadiusWorldM()</c>, frozen at the bell by
    /// <c>StoryRoundController.MeasureSettledRound</c>); it equals <c>diameter</c> today
    /// because both size ratios are 1, and it is deliberately not repeated here.
    /// </para>
    /// <para>
    /// It reads and does nothing else: no drive, no camera, no motor registration, no
    /// state. The only reference is the motor. A positive semantic diameter is the motor's
    /// public "started" proof, so reporting waits for it in <c>Update</c> rather than
    /// touching the motor in <c>Start</c>, where its stores may not exist yet.
    /// </para>
    /// <para>
    /// There is deliberately no shared formatter with the lab script. That script is
    /// scheduled for deletion once this component is attached (K2), so a static helper
    /// would add permanent API for two callers that are about to become one, and every
    /// shared-code change would need a Unity reimport of a component nobody will keep.
    /// The duplicated part is one format string that lives here from now on.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class StoryBallDiagnostics : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;

        [Header("Reporting")]
        [SerializeField] private bool loggingEnabled = true;

        [Tooltip("Seconds between readout lines. A non-positive value falls back to 1 s.")]
        [SerializeField] private float logIntervalSeconds = 1f;

        private bool ready;
        private float elapsed;
        private float nextLogTime;

        public void Update()
        {
            if (!loggingEnabled) return;

            if (!ready)
            {
                if (!MotorReady()) return;
                ready = true;
                elapsed = 0f;
                nextLogTime = 0f;
            }

            elapsed = elapsed + Time.deltaTime;

            // A scene author who types 0 must not turn this into a per-frame log; the
            // on/off switch is loggingEnabled, not the interval.
            float interval = logIntervalSeconds;
            if (interval <= 0f) interval = 1f;

            if (elapsed < nextLogTime) return;
            nextLogTime = elapsed + interval;
            Report();
        }

        /// <summary>True once the motor reports a started ball (diameter above zero).</summary>
        private bool MotorReady()
        {
            if (motor == null) return false;
            return motor.GetSemanticDiameterWorldM() > 0f;
        }

        /// <summary>
        /// One line per interval: elapsed since the ball started, centre, heading, the semantic
        /// and the visual diameter, the ball's bottom, the gap above the selected support,
        /// which surface that is, and whether the ball is falling.
        /// </summary>
        /// <remarks>
        /// <c>bottom</c> is the ball's lowest core point in world Y
        /// (<c>centre.y - collision radius</c>); <c>gap</c> is the distance from the centre to
        /// the selected support plane along that plane's normal, minus the radius. Both are
        /// reported because one proves the height and the other proves the ball actually sits on
        /// the surface (0.000 +/-0.005) and goes positive while it falls. The normal form matters
        /// on ramps: the vertical form reads <c>r(1/n.y - 1)</c> there (design R11).
        /// </remarks>
        private void Report()
        {
            Vector3 center = motor.GetCenterWorld();
            float diameter = motor.GetSemanticDiameterWorldM();
            // The drawn core's design diameter, in the same frame as the gameplay diameter:
            // a visual radius and a semantic diameter on one line cannot be read as agreeing
            // or disagreeing by accident (O6 round, Docs/Evidence/StoryMode/
            // 2026-11-16_first_full_round).
            float visualDiameter = 2f * motor.GetVisualCoreRadiusWorldM();
            float supportHeight = motor.GetSupportSurfaceHeightWorldM();
            Vector3 supportNormal = motor.GetSupportNormalWorld();
            // Gap is measured ALONG THE SUPPORT NORMAL: "ball centre to the support plane,
            // minus the radius". The design's R11 case is a ramp: the vertical
            // (centre.y - supportHeight - r) would read r(1/n.y - 1), i.e. 0.455 m for a
            // 45-degree board and a 2.20 m ball, and look like the ball floats. Projecting by
            // n.y turns that into exactly 0 on any plane. Flat ground has n.y = 1, so its
            // number is the old one (up to float association order).
            float normalY = supportNormal.y;
            if (!(normalY > 0.001f) || normalY > 1.0001f) normalY = 1f;
            float radius = motor.GetCollisionRadiusWorldM();
            float bottom = center.y - radius;
            float gap = (center.y - supportHeight) * normalY - radius;
            // The kind comes from the motor, not from a range test on the id: prop tops are
            // 100+i, so "id > 0" would mislabel one as a box.
            int supportId = motor.GetSupportSurfaceId();
            int supportKind = motor.GetSupportKind();
            // v2 adds kind 4 (a climb contact holds the ball) and 5 (only a pin holds it):
            // without them the line said "none" while the ball was visibly climbing. A climb
            // reports supportY = the ball's own bottom ON PURPOSE (the test field's
            // support_gap must read 0 on a wall; pass6 C08/C09/C10 read exactly -r otherwise),
            // so gap is a floor reading, never a climb reading.
            string supportName = BallDiagnosticsMath.SupportName(supportKind);
            bool supported = BallDiagnosticsMath.HasSupport(supportKind);
            // A PIN support is a shape-layer answer (design 4A.3): the core gap formula does
            // not describe it (it reads the tangent plane through the tip, which is 0 here,
            // and pass6 showed it reading -r for a wall) - so the line reports the kind and
            // leaves the floor reading out.
            bool gapMeaningful = supported && supportKind != 5;
            Vector3 axes = motor.GetPlanarVelocity();
            Debug.Log("[story] t=" + (Mathf.Floor(elapsed * 10f) / 10f)
                + " center=" + center
                + " heading=" + (Mathf.Floor(motor.GetHeadingDegrees() * 10f) / 10f)
                + " diameter=" + (Mathf.Floor(diameter * 10000f) / 10000f)
                + " d_vis=" + (Mathf.Floor(visualDiameter * 10000f) / 10000f)
                + " bottom=" + (Mathf.Floor(bottom * 1000f) / 1000f)
                // An unsupported frame keeps the PREVIOUS support pair (the motor's
                // documented v1 behaviour), so its height and gap would describe another
                // frame: print na instead of a number nobody can act on.
                + " gap=" + (gapMeaningful
                    ? (Mathf.Floor(gap * 1000f) / 1000f).ToString() : "na")
                + " support=" + supportName
                + " supportId=" + supportId
                + " supportY=" + (supported
                    ? (Mathf.Floor(supportHeight * 1000f) / 1000f).ToString() : "na")
                + " supports=" + motor.GetSupportSurfaceCount()
                + " stepLimit=" + (Mathf.Floor(motor.GetStepHeightLimitWorldM() * 1000f) / 1000f)
                + " falling=" + motor.IsFalling()
                + " axes=" + (Mathf.Floor(axes.x * 100f) / 100f) + "," + (Mathf.Floor(axes.z * 100f) / 100f)
                + " absorbed=" + motor.GetAbsorbedCount()
                + " rejects=" + motor.GetRejectEventCount()
                // Terrain cost, on ONE line so micros/full-eval can be computed by eye:
                // tq is the LAST step, steps is how many steps the frame ran, so the frame's
                // terrain cost is tq * steps. tqSup/tqPen split the ring from the T1
                // push-out; triTests is every triangle the chunk filter let through and
                // fullEvals the ones that paid the cross/plane/barycentric work.
                + " terrain=" + motor.GetTerrainMeshTriangleCount() + "/"
                + motor.GetTerrainMeshChunkCount()
                + " grid=" + motor.GetTerrainGridCellCount()
                + " overs=" + motor.GetTerrainOversizedTriangles()
                // Candidate counts are PER QUERY, PER LAST STEP (not frame-accumulated):
                // cands = support, candsPen = T1 push-out, candsContact = v2 sweep window.
                + " cands=" + motor.GetTerrainCandidates()
                + " candsPen=" + motor.GetTerrainPenetrationCandidates()
                + " candsContact=" + motor.GetTerrainContactCandidates()
                + " tq=" + (Mathf.Floor(motor.GetLastTerrainQueryMicros() * 10f) / 10f)
                + " tqSup=" + (Mathf.Floor(motor.GetLastTerrainSupportMicros() * 10f) / 10f)
                + " tqPen=" + (Mathf.Floor(motor.GetLastTerrainPenetrationMicros() * 10f) / 10f)
                + " triTests=" + motor.GetTerrainTrianglesTested()
                + " fullEvals=" + motor.GetTerrainFullEvals()
                + " steps=" + motor.GetStepsThisFrame());
        }
    }
}
