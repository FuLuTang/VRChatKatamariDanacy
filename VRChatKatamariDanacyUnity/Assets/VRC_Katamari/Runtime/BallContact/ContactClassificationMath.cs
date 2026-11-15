using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// The one classification of a contact normal, and the two independent choices
    /// the solver makes from a contact set: the support surface and the climb wall.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Classification is continuous in theta and reads nothing but the normal's +Y
    /// part (section 2.2): a ceiling (n.y &lt; -0.05) is CLASS_NONE, n.y at or above
    /// cos(45.5 deg) is CLASS_RIDE, everything else is CLASS_CLIMB. There is no
    /// surface-type branch and no angle whitelist, so a 45 deg ramp and a 45 deg box
    /// face classify identically. The 0.5 deg tolerance on the ride boundary is part
    /// of the design: a 45 deg board lies exactly on the line.
    /// </para>
    /// <para>
    /// Support and wall are chosen independently and on purpose. Being supported by the
    /// floor while pushing a wall is the normal start of a climb, so a single
    /// "dominant contact" would never let a climb begin. Support is the rideable
    /// contact with the highest n.y (ties: the deeper one); the wall is the climb-class
    /// contact the drive pushes into most (ties: the one with the higher n.y).
    /// </para>
    /// <para>
    /// The 15 deg margin is the third piece of the same discipline: a climb-class
    /// contact that is not clearly steeper than the support is the surface curving over
    /// (a ramp meeting a deck), so it is neither climbed nor allowed to clip the
    /// velocity. <see cref="IsClimbSteeperThanSupport"/> is the single place that
    /// comparison lives, because the model applies it in three places (wall selection,
    /// the climb channel and the velocity clip).
    /// </para>
    /// </remarks>
    public static class ContactClassificationMath
    {
        /// <summary>
        /// theta-derived class of one contact normal:
        /// <see cref="ContactCodes.ClassNone"/>, <see cref="ContactCodes.ClassRide"/>
        /// or <see cref="ContactCodes.ClassClimb"/>.
        /// </summary>
        /// <remarks>
        /// The comparison order is the model's: the ceiling test first, then the ride
        /// boundary, then climb. A normal just below zero (n.y = -0.03) is not a ceiling
        /// and not rideable, so it classifies as climb - that is the model's behaviour,
        /// not a special case.
        /// </remarks>
        public static int ClassifyContact(Vector3 normal,
            float thetaRideDeg = ContactTuning.ThetaRideDeg)
        {
            float upward = normal.y;
            if (upward < -ContactTuning.MinUpwardNormalY) return ContactCodes.ClassNone;
            float rideBoundary = Mathf.Cos((thetaRideDeg +
                ContactTuning.ThetaRideToleranceDeg) * Mathf.Deg2Rad);
            if (upward >= rideBoundary) return ContactCodes.ClassRide;
            return ContactCodes.ClassClimb;
        }

        /// <summary>
        /// True when a climb-class contact is steeper than the support by at least the
        /// 15 deg margin, i.e. when it is a real wall rather than the surface turning
        /// over. False exemptions are what let a ball roll over a ramp/deck seam.
        /// </summary>
        public static bool IsClimbSteeperThanSupport(float contactAngleDeg,
            float supportAngleDeg)
        {
            return contactAngleDeg >= supportAngleDeg + ContactTuning.ClimbSteeperMarginDeg;
        }

        /// <summary>
        /// Index of the support contact (the highest rideable normal, ties to the
        /// deeper contact), or -1 when no contact is rideable.
        /// </summary>
        /// <remarks>
        /// Skin contacts (depth &lt;= 0) are eligible on purpose: "the surface I am
        /// resting on" is a registration, not a penetration, and the push-out only ever
        /// touches depth beyond the slop. -1 also means an unusable argument (a null
        /// buffer or a count beyond the shortest buffer), never a guess.
        /// </remarks>
        public static int SelectSupportContact(Vector3[] contactNormals,
            float[] contactDepths, int[] contactClasses, int contactCount)
        {
            if (contactNormals == null || contactDepths == null || contactClasses == null)
                return -1;
            if (contactCount < 0 || contactCount > contactNormals.Length ||
                contactCount > contactDepths.Length || contactCount > contactClasses.Length)
                return -1;
            int best = -1;
            float bestUpward = 0f;
            float bestDepth = 0f;
            for (int i = 0; i < contactCount; i++)
            {
                if (contactClasses[i] != ContactCodes.ClassRide) continue;
                float upward = contactNormals[i].y;
                if (best < 0 || upward > bestUpward)
                {
                    best = i;
                    bestUpward = upward;
                    bestDepth = contactDepths[i];
                }
                else if (Mathf.Abs(upward - bestUpward) < ContactTuning.TieTolerance &&
                    contactDepths[i] > bestDepth)
                {
                    best = i;
                    bestDepth = contactDepths[i];
                }
            }
            return best;
        }

        /// <summary>
        /// Index of the climb-class contact the drive pushes into most, or -1 when there
        /// is none (or the drive direction is degenerate).
        /// </summary>
        /// <remarks>
        /// The push-in score is <c>max(0, -dot(unitDrive, n_horizontal))</c>, so a
        /// shallow side graze (small score) is not selected over a head-on wall. Ties go
        /// to the contact with the higher n.y, i.e. the less vertical wall. The drive
        /// direction is normalised here, exactly as the model does, because this
        /// selection is also callable on its own. A climb-class normal with no horizontal
        /// part cannot exist (classification needs n.y &lt; 0.70 and the ceiling band
        /// takes n.y &lt; -0.05), so such a record is skipped rather than selected.
        /// </remarks>
        public static int SelectClimbContact(Vector3[] contactNormals, int[] contactClasses,
            int contactCount, Vector3 driveDirection)
        {
            if (contactNormals == null || contactClasses == null) return -1;
            if (contactCount < 0 || contactCount > contactNormals.Length ||
                contactCount > contactClasses.Length) return -1;
            Vector3 unitDrive;
            float driveLength;
            if (!KatamariMath.TryNormalize(driveDirection, out unitDrive, out driveLength))
                return -1;

            int best = -1;
            float bestPush = -1f;
            float bestUpward = 0f;
            for (int i = 0; i < contactCount; i++)
            {
                if (contactClasses[i] != ContactCodes.ClassClimb) continue;
                float horizontalX = contactNormals[i].x;
                float horizontalZ = contactNormals[i].z;
                float horizontalLength = Mathf.Sqrt(horizontalX * horizontalX +
                    horizontalZ * horizontalZ);
                if (horizontalLength <= 0f) continue;
                float inverse = 1f / horizontalLength;
                float push = Mathf.Max(0f,
                    -(unitDrive.x * horizontalX * inverse + unitDrive.z * horizontalZ * inverse));
                if (best < 0 || push > bestPush + ContactTuning.TieTolerance ||
                    (Mathf.Abs(push - bestPush) <= ContactTuning.TieTolerance &&
                        contactNormals[i].y > bestUpward))
                {
                    best = i;
                    bestPush = push;
                    bestUpward = contactNormals[i].y;
                }
            }
            return best;
        }
    }
}
