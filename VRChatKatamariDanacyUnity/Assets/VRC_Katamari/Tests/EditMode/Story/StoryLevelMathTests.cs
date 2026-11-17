using NUnit.Framework;
using VRCKatamari.Story;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the per-level lookup rules shared by the multi-level round
    /// controller and the result presenter. The load-bearing claim: an empty or short
    /// table means "today's single level", so a scene serialized before the arrays
    /// existed behaves exactly as before.
    /// </summary>
    public class StoryLevelMathTests
    {
        [Test]
        public void GoalTypeValuesMatchTheSerializedInts()
        {
            Assert.AreEqual(StoryLevelMath.GoalReachDiameter, (int)StoryGoalType.ReachTargetDiameter);
            Assert.AreEqual(StoryLevelMath.GoalCollectCount, (int)StoryGoalType.CollectCategoryCount);
            Assert.AreEqual(StoryLevelMath.GoalFreeRoam, (int)StoryGoalType.FreeRoam);
            Assert.AreEqual(2, (int)StoryGoalType.FreeRoam);
        }

        [Test]
        public void IndexValidityIsBoundsOnly()
        {
            Assert.IsTrue(StoryLevelMath.IsIndexValid(0, 1));
            Assert.IsTrue(StoryLevelMath.IsIndexValid(3, 4));
            Assert.IsFalse(StoryLevelMath.IsIndexValid(4, 4));
            Assert.IsFalse(StoryLevelMath.IsIndexValid(-1, 4));
            Assert.IsFalse(StoryLevelMath.IsIndexValid(0, 0));
            Assert.IsFalse(StoryLevelMath.IsIndexValid(StoryLevelMath.NoLevel, 4));
        }

        [Test]
        public void SelectIsAcceptedOnlyInIdleOrResult()
        {
            Assert.IsTrue(StoryLevelMath.CanSelectInState(0));
            Assert.IsFalse(StoryLevelMath.CanSelectInState(1));
            Assert.IsFalse(StoryLevelMath.CanSelectInState(2));
            Assert.IsTrue(StoryLevelMath.CanSelectInState(3));
        }

        [Test]
        public void AbortIsRefusedWithoutATableOrALevel()
        {
            Assert.IsFalse(StoryLevelMath.CanAbortLevel(null, 0));
            Assert.IsFalse(StoryLevelMath.CanAbortLevel(new bool[0], 0));
            Assert.IsFalse(StoryLevelMath.CanAbortLevel(new bool[] { true, true }, StoryLevelMath.NoLevel));
            Assert.IsFalse(StoryLevelMath.CanAbortLevel(new bool[] { true, true }, 2));
        }

        [Test]
        public void AbortFollowsThePerLevelFlag()
        {
            bool[] allow = new bool[] { true, false, true };
            Assert.IsTrue(StoryLevelMath.CanAbortLevel(allow, 0));
            Assert.IsFalse(StoryLevelMath.CanAbortLevel(allow, 1));
            Assert.IsTrue(StoryLevelMath.CanAbortLevel(allow, 2));
        }

        [Test]
        public void OnlyFreeRoamIsUntimed()
        {
            Assert.IsTrue(StoryLevelMath.IsTimedGoal(StoryLevelMath.GoalReachDiameter));
            Assert.IsTrue(StoryLevelMath.IsTimedGoal(StoryLevelMath.GoalCollectCount));
            Assert.IsFalse(StoryLevelMath.IsTimedGoal(StoryLevelMath.GoalFreeRoam));
            Assert.IsTrue(StoryLevelMath.IsKnownGoal(2));
            Assert.IsFalse(StoryLevelMath.IsKnownGoal(3));
            Assert.IsFalse(StoryLevelMath.IsKnownGoal(-1));
        }

        [Test]
        public void PickersFallBackWhenTheTableHasNoEntry()
        {
            Assert.AreEqual(1.45f, StoryLevelMath.PickFloat(null, 0, 1.45f));
            Assert.AreEqual(1.45f, StoryLevelMath.PickFloat(new float[0], 0, 1.45f));
            Assert.AreEqual(1.45f, StoryLevelMath.PickFloat(new float[] { 2f }, -1, 1.45f));
            Assert.AreEqual(2f, StoryLevelMath.PickFloat(new float[] { 2f }, 0, 1.45f));
            Assert.AreEqual(20, StoryLevelMath.PickInt(null, 1, 20));
            Assert.AreEqual(20, StoryLevelMath.PickInt(new int[] { 5 }, 1, 20));
            Assert.AreEqual(5, StoryLevelMath.PickInt(new int[] { 5 }, 0, 20));
            Assert.IsTrue(StoryLevelMath.PickBool(null, 0, true));
            Assert.IsFalse(StoryLevelMath.PickBool(new bool[] { false }, 0, true));
        }

        [Test]
        public void StarsAreOnByDefaultAndOffOnlyWhenTheLevelSaysSo()
        {
            // No table (every scene before this field): stars, as today.
            Assert.IsTrue(StoryLevelMath.LevelUsesStars(null, 0));
            Assert.IsTrue(StoryLevelMath.LevelUsesStars(new bool[0], 0));
            Assert.IsTrue(StoryLevelMath.LevelUsesStars(new bool[] { false }, StoryLevelMath.NoLevel));

            bool[] has = new bool[] { true, false };
            Assert.IsTrue(StoryLevelMath.LevelUsesStars(has, 0));
            Assert.IsFalse(StoryLevelMath.LevelUsesStars(has, 1));
            Assert.IsTrue(StoryLevelMath.LevelUsesStars(has, 2));
        }
    }
}
