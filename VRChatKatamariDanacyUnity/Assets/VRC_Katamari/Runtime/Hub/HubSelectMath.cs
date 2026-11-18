using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// The level-select list's rules: which catalogue entries a scene can offer, how the
    /// offered ones are numbered, and where their pads stand in the room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One implementation, no scene. The editor assembler (Editor/Hub/HubAssembler) reads
    /// the catalogue and the open scene, and asks this class every question it would
    /// otherwise answer with its own arithmetic, so the pad layout and the index mapping
    /// exist once and are tested without Unity.
    /// </para>
    /// <para>
    /// The index that matters is the compact one. <c>HubLevelLauncher.levelEntries[i]</c>,
    /// <c>HubRoomHost.levelButtons[i]</c> and a <c>HubStartButton.levelIndex</c> all mean the
    /// same level for the same <c>i</c>, so the list that is written into the scene must
    /// have no holes: a catalogue entry that the scene cannot play (its level was never
    /// imported into this scene) is skipped, and the entries after it close up.
    /// </para>
    /// <para>
    /// Pads stand in one row along the room's X axis, centred on the room, one pad per
    /// offered level. A row is the simplest thing that scales: adding a level widens the
    /// row by one pad and one gap, and <see cref="RoomWidthNeeded"/> says how wide the room
    /// has to be, so the assembler can warn that the hub room's document needs widening
    /// instead of letting a pad hang through a wall.
    /// </para>
    /// </remarks>
    public static class HubSelectMath
    {
        /// <summary>
        /// Writes, for each offered entry, the index it had in the catalogue, and returns
        /// how many entries are offered.
        /// </summary>
        /// <param name="offered">
        /// One flag per catalogue entry: the scene holds that level's content and its entry
        /// point. A null array or a null buffer offers nothing.
        /// </param>
        /// <param name="sourceIndexOfCompact">
        /// Caller-owned buffer, at least as long as <paramref name="offered"/>. Slot
        /// <c>k</c> receives the catalogue index of the <c>k</c>-th offered entry; slots
        /// from the returned count onward are set to -1.
        /// </param>
        public static int CompactOffered(bool[] offered, int[] sourceIndexOfCompact)
        {
            if (offered == null || sourceIndexOfCompact == null) return 0;
            if (sourceIndexOfCompact.Length < offered.Length) return 0;

            for (int i = 0; i < sourceIndexOfCompact.Length; i++)
            {
                sourceIndexOfCompact[i] = -1;
            }

            int count = 0;
            for (int i = 0; i < offered.Length; i++)
            {
                if (!offered[i]) continue;
                sourceIndexOfCompact[count] = i;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Whether an identifier can be a catalogue key: not null, not empty, and not made
        /// of whitespace only.
        /// </summary>
        public static bool IsUsableId(string id)
        {
            if (id == null) return false;
            for (int i = 0; i < id.Length; i++)
            {
                if (!char.IsWhiteSpace(id[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// The index of the first entry whose id repeats an earlier one (ordinal compare),
        /// or -1 when every id is distinct. Unusable ids are not compared.
        /// </summary>
        public static int FirstDuplicateId(string[] ids)
        {
            if (ids == null) return -1;
            for (int i = 1; i < ids.Length; i++)
            {
                if (!IsUsableId(ids[i])) continue;
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(ids[i], ids[j])) return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Whether a level import may point the world spawn at the level's own spawn marker.
        /// False when the scene has a hub: the player is born in the hub and the level's
        /// spawn is only where a start sends them.
        /// </summary>
        public static bool LevelMayOwnWorldSpawn(bool sceneHasHubSpawn)
        {
            return !sceneHasHubSpawn;
        }

        /// <summary>The centre-to-centre distance of two neighbouring pads.</summary>
        public static float PadSpacing(float padSizeMeters, float gapMeters)
        {
            return padSizeMeters + gapMeters;
        }

        /// <summary>
        /// The pad's offset along the row from the room's centre: zero for the middle pad,
        /// symmetric about it, increasing with the index.
        /// </summary>
        public static float PadOffsetX(int index, int count, float spacingMeters)
        {
            return (index - (count - 1) * 0.5f) * spacingMeters;
        }

        /// <summary>
        /// The world centre of pad <paramref name="index"/> of <paramref name="count"/>:
        /// the room centre shifted along X by the pad's row offset, and by
        /// <paramref name="forwardMeters"/> along Z. False for an index outside the list, a
        /// non-positive size, a negative gap or any non-finite input.
        /// </summary>
        public static bool TryPadCenter(Vector3 roomCenter, float forwardMeters, int index,
            int count, float padSizeMeters, float gapMeters, out Vector3 center)
        {
            center = roomCenter;

            if (count < 1 || index < 0 || index >= count) return false;
            if (!KatamariMath.IsFiniteVector(roomCenter)) return false;
            if (!KatamariMath.IsFiniteFloat(forwardMeters)) return false;
            if (!KatamariMath.IsFiniteFloat(padSizeMeters) || padSizeMeters <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(gapMeters) || gapMeters < 0f) return false;

            float offsetX = PadOffsetX(index, count, PadSpacing(padSizeMeters, gapMeters));
            center = new Vector3(roomCenter.x + offsetX, roomCenter.y, roomCenter.z + forwardMeters);
            return true;
        }

        /// <summary>
        /// How wide the room must be, along the pad row, for <paramref name="count"/> pads
        /// with a free margin of <paramref name="marginMeters"/> on each side. Zero for
        /// no pads or unusable input.
        /// </summary>
        public static float RoomWidthNeeded(int count, float padSizeMeters, float gapMeters,
            float marginMeters)
        {
            if (count < 1) return 0f;
            if (!KatamariMath.IsFiniteFloat(padSizeMeters) || padSizeMeters <= 0f) return 0f;
            if (!KatamariMath.IsFiniteFloat(gapMeters) || gapMeters < 0f) return 0f;
            if (!KatamariMath.IsFiniteFloat(marginMeters) || marginMeters < 0f) return 0f;

            return count * padSizeMeters + (count - 1) * gapMeters + 2f * marginMeters;
        }

        /// <summary>Whether a room of <paramref name="roomWidthMeters"/> holds the pad row.</summary>
        public static bool PadsFit(int count, float padSizeMeters, float gapMeters,
            float marginMeters, float roomWidthMeters)
        {
            float needed = RoomWidthNeeded(count, padSizeMeters, gapMeters, marginMeters);
            if (needed <= 0f) return false;
            if (!KatamariMath.IsFiniteFloat(roomWidthMeters)) return false;
            return roomWidthMeters >= needed;
        }
    }
}
