using NUnit.Framework;
using VRCKatamari.Story;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The <c>[story]</c> line's support vocabulary: the v2 model's two extra kinds must be
    /// named instead of printing "none", and an unsupported frame must not be read as
    /// "support at the ball's bottom" (the pair is stale then).
    /// </summary>
    public class BallDiagnosticsMathTests
    {
        [Test]
        public void EverySupportKindHasItsOwnName()
        {
            Assert.AreEqual("ground", BallDiagnosticsMath.SupportName(0));
            Assert.AreEqual("box", BallDiagnosticsMath.SupportName(1));
            Assert.AreEqual("ramp", BallDiagnosticsMath.SupportName(2));
            Assert.AreEqual("prop", BallDiagnosticsMath.SupportName(3));
            Assert.AreEqual("climb", BallDiagnosticsMath.SupportName(4),
                "a v2 climb contact is a support kind of its own, not 'none'");
            Assert.AreEqual("pin", BallDiagnosticsMath.SupportName(5),
                "a v2 pin support is a support kind of its own, not 'none'");
            Assert.AreEqual("terrain", BallDiagnosticsMath.SupportName(6));
            Assert.AreEqual("none", BallDiagnosticsMath.SupportName(-1));
            Assert.AreEqual("none", BallDiagnosticsMath.SupportName(7));
        }

        [Test]
        public void OnlyAReportedSupportCountsAsSupported()
        {
            for (int kind = 0; kind <= 6; kind++)
            {
                Assert.IsTrue(BallDiagnosticsMath.HasSupport(kind), "kind " + kind);
            }
            Assert.IsFalse(BallDiagnosticsMath.HasSupport(-1),
                "an unsupported frame keeps the previous pair, so it is not a support");
        }
    }
}
