using System;
using System.Collections.Generic;

namespace VRCKatamari.Editor.Pickup
{
    /// <summary>
    /// The pure half of the pickup-field authoring path: size tier from bounds, value-unit
    /// lookup by kind and tier, duplicate-id detection and deterministic path ordering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No Unity scene, component or asset is touched here, so the rules can be exercised by
    /// plain NUnit tests (<c>Tests/EditMode/Pickup/PickupFieldAuthoringRulesTests.cs</c>).
    /// The scene walk, the SerializedObject write and the UdonSharp copy live in
    /// <see cref="PickupFieldSync"/>; this type only answers questions about values.
    /// </para>
    /// <para>
    /// Why these rules exist at all. <c>Runtime/Pickup/PickupFieldHost.cs</c> holds six
    /// index-aligned serialized arrays, and the host is the only runtime owner of that data.
    /// An editor-only MonoBehaviour cannot carry the authoring data instead: a component
    /// whose script lives in an Editor assembly has no runtime type, so Unity cannot
    /// serialize it into a built scene. The authoring path therefore derives the rows from
    /// the props that already exist in the scene (GameObject name, hierarchy position,
    /// renderer bounds, colliders) and writes them into the host. This class is the part of
    /// that derivation that is decidable without looking at a scene.
    /// </para>
    /// <para>
    /// Values are whole units at least 1, because the ledger rejects anything else as
    /// <c>RejectNoGain</c> (Docs/OPEN_DECISIONS.md (j); the runtime mirror is
    /// <c>PickupAbsorbMath.TryAbsorbValueToLedgerGain</c>). The table below is explicit:
    /// an unknown kind, or a known kind in a tier it does not declare, is reported as a
    /// rejected row instead of being given a silent default. Adding a kind is a one-line
    /// edit here, and that is deliberate - the project has already paid for values that
    /// looked plausible but were never declared anywhere.
    /// </para>
    /// <para>
    /// The tier boundaries and the default rows are PROVISIONAL. They are read off the gaps
    /// in the PicnicLawn prop catalog (<c>Docs/Design/Maps/PicnicLawn/unity_greybox.json</c>:
    /// small props top out at 0.18 m, medium at 0.72 m, large start at 0.80 m), not measured
    /// original-game values, and the table records the catalog's dominant value per
    /// (kind, tier). Where the catalog varies inside one tier (food m 4/6, drink m 4/5,
    /// tableware m 3/4, container l 16/20), one row cannot carry both numbers: the table
    /// records the lower one and the level author who needs the other value adds a row for
    /// their own kind or tier here, instead of hand-editing the six arrays.
    /// </para>
    /// </remarks>
    public static class PickupFieldAuthoringRules
    {
        // The ledger consumes whole units and refuses anything below 1
        // (Docs/OPEN_DECISIONS.md (j), Runtime/Pickup/PickupAbsorbMath.cs:259-271).
        public const int MinValueUnits = 1;

        // A prop whose longest renderer extent is below this has no usable size: an AABB
        // that small is a missing renderer or a degenerate mesh, not a tiny prop.
        public const float MinBoundsExtentM = 0.0001f;

        // Tier bands, metres of the longest AABB extent. PROVISIONAL: the s/m gap in the
        // PicnicLawn catalog is 0.18 -> 0.24 m and the m/l gap is 0.72 -> 0.80 m, so the
        // boundaries sit in the middle of those gaps.
        public const float SmallTierMaxExtentM = 0.21f;
        public const float MediumTierMaxExtentM = 0.76f;

        public const string TierSmall = "s";
        public const string TierMedium = "m";
        public const string TierLarge = "l";

        // Explicitly declared (kind, tier) -> value units. Rows come from the PicnicLawn
        // catalog for the categories that are absorbable props; categories whose catalog
        // value is 0 (boundary, gameplay, landmark, terrain) deliberately have no row and
        // are refused by the sync instead of shipping an unabsorbable prop.
        private sealed class ValueRow
        {
            public ValueRow(string kind, string tier, int units)
            {
                Kind = kind;
                Tier = tier;
                Units = units;
            }

            public readonly string Kind;
            public readonly string Tier;
            public readonly int Units;
        }

        private static readonly ValueRow[] ValueTable =
        {
            new ValueRow("debris", TierSmall, 1),
            new ValueRow("food", TierSmall, 1),
            new ValueRow("nature", TierSmall, 1),
            new ValueRow("drink", TierMedium, 4),
            new ValueRow("food", TierMedium, 4),
            new ValueRow("garden", TierMedium, 6),
            new ValueRow("tableware", TierMedium, 4),
            new ValueRow("toy", TierMedium, 4),
            new ValueRow("container", TierLarge, 16),
            new ValueRow("furniture", TierLarge, 16),
            new ValueRow("garden", TierLarge, 16),
        };

        /// <summary>
        /// Classifies a full-size AABB into the authoring's size tier.
        /// </summary>
        /// <returns>
        /// <c>"s"</c>, <c>"m"</c> or <c>"l"</c>; the empty string when the bounds cannot
        /// describe a prop (negative, non-finite, or shorter than
        /// <see cref="MinBoundsExtentM"/>).
        /// </returns>
        public static string ClassifySizeTier(float sizeX, float sizeY, float sizeZ)
        {
            if (!IsFiniteNonNegative(sizeX) || !IsFiniteNonNegative(sizeY) || !IsFiniteNonNegative(sizeZ))
            {
                return string.Empty;
            }

            float longest = sizeX;
            if (sizeY > longest)
            {
                longest = sizeY;
            }

            if (sizeZ > longest)
            {
                longest = sizeZ;
            }

            if (longest < MinBoundsExtentM)
            {
                return string.Empty;
            }

            if (longest <= SmallTierMaxExtentM)
            {
                return TierSmall;
            }

            if (longest <= MediumTierMaxExtentM)
            {
                return TierMedium;
            }

            return TierLarge;
        }

        /// <summary>
        /// True when the table declares a value for this (kind, tier) pair, whatever the
        /// declared number is.
        /// </summary>
        /// <remarks>
        /// Kept separate from <see cref="LookupValueUnits"/> so "this kind+tier is not
        /// declared" and "this row declares an illegal value" stay distinguishable: the
        /// second is a table bug, and the report must be able to name it instead of telling
        /// the author the row does not exist.
        /// </remarks>
        public static bool HasValueRow(string kind, string tier)
        {
            int units;
            return FindValueUnits(kind, tier, out units);
        }

        /// <summary>
        /// Looks up the declared value unit of a (kind, tier) pair.
        /// </summary>
        /// <returns>The declared units, or 0 when no row declares this pair.</returns>
        public static int LookupValueUnits(string kind, string tier)
        {
            int units;
            return FindValueUnits(kind, tier, out units) ? units : 0;
        }

        private static bool FindValueUnits(string kind, string tier, out int units)
        {
            units = 0;
            if (kind == null || tier == null)
            {
                return false;
            }

            for (int i = 0; i < ValueTable.Length; i++)
            {
                ValueRow row = ValueTable[i];
                if (string.Equals(row.Kind, kind, StringComparison.Ordinal) &&
                    string.Equals(row.Tier, tier, StringComparison.Ordinal))
                {
                    units = row.Units;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Applies the ledger's value rule to a raw number: whole units, at least 1.
        /// </summary>
        /// <remarks>
        /// The table stores integers, so no shipped row fails this today; the check exists
        /// because a hand-edited row (0, or a fractional literal) must be reported as a value
        /// error instead of being rounded, and because the ledger's rule must live in one
        /// place rather than being assumed from the field's type.
        /// </remarks>
        public static bool TryValidateValueUnits(float rawValueUnits, out int valueUnits)
        {
            valueUnits = 0;
            if (float.IsNaN(rawValueUnits) || float.IsInfinity(rawValueUnits))
            {
                return false;
            }

            if (rawValueUnits < MinValueUnits)
            {
                return false;
            }

            // 2^31 is the first value an int cannot hold; refuse it before the cast.
            if (rawValueUnits >= 2147483648f)
            {
                return false;
            }

            int truncated = (int)rawValueUnits;
            if ((float)truncated != rawValueUnits)
            {
                return false;
            }

            valueUnits = truncated;
            return true;
        }

        /// <summary>True for a non-empty name with no leading or trailing whitespace.</summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return name.Trim().Length == name.Length;
        }

        /// <summary>True when <paramref name="itemId"/> can be written as a row id.</summary>
        public static bool IsValidItemId(string itemId)
        {
            return IsValidName(itemId);
        }

        /// <summary>True when <paramref name="kind"/> can be written as a row kind.</summary>
        public static bool IsValidKind(string kind)
        {
            return IsValidName(kind);
        }

        /// <summary>
        /// Sorts hierarchy paths ordinally without touching the input array.
        /// </summary>
        /// <remarks>
        /// Ordinal, not culture-aware: the authored order must not change with the editor's
        /// language or with case-insensitive comparisons, because the row order is the row
        /// identity of the host's index-aligned arrays.
        /// </remarks>
        public static string[] SortHierarchyPaths(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return new string[0];
            }

            string[] sorted = (string[])paths.Clone();
            Array.Sort(sorted, StringComparer.Ordinal);
            return sorted;
        }

        /// <summary>
        /// Returns every id that occurs more than once, once per id, in ordinal order.
        /// </summary>
        /// <remarks>
        /// Duplicates are a data error, not something to resolve by picking a winner: the
        /// item id is the candidate/ledger key, so two rows sharing one id would let one
        /// prop's absorb event be credited to the other prop's value.
        /// </remarks>
        public static string[] FindDuplicateIds(string[] itemIds)
        {
            if (itemIds == null || itemIds.Length < 2)
            {
                return new string[0];
            }

            string[] sorted = (string[])itemIds.Clone();
            Array.Sort(sorted, StringComparer.Ordinal);

            var duplicates = new List<string>();
            for (int i = 1; i < sorted.Length; i++)
            {
                if (!string.Equals(sorted[i], sorted[i - 1], StringComparison.Ordinal))
                {
                    continue;
                }

                if (duplicates.Count == 0 ||
                    !string.Equals(duplicates[duplicates.Count - 1], sorted[i], StringComparison.Ordinal))
                {
                    duplicates.Add(sorted[i]);
                }
            }

            return duplicates.ToArray();
        }

        private static bool IsFiniteNonNegative(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
        }
    }
}
