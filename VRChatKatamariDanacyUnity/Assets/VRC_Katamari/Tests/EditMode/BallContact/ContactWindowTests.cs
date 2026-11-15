using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallContact;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode port of the broadphase-window cases of
    /// Docs/tests/ball_contact_v2/test_contact_v2.py: only near geometry enters the window,
    /// overflow is explicit and can report the window incomplete, and the two-radius
    /// hysteresis keeps an already-selected boundary item.
    ///
    /// R_in = r + travel + 0.05 = 0.48 m and R_out = R_in + 0.5 = 0.98 m for the reference
    /// ball (r = 0.30, travel = 0.13), so a box whose surface is 0.8 m away is inside R_out
    /// only. Selection order is (distance, surface id), exactly the model's.
    /// </summary>
    public class ContactWindowTests
    {
        private const float Tolerance = 1e-6f;

        private sealed class WindowResult
        {
            public int Count;
            public int Dropped;
            public bool Complete;
            public bool Rebuilt;
            public int[] Indices = new int[0];
        }

        private static ContactWorld Row(int count, float spacing)
        {
            ContactWorld world = new ContactWorld();
            for (int i = 0; i < count; i++)
            {
                world.Add(new Vector3(i * spacing, 0.5f, 0f), new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                    new Vector3(0.4f, 0.4f, 0.4f), i + 1, ContactCodes.RoleBox);
            }
            return world;
        }

        private static WindowResult Select(ContactWorld world, Vector3 center, float radius,
            float travel, int[] previousIds, int capacity)
        {
            int[] scratchIndices = new int[world.Count > 0 ? world.Count : 1];
            float[] scratchDistances = new float[scratchIndices.Length];
            int[] selectedIndices = new int[capacity > 0 ? capacity : 1];
            WindowResult result = new WindowResult();
            Assert.IsTrue(ContactWindowMath.TrySelectWindow(world.Count, world.Centers,
                world.AxisX, world.AxisY, world.AxisZ, world.HalfExtents, world.BoundingRadii,
                world.SurfaceIds, center, radius, travel, previousIds, previousIds.Length,
                capacity, scratchIndices, scratchDistances, selectedIndices,
                out result.Count, out result.Dropped, out result.Complete, out result.Rebuilt));
            result.Indices = selectedIndices;
            return result;
        }

        [Test]
        public void TheTwoRadiiAreGeometric()
        {
            Assert.AreEqual(0.48f, ContactWindowMath.WindowRadiusIn(0.30f, 0.13f), Tolerance);
            Assert.AreEqual(0.98f, ContactWindowMath.WindowRadiusOut(0.30f, 0.13f), Tolerance);
        }

        [Test]
        public void OnlyNearGeometryEntersTheWindow()
        {
            ContactWorld world = Row(200, 2f);
            WindowResult window = Select(world, new Vector3(0f, 0.5f, 0f), 0.30f, 0.13f,
                new int[0], ContactTuning.MaxNearObbCount);
            Assert.LessOrEqual(window.Count, 24);
            for (int i = 0; i < window.Count; i++)
            {
                Assert.LessOrEqual(Mathf.Abs(world.Centers[window.Indices[i]].x), 3f);
            }
            Assert.AreEqual(1, window.Count);
            Assert.AreEqual(0, window.Dropped);
            Assert.IsTrue(window.Complete);
            Assert.IsTrue(window.Rebuilt);
        }

        [Test]
        public void OverflowIsExplicitAndReportsIncomplete()
        {
            // 40 boxes all within contact reach of the ball: capacity 24 overflows.
            ContactWorld world = new ContactWorld();
            for (int i = 0; i < 40; i++)
            {
                float angle = 2f * Mathf.PI * i / 40f;
                world.Add(new Vector3(0.35f * Mathf.Cos(angle), 0.35f,
                    0.35f * Mathf.Sin(angle)), new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                    new Vector3(0.2f, 0.2f, 0.2f), i + 1, ContactCodes.RoleBox);
            }
            WindowResult window = Select(world, new Vector3(0f, 0.35f, 0f), 0.30f, 0.13f,
                new int[0], ContactTuning.MaxNearObbCount);
            Assert.AreEqual(24, window.Count);
            Assert.AreEqual(16, window.Dropped);
            Assert.IsFalse(window.Complete);  // a dropped item could have been touching
            Assert.IsTrue(window.Rebuilt);
        }

        [Test]
        public void HysteresisKeepsABoundaryItemOnlyWhileItStaysNear()
        {
            ContactWorld world = new ContactWorld();
            world.Add(new Vector3(1.00f, 0.5f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                new Vector3(0.2f, 0.2f, 0.2f), 7, ContactCodes.RoleBox);
            // The box surface is 0.8 m from the ball centre: inside R_out only.
            WindowResult fresh = Select(world, new Vector3(0f, 0.5f, 0f), 0.30f, 0.13f,
                new int[0], ContactTuning.MaxNearObbCount);
            WindowResult kept = Select(world, new Vector3(0f, 0.5f, 0f), 0.30f, 0.13f,
                new int[] { 7 }, ContactTuning.MaxNearObbCount);
            Assert.AreEqual(0, fresh.Count);
            Assert.AreEqual(1, kept.Count);
            Assert.IsFalse(kept.Rebuilt);
        }

        [Test]
        public void ADroppedItemWithinReachFailsTheWindowClosed()
        {
            // Three touching boxes and a capacity of two: the third is within reach, so the
            // window may not claim to be complete.
            ContactWorld world = new ContactWorld();
            for (int i = 0; i < 3; i++)
            {
                world.Add(new Vector3(0.2f * i, 0.5f, 0f), new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
                    new Vector3(0.2f, 0.2f, 0.2f), i + 1, ContactCodes.RoleBox);
            }
            WindowResult window = Select(world, new Vector3(0f, 0.5f, 0f), 0.30f, 0.13f,
                new int[0], 2);
            Assert.AreEqual(2, window.Count);
            Assert.AreEqual(1, window.Dropped);
            Assert.IsFalse(window.Complete);
        }

        [Test]
        public void AShortSelectedBufferIsRefused()
        {
            ContactWorld world = Row(4, 2f);
            int[] scratchIndices = new int[world.Count];
            float[] scratchDistances = new float[world.Count];
            int[] selectedIndices = new int[1];
            int selectedCount;
            int dropped;
            bool complete;
            bool rebuilt;
            Assert.IsFalse(ContactWindowMath.TrySelectWindow(world.Count, world.Centers,
                world.AxisX, world.AxisY, world.AxisZ, world.HalfExtents, world.BoundingRadii,
                world.SurfaceIds, new Vector3(0f, 0.5f, 0f), 0.30f, 0.13f, new int[0], 0,
                ContactTuning.MaxNearObbCount, scratchIndices, scratchDistances, selectedIndices,
                out selectedCount, out dropped, out complete, out rebuilt));
        }
    }
}
