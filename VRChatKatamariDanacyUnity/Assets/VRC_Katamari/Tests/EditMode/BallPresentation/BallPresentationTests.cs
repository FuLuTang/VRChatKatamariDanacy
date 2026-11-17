using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.BallPresentation;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins BallView's collision-diameter scaling and the apparent-size helper used by
    /// the eye diagnostics. BallGrowthDiagnosticsTests separately pins the scene probe.
    /// </summary>
    public class BallPresentationTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic;

        [TestCase(0.222f)]
        [TestCase(0.3f)]
        public void CoreDiameterFollowsCollisionAfterAbsorptionAndShrink(float startDiameterM)
        {
            GameObject host = new GameObject("BallViewTests");
            GameObject core = new GameObject("CoreMesh");
            GameObject attachments = new GameObject("AttachmentRoot");
            try
            {
                Type motorType = typeof(BallPresentationMath).Assembly.GetType(
                    "VRCKatamari.BallMotor.KatamariBallMotor", true);
                Type viewType = typeof(BallPresentationMath).Assembly.GetType(
                    "VRCKatamari.BallPresentation.BallView", true);
                Component motor = host.AddComponent(motorType);
                Component view = host.AddComponent(viewType);
                viewType.GetField("motor", Any).SetValue(view, motor);
                viewType.GetField("coreMesh", Any).SetValue(view, core.transform);
                core.transform.SetParent(host.transform);
                attachments.transform.SetParent(host.transform);
                core.transform.localScale = Vector3.one * 0.7f;
                Action tick = (Action)Delegate.CreateDelegate(typeof(Action), view,
                    viewType.GetMethod("Update", Any));

                // An unstarted motor reports zero; the existing mesh must stay visible.
                tick();
                Assert.AreEqual(0.7f, core.transform.localScale.x, 1e-6f);
                long startVolume;
                Assert.IsTrue(SizeModelMath.TrySphereVolumeMm3(startDiameterM, 1f,
                    out startVolume));
                motorType.GetField("startVolumeMm3", Any).SetValue(motor, startVolume);
                MethodInfo derive = motorType.GetMethod("DeriveSize", Any);
                MethodInfo collision = motorType.GetMethod("GetCollisionRadiusWorldM", Any);
                float previous = 0f;
                foreach (long confirmed in new long[] { 0L, 1000L, 2000L, 100000L, 200000L, 1L, 0L })
                {
                    motorType.GetField("confirmedVolumeMm3", Any).SetValue(motor, confirmed);
                    derive.Invoke(motor, null);
                    // Deliberately disagree with semantic/start: presentation uses the same
                    // collision radius as the geometric pickup gate, never HUD bias or start.
                    motorType.GetField("semanticDiameterWorldM", Any).SetValue(motor, 9f);
                    tick();
                    float expected = 2f * (float)collision.Invoke(motor, null);
                    Assert.AreEqual(expected, core.transform.localScale.x, 1e-7f);
                    Assert.AreEqual(expected, core.transform.localScale.y, 1e-7f);
                    Assert.AreEqual(expected, core.transform.localScale.z, 1e-7f);
                    if (confirmed == 100000L || confirmed == 200000L)
                        Assert.Greater(expected, previous, "absorbed volume must grow the core");
                    previous = expected;
                    Assert.AreEqual(Vector3.one, host.transform.localScale);
                    Assert.AreEqual(Vector3.one, attachments.transform.localScale);
                    tick();
                    Assert.AreEqual(expected, core.transform.localScale.x, 1e-7f);
                }
                Assert.AreEqual(startDiameterM, previous, 1e-6f);
                motorType.GetField("collisionRadiusWorldM", Any).SetValue(motor, float.NaN);
                tick();
                Assert.AreEqual(previous, core.transform.localScale.x, 1e-7f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(core);
                UnityEngine.Object.DestroyImmediate(attachments);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        // ---------------------------------------------------------------- log quantities
        /// <summary>
        /// The apparent size the <c>[eye]</c> line prints is the core diameter over the eye
        /// distance. The first case is a sample from a full round (0.7863 m core at
        /// 2.297 m reads 0.3423 rad), so the printed line can be checked by hand.
        /// </summary>
        [Test]
        public void ApparentSizeIsCoreDiameterOverEyeDistance()
        {
            float apparent;
            Assert.IsTrue(BallPresentationMath.TryApparentSize(0.7863f, 2.297f, out apparent));
            Assert.AreEqual(0.3423f, apparent, 5e-5f);

            Assert.IsTrue(BallPresentationMath.TryApparentSize(1f, 2f, out apparent));
            Assert.AreEqual(0.5f, apparent, 1e-6f);

            // Doubling the distance halves the apparent size: that factor is exactly the
            // follow rig's pull-back, which is why the audit multiplies growth by it.
            float near;
            float far;
            Assert.IsTrue(BallPresentationMath.TryApparentSize(0.5f, 1f, out near));
            Assert.IsTrue(BallPresentationMath.TryApparentSize(0.5f, 2f, out far));
            Assert.AreEqual(0.5f * near, far, 1e-6f);
        }

        /// <summary>
        /// A degenerate eye or a missing ball is not a measurement: the line then prints
        /// <c>app=none</c> instead of an invented number.
        /// </summary>
        [Test]
        public void ApparentSizeRefusesADegenerateEyeOrBall()
        {
            float apparent;
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0.7863f, 0f, out apparent));
            Assert.AreEqual(0f, apparent);
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0.7863f, 0.00001f, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0.7863f, -1f, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0.7863f, float.NaN, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(0f, 2f, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(-1f, 2f, out apparent));
            Assert.IsFalse(BallPresentationMath.TryApparentSize(float.NaN, 2f, out apparent));
        }
    }
}
