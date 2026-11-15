using System;
using System.Collections.Generic;
using System.Globalization;

namespace VRCKatamari.Editor.Pickup
{
    /// <summary>
    /// The pure half of the pickup-field authoring path: size tier from bounds, value-unit
    /// lookup by kind and tier, duplicate-id detection, the optional
    /// <c>@size=&lt;metres&gt;</c> name suffix and deterministic path ordering.
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
    /// <para>
    /// Two rows are not catalog rows: <c>fruit s 1</c> and <c>fruit m 4</c> are the story
    /// level-2 (fruit counting) values of
    /// <c>Docs/Design/StoryMode/LEVEL_CONTENT_PLAN.md</c> section 3.1, also PROVISIONAL.
    /// They are added here because the plan's fruit kinds cannot be counted by the host
    /// unless the row exists: an undeclared (kind, tier) pair is a rejected row, so level 2
    /// would see 0 fruit. <c>fruit l</c> is deliberately absent - the plan's largest fruit
    /// (watermelon, 0.72 m) is medium tier, and a larger fruit must get its own declared row
    /// rather than a silent default.
    /// </para>
    /// <para>
    /// An explicit absorb size for a prop whose visual is much longer than its pickup size
    /// (a gate bench, a fallen log) is a suffix on the GameObject name:
    /// <c>prop_bench_01@size=1.25</c>. <see cref="TryParseDeclaredSize"/> is the only reader
    /// of that convention, it strips the suffix from the item id, and the sync turns the
    /// declared edge into a cube for <c>propBounds</c> and the tier. Why a name suffix and
    /// not a marker child or a component: the name travels with the object through a rename
    /// or a prefab move, it needs no scene object and no runtime type, and stripping keeps
    /// the item id equal to the level plan's <c>prop_&lt;definitionId&gt;_&lt;NN&gt;</c>
    /// naming. A malformed suffix is a rejected row with the reason in the report, never a
    /// silent fallback to the AABB.
    /// </para>
    /// </remarks>
    public static class PickupFieldAuthoringRules
    {
        // The ledger consumes whole units and refuses anything below 1
        // (Docs/OPEN_DECISIONS.md (j), Runtime/Pickup/PickupAbsorbMath.cs:259-271).
        public const int MinValueUnits = 1;

        // A prop whose longest renderer extent is below this has no usable size: an AABB
        // that small is a missing renderer or a degenerate mesh, not a tiny prop. Only the
        // longest side counts, so a flat prop (a plane or a leaf with a zero side) is fine.
        public const float MinBoundsExtentM = 0.0001f;

        // Tier bands, metres of the longest AABB extent. PROVISIONAL: the s/m gap in the
        // PicnicLawn catalog is 0.18 -> 0.24 m and the m/l gap is 0.72 -> 0.80 m, so the
        // boundaries sit in the middle of those gaps.
        public const float SmallTierMaxExtentM = 0.21f;
        public const float MediumTierMaxExtentM = 0.76f;

        public const string TierSmall = "s";
        public const string TierMedium = "m";
        public const string TierLarge = "l";

        // The one spelling of the declared-size suffix ("prop_bench_01@size=1.25"). Exact,
        // lowercase: a case variant is reported instead of being ignored, because a marker
        // that does nothing is the failure mode this convention exists to prevent.
        public const string DeclaredSizeMarker = "@size=";

        // Explicitly declared (kind, tier) -> value units. Rows come from the PicnicLawn
        // catalog for the categories that are absorbable props; categories whose catalog
        // value is 0 (boundary, gameplay, landmark, terrain) deliberately have no row and
        // are refused by the sync instead of shipping an unabsorbable prop. The fruit rows
        // are the story level-2 values (Docs/Design/StoryMode/LEVEL_CONTENT_PLAN.md 3.1).
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
            new ValueRow("fruit", TierSmall, 1),
            new ValueRow("nature", TierSmall, 1),
            new ValueRow("drink", TierMedium, 4),
            new ValueRow("food", TierMedium, 4),
            new ValueRow("fruit", TierMedium, 4),
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
        /// size a prop: a negative or non-finite side, or a longest side shorter than
        /// <see cref="MinBoundsExtentM"/>. A side of exactly 0 is not an error by itself -
        /// a plane, a sheet of paper or a leaf has a flat AABB - so the decision is made on
        /// the longest side only.
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
        /// Reads the optional <c>@size=&lt;metres&gt;</c> suffix off a prop GameObject name.
        /// </summary>
        /// <param name="name">The prop GameObject's name, as authored in the scene.</param>
        /// <param name="itemId">
        /// The row id: <paramref name="name"/> with the suffix removed when one is present,
        /// otherwise <paramref name="name"/> unchanged. On a refusal it is
        /// <paramref name="name"/> unchanged, because the sync rejects the row anyway.
        /// </param>
        /// <param name="declaredEdgeM">
        /// The declared absorb size as the edge of a cube, in metres; 0 when the name
        /// declares none.
        /// </param>
        /// <param name="error">
        /// The refusal reason, or <c>null</c> when the name is fine. A present-but-broken
        /// marker is always refused here instead of falling back to the renderer AABB: an
        /// author who writes a size and silently gets the AABB instead has no way to notice
        /// that the gate prop is recorded at 3.2 m.
        /// </param>
        /// <returns>
        /// True when the name carries no marker or one well-formed marker; false when the
        /// marker is malformed (wrong case, repeated, no number, not the trailing token,
        /// an empty id, a non-finite / non-positive / sub-millimetre size). A null or
        /// empty name is not a marker problem and is left for
        /// <see cref="IsValidItemId"/> to refuse.
        /// </returns>
        /// <remarks>
        /// The number is parsed with <see cref="CultureInfo.InvariantCulture"/> and a fixed
        /// style (leading sign and decimal point only): the scene asset is authored on
        /// whichever machine the editor runs on, so <c>1,25</c> must not become 1.25 on a
        /// German machine and must not become 125 anywhere. Exponents are refused for the
        /// same reason - a prop size is a plain metre value, and "1e40" is not one.
        /// </remarks>
        public static bool TryParseDeclaredSize(string name, out string itemId, out float declaredEdgeM,
            out string error)
        {
            itemId = name;
            declaredEdgeM = 0f;
            error = null;

            if (string.IsNullOrEmpty(name))
            {
                return true;
            }

            int exactMarkers = CountOccurrences(name, DeclaredSizeMarker, StringComparison.Ordinal);
            int looseMarkers = CountOccurrences(name, DeclaredSizeMarker, StringComparison.OrdinalIgnoreCase);

            if (looseMarkers == 0)
            {
                return true;
            }

            if (exactMarkers != looseMarkers)
            {
                error = "the size marker must be written exactly as '" + DeclaredSizeMarker +
                        "' (lower case) in '" + name + "'; it is refused rather than ignored";
                return false;
            }

            if (looseMarkers > 1)
            {
                error = "'" + name + "' has " + looseMarkers + " '" + DeclaredSizeMarker +
                        "' markers; declare the absorb size once";
                return false;
            }

            int markerIndex = name.IndexOf(DeclaredSizeMarker, StringComparison.Ordinal);
            string numberText = name.Substring(markerIndex + DeclaredSizeMarker.Length);
            if (numberText.Length == 0)
            {
                error = "'" + name + "' ends with '" + DeclaredSizeMarker + "' and no number";
                return false;
            }

            float parsed;
            const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
            if (!float.TryParse(numberText, styles, CultureInfo.InvariantCulture, out parsed))
            {
                error = "'" + numberText + "' after '" + DeclaredSizeMarker +
                        "' is not a plain invariant-culture decimal number (example: " +
                        DeclaredSizeMarker + "1.25); it must be the last token of the name";
                return false;
            }

            if (!IsUsableDeclaredSize(parsed))
            {
                error = "'" + numberText + "' after '" + DeclaredSizeMarker +
                        "' is not a usable absorb size: it must be finite, positive and at least " +
                        FormatMetres(MinBoundsExtentM) + " m";
                return false;
            }

            string stripped = name.Substring(0, markerIndex);
            if (stripped.Trim().Length == 0)
            {
                error = "'" + name + "' leaves an empty item id once the '" + DeclaredSizeMarker +
                        "' suffix is removed";
                return false;
            }

            itemId = stripped;
            declaredEdgeM = parsed;
            return true;
        }

        /// <summary>
        /// True when <paramref name="edgeM"/> can be a declared absorb size: finite, positive
        /// and at least <see cref="MinBoundsExtentM"/>.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="TryParseDeclaredSize"/> so the numeric rule is testable
        /// without a string: a name cannot spell every <c>float</c>, and NaN/Infinity must be
        /// refused by the rule itself rather than by a parser's incidental behaviour.
        /// </remarks>
        public static bool IsUsableDeclaredSize(float edgeM)
        {
            return IsFiniteNonNegative(edgeM) && edgeM >= MinBoundsExtentM;
        }

        private static int CountOccurrences(string text, string value, StringComparison comparison)
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                int found = text.IndexOf(value, index, comparison);
                if (found < 0)
                {
                    return count;
                }

                count++;
                index = found + value.Length;
            }
        }

        private static string FormatMetres(float metres)
        {
            return metres.ToString("0.####", CultureInfo.InvariantCulture);
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
