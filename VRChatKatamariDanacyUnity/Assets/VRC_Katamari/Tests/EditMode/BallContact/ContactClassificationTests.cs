using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of the classification cases of
    /// Docs/tests/ball_contact_v2/test_contact_v2.py, plus the support/wall selection and
    /// the 15 deg "the surface is curving over" rule that the Python model applies inline.
    ///
    /// Angles are degrees from +Y: 0 is a floor, 90 a wall. Expected values are geometric
    /// identities (the 45 deg normal of a 45 deg board, cos(70 deg), the 15 deg margin),
    /// never a copy of the implementation.
    /// </summary>
    public class ContactClassificationTests
    {
        private const float Tolerance = 1e-5f;
        private const float Sin45 = 0.70710678f;
        private const float Cos45 = 0.70710678f;

        private static Vector3 SlopeNormal(float angleDeg)
        {
            float angle = angleDeg * Mathf.Deg2Rad;
            return new Vector3(0f, Mathf.Cos(angle), -Mathf.Sin(angle));
        }

        [Test]
        public void ThreeBandsAreDerivedFromOneNormal()
        {
            Assert.AreEqual(ContactCodes.ClassRide,
                ContactClassificationMath.ClassifyContact(new Vector3(0f, 1f, 0f)));
            Assert.AreEqual(ContactCodes.ClassRide,
                ContactClassificationMath.ClassifyContact(new Vector3(0f, Cos45, -Sin45)));
            Assert.AreEqual(ContactCodes.ClassClimb,
                ContactClassificationMath.ClassifyContact(SlopeNormal(70f)));
            Assert.AreEqual(ContactCodes.ClassClimb,
                ContactClassificationMath.ClassifyContact(new Vector3(0f, 0f, 1f)));
            Assert.AreEqual(ContactCodes.ClassNone,
                ContactClassificationMath.ClassifyContact(new Vector3(0f, -1f, 0f)));
            // 50 deg is already outside the ride band (that is C06's redesign).
            Assert.AreEqual(ContactCodes.ClassClimb,
                ContactClassificationMath.ClassifyContact(SlopeNormal(50f)));
        }

        [Test]
        public void TheRideBoundaryHasHalfADegreeOfTolerance()
        {
            // 45.4 deg rides, 45.6 deg climbs: the 0.5 deg band is what keeps a 45 deg board
            // from flipping on float noise.
            Assert.AreEqual(ContactCodes.ClassRide,
                ContactClassificationMath.ClassifyContact(SlopeNormal(45.4f)));
            Assert.AreEqual(ContactCodes.ClassClimb,
                ContactClassificationMath.ClassifyContact(SlopeNormal(45.6f)));
        }

        [Test]
        public void TheContactAngleIsTheAngleFromUp()
        {
            Assert.AreEqual(0f, ObbContactMath.ContactAngleDeg(new Vector3(0f, 1f, 0f)),
                Tolerance);
            Assert.AreEqual(45f, ObbContactMath.ContactAngleDeg(new Vector3(0f, Cos45, -Sin45)),
                Tolerance);
            Assert.AreEqual(90f, ObbContactMath.ContactAngleDeg(new Vector3(0f, 0f, 1f)),
                Tolerance);
        }

        [Test]
        public void TheSupportIsTheHighestRideableNormalAndTiesGoToTheDeeperContact()
        {
            Vector3[] normals = { new Vector3(0f, 1f, 0f), SlopeNormal(45f) };
            float[] depths = { 0.001f, 0.010f };
            int[] classes = { ContactCodes.ClassRide, ContactCodes.ClassRide };
            // The floor normal has the higher +Y, so it wins even with the smaller depth.
            Assert.AreEqual(0, ContactClassificationMath.SelectSupportContact(normals, depths,
                classes, 2));

            // Equal normals: the deeper contact wins.
            Vector3[] tied = { new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f) };
            float[] tiedDepths = { 0.002f, 0.005f };
            Assert.AreEqual(1, ContactClassificationMath.SelectSupportContact(tied, tiedDepths,
                classes, 2));

            // A climb contact is never a support.
            int[] climbs = { ContactCodes.ClassClimb, ContactCodes.ClassClimb };
            Assert.AreEqual(-1, ContactClassificationMath.SelectSupportContact(normals, depths,
                climbs, 2));
        }

        [Test]
        public void TheWallIsTheContactTheDrivePushesIntoMost()
        {
            // Normals point from the solid towards the ball centre (design section 9.1), so a
            // wall the drive pushes INTO has n = -drive: driving -z meets the wall whose
            // normal is +z, driving +x meets the wall whose normal is -x. Written the other
            // way round every push-in is zero and the model's tie-break returns index 0
            // (verified against contact_v2._climb_contact: pushes [0, 0] for both drives).
            Vector3[] normals =
            {
                new Vector3(0f, 0f, 1f),    // wall ahead when driving -z
                new Vector3(-1f, 0f, 0f),   // wall ahead when driving +x
                new Vector3(0f, 1f, 0f)     // floor
            };
            int[] classes =
            {
                ContactCodes.ClassClimb, ContactCodes.ClassClimb, ContactCodes.ClassRide
            };
            Assert.AreEqual(0, ContactClassificationMath.SelectClimbContact(normals, classes, 3,
                new Vector3(0f, 0f, -1f)));
            Assert.AreEqual(1, ContactClassificationMath.SelectClimbContact(normals, classes, 3,
                new Vector3(1f, 0f, 0f)));
            // A zero drive direction cannot select a wall.
            Assert.AreEqual(-1, ContactClassificationMath.SelectClimbContact(normals, classes, 3,
                Vector3.zero));
            // A count past the buffers is refused rather than read out of bounds.
            Assert.AreEqual(-1, ContactClassificationMath.SelectClimbContact(normals, classes, 4,
                new Vector3(0f, 0f, -1f)));
        }

        [Test]
        public void ASurfaceCurvingOverIsNotAClimb()
        {
            // 50 deg over a 45 deg support is the surface turning over; 60 deg is a wall.
            Assert.IsFalse(ContactClassificationMath.IsClimbSteeperThanSupport(50f, 45f));
            Assert.IsFalse(ContactClassificationMath.IsClimbSteeperThanSupport(59.9f, 45f));
            Assert.IsTrue(ContactClassificationMath.IsClimbSteeperThanSupport(60f, 45f));
            Assert.IsTrue(ContactClassificationMath.IsClimbSteeperThanSupport(65f, 45f));
            // With no support (support angle 0) any climb contact counts as a wall.
            Assert.IsTrue(ContactClassificationMath.IsClimbSteeperThanSupport(20f, 0f));
        }

        [Test]
        public void TheRememberedClimbFaceIsFoundByItsPlaneNotByItsIndex()
        {
            // The selection hysteresis (TERRAIN_CLIMB 6.6): contact 0 is a face of a narrow V,
            // contact 1 the other, contact 2 the floor, contact 3 a SECOND triangle of the
            // remembered plane (a split wall). The remembered plane is face 1's.
            float a = 65f * Mathf.Deg2Rad;
            Vector3 faceA = new Vector3(Mathf.Sin(a), 0f, -Mathf.Cos(a));
            Vector3 faceB = new Vector3(-Mathf.Sin(a), 0f, -Mathf.Cos(a));
            Vector3[] normals = { faceA, faceB, new Vector3(0f, 1f, 0f), faceB };
            Vector3[] points =
            {
                new Vector3(0.1f, 0.3f, 2.9f), new Vector3(-0.1f, 0.3f, 2.9f),
                new Vector3(0f, 0f, 2.5f), new Vector3(-0.1f, 0.9f, 2.9f)
            };
            float[] depths = { 0.002f, 0.001f, 0f, 0.003f };
            int[] classes =
            {
                ContactCodes.ClassClimb, ContactCodes.ClassClimb, ContactCodes.ClassRide,
                ContactCodes.ClassClimb
            };
            Vector3 planePoint = new Vector3(-0.1f, 0.5f, 2.9f);

            // Both of the remembered plane's contacts match; the DEEPER one (index 3) wins.
            Assert.AreEqual(3, ContactClassificationMath.FindClimbContactOnPlane(normals,
                points, depths, classes, 4, faceB, planePoint));
            // Equal depths: the first match.
            float[] flat = { 0.002f, 0.001f, 0f, 0.001f };
            Assert.AreEqual(1, ContactClassificationMath.FindClimbContactOnPlane(normals,
                points, flat, classes, 4, faceB, planePoint));
            // The other face is a different plane (normal dot -0.64), the floor is not a
            // climb contact, and a plane point 5 cm off every contact finds nothing.
            Assert.AreEqual(0, ContactClassificationMath.FindClimbContactOnPlane(normals,
                points, depths, classes, 3, faceA, new Vector3(0.1f, 0.5f, 2.9f)));
            Assert.AreEqual(-1, ContactClassificationMath.FindClimbContactOnPlane(normals,
                points, depths, classes, 4, new Vector3(0f, 1f, 0f), planePoint));
            Assert.AreEqual(-1, ContactClassificationMath.FindClimbContactOnPlane(normals,
                points, depths, classes, 4, faceB,
                new Vector3(-0.1f + faceB.x * 0.05f, 0.5f, 2.9f + faceB.z * 0.05f)));
            // A tilted normal outside the 0.99 dot (10 deg: cos = 0.985) is another face.
            float tilt = 10f * Mathf.Deg2Rad;
            Vector3 tilted = new Vector3(faceB.x * Mathf.Cos(tilt), Mathf.Sin(tilt),
                faceB.z * Mathf.Cos(tilt));
            Assert.AreEqual(-1, ContactClassificationMath.FindClimbContactOnPlane(normals,
                points, depths, classes, 4, tilted, planePoint));
            // Unusable arguments are refused, never guessed.
            Assert.AreEqual(-1, ContactClassificationMath.FindClimbContactOnPlane(normals,
                points, depths, classes, 5, faceB, planePoint));
            Assert.AreEqual(-1, ContactClassificationMath.FindClimbContactOnPlane(normals,
                points, depths, classes, 4, Vector3.zero, planePoint));
            Assert.AreEqual(-1, ContactClassificationMath.FindClimbContactOnPlane(null,
                points, depths, classes, 4, faceB, planePoint));
        }
    }
}
