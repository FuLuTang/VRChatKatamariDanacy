using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using VRCKatamari.Pickup;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// Pins the <c>propMobile</c> column rules of the pickup-field authoring path: which motion
    /// rows may move their prop's Transform, and how the column is resolved and preserved.
    /// </summary>
    /// <remarks>
    /// The rules live in the editor assembly, which this test assembly does not reference, so
    /// they are called by reflection (same reason and helper shape as
    /// <c>PickupFieldAuthoringRulesTests</c>). The class codes are the runtime's own constants
    /// (<see cref="MotionHostMath"/>), so a renumbering cannot silently change the meaning.
    /// </remarks>
    public class PickupMobileColumnTests
    {
        private const string RulesTypeName = "VRCKatamari.Editor.Pickup.PickupFieldAuthoringRules";

        private const int None = MotionHostMath.InheritNone;
        private const int Full = MotionHostMath.InheritFull;

        private static HashSet<string> Mobile(string[] ids, int[] parents, int[] inherit, int[] classes)
        {
            return (HashSet<string>)Invoke("FindMobileMotionItemIds",
                new object[] { ids, parents, inherit, classes });
        }

        [Test]
        public void EveryClassThatWritesAPoseIsMobileAndTheTriggerGateAndNoProgramAreNot()
        {
            var movers = new[]
            {
                MotionHostMath.ClassSpin, MotionHostMath.ClassFlip, MotionHostMath.ClassBob,
                MotionHostMath.ClassSway, MotionHostMath.ClassWhackAMole, MotionHostMath.ClassHop,
                MotionHostMath.ClassSporadicWalk, MotionHostMath.ClassClouds,
                MotionHostMath.ClassPathCollision, MotionHostMath.ClassSlopingPath,
                MotionHostMath.ClassBackAndForth,
            };
            foreach (int cls in movers)
            {
                HashSet<string> set = Mobile(new[] { "a" }, new[] { -1 }, new[] { None },
                    new[] { cls });
                Assert.IsTrue(set.Contains("a"), "class " + cls + " writes a pose");
            }

            // WaitForPlayer runs a gate, DraggedAlong has no program of its own, None has none.
            foreach (int cls in new[] { MotionHostMath.ClassWaitForPlayer,
                MotionHostMath.ClassDraggedAlong, MotionHostMath.ClassNone })
            {
                HashSet<string> set = Mobile(new[] { "a" }, new[] { -1 }, new[] { None },
                    new[] { cls });
                Assert.IsFalse(set.Contains("a"), "class " + cls + " does not move its Transform");
            }
        }

        [Test]
        public void ARiderIsMobileExactlyWhenItsParentRowIs()
        {
            // 0: a sway (moves); 1: rides 0 (moves with it); 2: rides 1 (moves, chain);
            // 3: a static prop; 4: rides the static prop (stays); 5: WaitForPlayer rider on 0
            // (gate only, but it IS carried by its moving parent).
            string[] ids = { "pan", "crab", "egg", "table", "cup", "guard" };
            int[] parents = { -1, 0, 1, -1, 3, 0 };
            int[] inherit = { None, Full, Full, None, Full, Full };
            int[] classes =
            {
                MotionHostMath.ClassSway, MotionHostMath.ClassNone, MotionHostMath.ClassNone,
                MotionHostMath.ClassNone, MotionHostMath.ClassNone, MotionHostMath.ClassWaitForPlayer,
            };
            HashSet<string> set = Mobile(ids, parents, inherit, classes);
            Assert.IsTrue(set.Contains("pan"));
            Assert.IsTrue(set.Contains("crab"));
            Assert.IsTrue(set.Contains("egg"), "a rider of a rider of a mover moves");
            Assert.IsFalse(set.Contains("table"));
            Assert.IsFalse(set.Contains("cup"), "a rider on a prop that never moves stays put");
            Assert.IsTrue(set.Contains("guard"), "carried by its moving parent");
            Assert.AreEqual(4, set.Count);
        }

        [Test]
        public void AParentLinkWithInheritNoneOrAParentThatDoesNotPrecedeCarriesNothing()
        {
            string[] ids = { "mover", "linked", "late" };
            int[] parents = { -1, 0, 5 };
            int[] inherit = { None, None, Full };
            int[] classes =
            {
                MotionHostMath.ClassHop, MotionHostMath.ClassNone, MotionHostMath.ClassNone,
            };
            HashSet<string> set = Mobile(ids, parents, inherit, classes);
            Assert.IsTrue(set.Contains("mover"));
            Assert.IsFalse(set.Contains("linked"), "inherit none: no carry");
            Assert.IsFalse(set.Contains("late"), "a parent row that does not precede is ignored");
        }

        [Test]
        public void EmptyAndMismatchedInputsGiveAnEmptySetWithoutThrowing()
        {
            Assert.AreEqual(0, Mobile(null, null, null, null).Count);
            Assert.AreEqual(0, Mobile(new string[0], new int[0], new int[0], new int[0]).Count);
            // Shorter parallel arrays bound the walk instead of overrunning.
            HashSet<string> set = Mobile(new[] { "a", "b" }, new[] { -1 }, new[] { None },
                new[] { MotionHostMath.ClassSpin });
            Assert.IsTrue(set.Contains("a"));
            Assert.IsFalse(set.Contains("b"));
        }

        [Test]
        public void TheColumnIsTrueExactlyForTheDeclaredIds()
        {
            var declared = new HashSet<string> { "b", "d", "zzz" };
            bool[] column = (bool[])Invoke("ResolveMobileColumn",
                new object[] { new[] { "a", "b", "c", "d", "" }, declared });
            CollectionAssert.AreEqual(new[] { false, true, false, true, false }, column);
            bool[] none = (bool[])Invoke("ResolveMobileColumn",
                new object[] { new[] { "a", "b" }, null });
            CollectionAssert.AreEqual(new[] { false, false }, none);
        }

        [Test]
        public void AReSyncKeepsKnownIdsAndMakesUnknownIdsMobile()
        {
            var existing = new Dictionary<string, bool>
            {
                { "still", false }, { "moves", true },
            };
            bool[] column = (bool[])Invoke("PreserveMobileColumn",
                new object[] { new[] { "still", "moves", "new", null }, existing });
            // An id with no value yet is read live every scan: the safe default.
            CollectionAssert.AreEqual(new[] { false, true, true, true }, column);

            // No column at all (a scene synced before it existed): every row is mobile, which is
            // exactly the old read-everything behaviour.
            bool[] fresh = (bool[])Invoke("PreserveMobileColumn",
                new object[] { new[] { "x", "y" }, null });
            CollectionAssert.AreEqual(new[] { true, true }, fresh);
        }

        // ---- reflection -------------------------------------------------------------------

        private static object Invoke(string methodName, object[] arguments)
        {
            Type type = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length && type == null; i++)
            {
                type = assemblies[i].GetType(RulesTypeName, false);
            }
            Assert.IsNotNull(type, "type '" + RulesTypeName + "' is not loaded; it lives in " +
                "Assets/VRC_Katamari/Editor/Pickup and compiles into the editor assembly");
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, RulesTypeName + "." + methodName + " is missing");
            return method.Invoke(null, arguments);
        }
    }
}
