using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the round's core loop against the optional result presenter: the round has to
    /// start, time, settle, freeze and reach Result with no presenter attached at all, and
    /// it must not carry a single member that names scoring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection only, for the same reason as BankReasonTests: this test assembly does not
    /// reference UdonSharp.Runtime, so an UdonSharpBehaviour type cannot appear in a field
    /// or a signature here. The behaviour is added by <see cref="Type"/> and driven through
    /// its public methods (<c>Update</c> is public in that file), which is enough for a
    /// state machine and needs no scene, motor, pickup host or card.
    /// </para>
    /// <para>
    /// Every scene link is left null on purpose: a null link is documented as skipped, so
    /// this is the "round with nothing wired but its own clock" configuration - the one
    /// that must keep working when the result card is not in the scene. The facts it
    /// reports therefore read 0, which is also what a ball at rest under every threshold
    /// would report.
    /// </para>
    /// </remarks>
    public class StoryRoundControllerTests
    {
        private const string ControllerTypeName =
            "VRCKatamari.Story.StoryRoundController, VRCKatamari.Runtime";

        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private GameObject host;
        private Component controller;
        private Type controllerType;

        [SetUp]
        public void SetUp()
        {
            controllerType = Type.GetType(ControllerTypeName);
            if (controllerType == null)
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t0 = a.GetType("VRCKatamari.Story.StoryRoundController");
                    if (t0 != null) { controllerType = t0; break; }
                }
            }
            Assert.IsNotNull(controllerType, "StoryRoundController type not loaded");

            host = new GameObject("StoryRoundControllerTests");
            controller = host.AddComponent(controllerType);
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
        }

        [Test]
        public void CoreSettlesWithoutAResultPresenter()
        {
            // The decoupling claim, checked on the type itself: the round must not know
            // that scoring exists. A regression that puts the star table or a presenter
            // reference back into the core loop fails here, before any scene does.
            AssertNoScoringOnTheRoundType();

            int stateIdle = StateConstant("StateIdle");
            int stateRunning = StateConstant("StateRunning");
            int statePresenting = StateConstant("StatePresenting");
            int stateResult = StateConstant("StateResult");

            // The ordinary start of a played level: it waits, it does not count.
            Invoke("Start");
            Assert.AreEqual(stateIdle, GetState(), "a level without startOnLoad waits in Idle");
            Assert.AreEqual(false, (bool)Invoke("RequestRetry"),
                "a retry before any result must be refused");

            // The sit-down seam begins the round; the clock starts at the level's limit.
            Invoke("BeginRound");
            Assert.AreEqual(stateRunning, GetState(), "BeginRound starts the countdown");
            Assert.AreEqual(120f, Convert.ToSingle(Invoke("GetRemainingSeconds")), 1e-3f,
                "the countdown starts at the level's time_limit_s");
            Assert.AreEqual(0f, Convert.ToSingle(Invoke("GetResultDiameterM")),
                "nothing has settled yet");

            // Move the recorded start into the past, which is what a running clock looks
            // like after the limit; one Update is enough to settle the round.
            SetField("startTimeSeconds", Time.time - 1000f);
            Invoke("Update");
            Assert.AreEqual(statePresenting, GetState(), "the bell freezes the round");
            Assert.AreEqual(0f, Convert.ToSingle(Invoke("GetRemainingSeconds")), 1e-4f,
                "a settled round reads a remaining time of 0, never negative");
            Assert.AreEqual(0f, Convert.ToSingle(Invoke("GetResultDiameterM")), 1e-4f,
                "with no motor wired the settled diameter is the documented 0");
            Assert.AreEqual(0, Convert.ToInt32(Invoke("GetResultCount")),
                "a size level reports no count");

            // The presenting beat ends on the round's own clock. Nothing outside the round
            // tells it that the result now exists.
            SetField("presentingSeconds", 0f);
            Invoke("Update");
            Assert.AreEqual(stateResult, GetState(), "Presenting reaches Result with no presenter");

            // Retry stays the round's own gate and stays safe with nothing wired.
            Assert.AreEqual(false, (bool)Invoke("RequestRetry"),
                "a retry with no motor is refused");
            Assert.AreEqual(stateResult, GetState(),
                "a refused retry leaves the round where it was");

            // The facts are still readable after the refusal, and a new round clears them.
            Assert.AreEqual(0f, Convert.ToSingle(Invoke("GetResultDiameterM")), 1e-4f);
            Invoke("BeginRound");
            Assert.AreEqual(stateRunning, GetState(), "a new round can follow without a presenter");
            Assert.AreEqual(0, Convert.ToInt32(Invoke("GetAttemptNumber")),
                "BeginRound alone is not a retry");
        }

        /// <summary>
        /// Fails if any declared member of the round names the star table or the result
        /// presenter, or if a scoring member that this split removed comes back.
        /// </summary>
        private void AssertNoScoringOnTheRoundType()
        {
            FieldInfo[] fields = controllerType.GetFields(Any);
            for (int i = 0; i < fields.Length; i++)
            {
                string typeName = fields[i].FieldType.Name;
                Assert.AreNotEqual("StoryStarMath", typeName,
                    "the round must not hold the star table: field " + fields[i].Name);
                Assert.AreNotEqual("StoryResultPresenter", typeName,
                    "the round must not hold a result presenter: field " + fields[i].Name);
            }

            MethodInfo[] methods = controllerType.GetMethods(Any);
            for (int i = 0; i < methods.Length; i++)
            {
                string returnTypeName = methods[i].ReturnType.Name;
                Assert.AreNotEqual("StoryStarMath", returnTypeName,
                    "the round must not return star math: method " + methods[i].Name);
                Assert.AreNotEqual("StoryResultPresenter", returnTypeName,
                    "the round must not return a result presenter: method " + methods[i].Name);
                ParameterInfo[] parameters = methods[i].GetParameters();
                for (int p = 0; p < parameters.Length; p++)
                {
                    string parameterTypeName = parameters[p].ParameterType.Name;
                    Assert.AreNotEqual("StoryStarMath", parameterTypeName,
                        "the round must not take star math: method " + methods[i].Name);
                    Assert.AreNotEqual("StoryResultPresenter", parameterTypeName,
                        "the round must not take a result presenter: method " + methods[i].Name);
                }
            }

            // The members this split deleted. Their names are the contract that scoring
            // moved out; a name coming back means the boundary moved back with it.
            Assert.IsNull(controllerType.GetField("resultStars", Any),
                "the round must not carry a star count");
            Assert.IsNull(controllerType.GetField("resultInsidePartialBand", Any),
                "the round must not carry a partial-band verdict");
            Assert.IsNull(controllerType.GetMethod("GetResultStars", Any),
                "the round must not expose stars");
            Assert.IsNull(controllerType.GetMethod("ShowResult", Any),
                "the result card is the presenter's to draw");
            Assert.IsNull(controllerType.GetMethod("EvaluateSettledRound", Any),
                "settling records facts, it does not grade them");
            Assert.IsNull(controllerType.GetMethod("StarGlyphs", Any),
                "star glyphs are presentation");
            Assert.IsNull(controllerType.GetMethod("CommentLine", Any),
                "the comment line is presentation");
        }

        private int StateConstant(string name)
        {
            FieldInfo field = controllerType.GetField(name,
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(field, name + " not found");
            return (int)field.GetValue(null);
        }

        private int GetState()
        {
            return Convert.ToInt32(Invoke("GetState"));
        }

        private object Invoke(string name, params object[] args)
        {
            MethodInfo method = controllerType.GetMethod(name, Any);
            Assert.IsNotNull(method, name + " not found");
            return method.Invoke(controller, args);
        }

        private void SetField(string name, object value)
        {
            FieldInfo field = controllerType.GetField(name, Any);
            Assert.IsNotNull(field, name + " not found");
            field.SetValue(controller, value);
        }
    }
}
