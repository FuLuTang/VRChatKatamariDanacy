using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The sync's id-to-row resolution of the parent column
    /// (<see cref="PickupFieldAuthoringRules.ResolveParentColumn"/>). Pure: no host, no scene.
    /// Writing the column through the SerializedObject and its read-back need Unity.
    /// </summary>
    public class PropParentColumnTests
    {
        // The rules class lives in Assembly-CSharp-Editor, which the test assembly cannot
        // reference, so it is reached by reflection (same as the authoring-rules tests).
        private static int[] Resolve(string[] rows, IDictionary<string, string> parentOf,
            out int unresolved, out int cut)
        {
            Type type = Type.GetType("VRCKatamari.Editor.Pickup.PickupFieldAuthoringRules, Assembly-CSharp-Editor");
            Assert.IsNotNull(type, "PickupFieldAuthoringRules not found");
            MethodInfo method = type.GetMethod("ResolveParentColumn");
            var args = new object[] { rows, parentOf, 0, 0 };
            var column = (int[])method.Invoke(null, args);
            unresolved = (int)args[2];
            cut = (int)args[3];
            return column;
        }

        [Test]
        public void ResolvesIdsToRowIndicesIndependentOfDeclarationOrder()
        {
            string[] rows = { "b", "a", "c" };
            var parentOf = new Dictionary<string, string> { { "c", "a" }, { "b", "a" } };
            int unresolved;
            int cut;
            int[] column = Resolve(rows, parentOf, out unresolved,
                out cut);
            CollectionAssert.AreEqual(new[] { 1, -1, 1 }, column);
            Assert.AreEqual(0, unresolved);
            Assert.AreEqual(0, cut);

            // Same data, map filled in the other order: the same column (idempotent).
            var reversed = new Dictionary<string, string> { { "b", "a" }, { "c", "a" } };
            int[] again = Resolve(rows, reversed, out unresolved,
                out cut);
            CollectionAssert.AreEqual(column, again);
        }

        [Test]
        public void ParentWithoutARowAndSelfEdgeAreCountedAndBecomeNone()
        {
            string[] rows = { "a", "b" };
            var parentOf = new Dictionary<string, string> { { "a", "ghost" }, { "b", "b" } };
            int unresolved;
            int cut;
            int[] column = Resolve(rows, parentOf, out unresolved,
                out cut);
            CollectionAssert.AreEqual(new[] { -1, -1 }, column);
            Assert.AreEqual(2, unresolved);
        }

        [Test]
        public void CycleIsCutAndCounted()
        {
            string[] rows = { "a", "b", "c" };
            var parentOf = new Dictionary<string, string> { { "a", "b" }, { "b", "c" }, { "c", "a" } };
            int unresolved;
            int cut;
            int[] column = Resolve(rows, parentOf, out unresolved,
                out cut);
            Assert.AreEqual(1, cut);
            CollectionAssert.AreEqual(new[] { -1, 2, 0 }, column);
        }

        [Test]
        public void NullMapAndEmptyRowsGiveAnEmptyOrAllNoneColumn()
        {
            int unresolved;
            int cut;
            int[] column = Resolve(new[] { "a", "b" }, null,
                out unresolved, out cut);
            CollectionAssert.AreEqual(new[] { -1, -1 }, column);
            Assert.AreEqual(0, Resolve(new string[0], null,
                out unresolved, out cut).Length);
        }
    }
}
