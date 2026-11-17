using NUnit.Framework;
using UnityEngine;
using VRCKatamari.Hub;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for the level-select list's rules: which catalogue entries a scene
    /// offers and how they are renumbered, and where the pads stand. The expected pad
    /// positions are hand-computed from "one row, centred on the room, one spacing apart".
    /// </summary>
    public class HubSelectMathTests
    {
        // ------------------------------------------------------------------ compaction

        [Test]
        public void CompactionClosesTheGapLeftByAnUnofferedEntry()
        {
            // Catalogue: level 0 is in the scene, level 1 is not, level 2 is.
            var offered = new[] { true, false, true };
            var source = new int[3];

            int count = HubSelectMath.CompactOffered(offered, source);

            Assert.AreEqual(2, count);
            Assert.AreEqual(0, source[0]);
            Assert.AreEqual(2, source[1]);
            Assert.AreEqual(-1, source[2]);
        }

        [Test]
        public void CompactionOfNothingOfferedIsEmpty()
        {
            var source = new[] { 7, 7 };

            Assert.AreEqual(0, HubSelectMath.CompactOffered(new[] { false, false }, source));
            Assert.AreEqual(-1, source[0]);
            Assert.AreEqual(-1, source[1]);
        }

        [Test]
        public void CompactionKeepsTheCatalogueOrder()
        {
            var source = new int[4];

            int count = HubSelectMath.CompactOffered(new[] { true, true, true, true }, source);

            Assert.AreEqual(4, count);
            for (int i = 0; i < 4; i++) Assert.AreEqual(i, source[i]);
        }

        [Test]
        public void CompactionRefusesMissingOrTooSmallBuffers()
        {
            Assert.AreEqual(0, HubSelectMath.CompactOffered(null, new int[2]));
            Assert.AreEqual(0, HubSelectMath.CompactOffered(new[] { true }, null));
            Assert.AreEqual(0, HubSelectMath.CompactOffered(new[] { true, true }, new int[1]));
        }

        // ------------------------------------------------------------------ ids

        [Test]
        public void UsableIdsAreNonEmptyAndNotJustWhitespace()
        {
            Assert.IsTrue(HubSelectMath.IsUsableId("_ref_cancer"));
            Assert.IsTrue(HubSelectMath.IsUsableId(" a "));
            Assert.IsFalse(HubSelectMath.IsUsableId(null));
            Assert.IsFalse(HubSelectMath.IsUsableId(""));
            Assert.IsFalse(HubSelectMath.IsUsableId("  \t"));
        }

        [Test]
        public void FirstDuplicateIdIsTheSecondOccurrence()
        {
            Assert.AreEqual(-1, HubSelectMath.FirstDuplicateId(new[] { "a", "b", "c" }));
            Assert.AreEqual(2, HubSelectMath.FirstDuplicateId(new[] { "a", "b", "a", "b" }));
            Assert.AreEqual(1, HubSelectMath.FirstDuplicateId(new[] { "x", "x" }));
        }

        [Test]
        public void DuplicateCheckIsCaseSensitiveAndIgnoresUnusableIds()
        {
            // Level ids are directory names; "A" and "a" are two levels.
            Assert.AreEqual(-1, HubSelectMath.FirstDuplicateId(new[] { "A", "a" }));
            Assert.AreEqual(-1, HubSelectMath.FirstDuplicateId(new[] { "", "", null }));
            Assert.AreEqual(-1, HubSelectMath.FirstDuplicateId(null));
            Assert.AreEqual(-1, HubSelectMath.FirstDuplicateId(new string[0]));
        }

        // ------------------------------------------------------------------ world spawn

        [Test]
        public void ALevelImportOwnsTheWorldSpawnOnlyInASceneWithoutAHub()
        {
            // The importer asks this before writing the descriptor's spawns: a scene with a
            // HubSpawn keeps the player in the hub.
            Assert.IsTrue(HubSelectMath.LevelMayOwnWorldSpawn(false));
            Assert.IsFalse(HubSelectMath.LevelMayOwnWorldSpawn(true));
        }

        // ------------------------------------------------------------------ pad layout

        [Test]
        public void OnePadStandsInTheMiddle()
        {
            Assert.AreEqual(0f, HubSelectMath.PadOffsetX(0, 1, 3.4f), 1e-6f);
        }

        [Test]
        public void TwoPadsStandSymmetricallyHalfASpacingFromTheCentre()
        {
            // Spacing 3.4 m: the pads' centres are at -1.7 and +1.7.
            Assert.AreEqual(-1.7f, HubSelectMath.PadOffsetX(0, 2, 3.4f), 1e-5f);
            Assert.AreEqual(1.7f, HubSelectMath.PadOffsetX(1, 2, 3.4f), 1e-5f);
        }

        [Test]
        public void ThreePadsPutTheMiddleOneAtTheCentre()
        {
            Assert.AreEqual(-3.4f, HubSelectMath.PadOffsetX(0, 3, 3.4f), 1e-5f);
            Assert.AreEqual(0f, HubSelectMath.PadOffsetX(1, 3, 3.4f), 1e-5f);
            Assert.AreEqual(3.4f, HubSelectMath.PadOffsetX(2, 3, 3.4f), 1e-5f);
        }

        [Test]
        public void SpacingIsPadPlusGap()
        {
            Assert.AreEqual(3.4f, HubSelectMath.PadSpacing(2.8f, 0.6f), 1e-6f);
        }

        [Test]
        public void PadCentreIsTheRoomCentreShiftedAlongTheRow()
        {
            // The hub room is centred on (1200, 0, 0); a pad sits 1.2 m toward +Z.
            Vector3 center;

            Assert.IsTrue(HubSelectMath.TryPadCenter(new Vector3(1200f, 0f, 0f), 1.2f, 1, 2,
                2.8f, 0.6f, out center));

            Assert.AreEqual(1201.7f, center.x, 1e-3f);
            Assert.AreEqual(0f, center.y, 1e-6f);
            Assert.AreEqual(1.2f, center.z, 1e-6f);
        }

        [Test]
        public void PadCentreRefusesBadIndexAndBadNumbers()
        {
            Vector3 center;
            Vector3 room = new Vector3(1200f, 0f, 0f);

            Assert.IsFalse(HubSelectMath.TryPadCenter(room, 1.2f, 2, 2, 2.8f, 0.6f, out center));
            Assert.IsFalse(HubSelectMath.TryPadCenter(room, 1.2f, -1, 2, 2.8f, 0.6f, out center));
            Assert.IsFalse(HubSelectMath.TryPadCenter(room, 1.2f, 0, 0, 2.8f, 0.6f, out center));
            Assert.IsFalse(HubSelectMath.TryPadCenter(room, 1.2f, 0, 1, 0f, 0.6f, out center));
            Assert.IsFalse(HubSelectMath.TryPadCenter(room, 1.2f, 0, 1, 2.8f, -0.1f, out center));
            Assert.IsFalse(HubSelectMath.TryPadCenter(room, float.NaN, 0, 1, 2.8f, 0.6f,
                out center));
            Assert.IsFalse(HubSelectMath.TryPadCenter(
                new Vector3(float.PositiveInfinity, 0f, 0f), 1.2f, 0, 1, 2.8f, 0.6f, out center));
        }

        // ------------------------------------------------------------------ room width

        [Test]
        public void RoomWidthCountsPadsGapsAndBothMargins()
        {
            // 2 pads of 2.8, one 0.6 gap, 0.5 margin each side: 5.6 + 0.6 + 1.0 = 7.2.
            Assert.AreEqual(7.2f, HubSelectMath.RoomWidthNeeded(2, 2.8f, 0.6f, 0.5f), 1e-5f);
            // One pad has no gap: 2.8 + 1.0 = 3.8.
            Assert.AreEqual(3.8f, HubSelectMath.RoomWidthNeeded(1, 2.8f, 0.6f, 0.5f), 1e-5f);
        }

        [Test]
        public void TheEightMetreHubHoldsTwoPadsButNotThree()
        {
            Assert.IsTrue(HubSelectMath.PadsFit(2, 2.8f, 0.6f, 0.5f, 8f));
            // 3 pads need 8.4 + 1.2 + 1.0 = 10.6 m.
            Assert.IsFalse(HubSelectMath.PadsFit(3, 2.8f, 0.6f, 0.5f, 8f));
            Assert.AreEqual(10.6f, HubSelectMath.RoomWidthNeeded(3, 2.8f, 0.6f, 0.5f), 1e-4f);
        }

        [Test]
        public void RoomWidthOfNothingOrBadInputIsZeroAndNothingFits()
        {
            Assert.AreEqual(0f, HubSelectMath.RoomWidthNeeded(0, 2.8f, 0.6f, 0.5f));
            Assert.AreEqual(0f, HubSelectMath.RoomWidthNeeded(1, 0f, 0.6f, 0.5f));
            Assert.AreEqual(0f, HubSelectMath.RoomWidthNeeded(1, 2.8f, -1f, 0.5f));
            Assert.AreEqual(0f, HubSelectMath.RoomWidthNeeded(1, 2.8f, 0.6f, float.NaN));
            Assert.IsFalse(HubSelectMath.PadsFit(0, 2.8f, 0.6f, 0.5f, 100f));
            Assert.IsFalse(HubSelectMath.PadsFit(1, 2.8f, 0.6f, 0.5f, float.NaN));
        }
    }
}
