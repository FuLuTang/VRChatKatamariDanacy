using System;
using System.Collections.Generic;
using System.Globalization;

namespace VRCKatamari.Editor.Pickup
{
    /// <summary>
    /// The pure half of the pickup-field authoring path: renderer-bounds usability, the
    /// resolution of a row's declared or already-serialized value units, read-back
    /// mismatch detection, duplicate-id detection, the optional
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
    /// Why these rules exist at all. <c>Runtime/Pickup/PickupFieldHost.cs</c> holds eight
    /// index-aligned serialized arrays, and the host is the only runtime owner of that data.
    /// An editor-only MonoBehaviour cannot carry the authoring data instead: a component
    /// whose script lives in an Editor assembly has no runtime type, so Unity cannot
    /// serialize it into a built scene. The authoring path therefore derives the rows from
    /// the props that already exist in the scene (GameObject name, hierarchy position,
    /// renderer bounds, colliders) and writes them into the host. This class is the part of
    /// that derivation that is decidable without looking at a scene.
    /// </para>
    /// <para>
    /// Values are content data, not a pricing rule. The Lead's decision of 2026-11-15
    /// (Docs/OPEN_DECISIONS.md (q) U2) puts <c>valueUnits</c> on the level's per-def
    /// declaration (<c>placement.json</c> <c>defs[].valueUnits</c>, overridable per
    /// <c>props[].valueUnits</c>), so this class no longer carries a (kind, tier) value
    /// table and the sync no longer prices a prop from its longest edge. A missing table
    /// row therefore cannot drop a prop. What remains here is the decision a sync still
    /// has to make for each row:
    /// </para>
    /// <list type="bullet">
    /// <item><description>a value supplied by the caller (the importer's declaration)
    /// wins;</description></item>
    /// <item><description>without a declaration, the value already serialized on the host
    /// is preserved - that is what keeps un-migrated scenes (StoryLevel's 110 props,
    /// CpuSkirmish) bit-identical;</description></item>
    /// <item><description>a row with neither is written as 0 and reported, never silently
    /// given a size-derived number.</description></item>
    /// </list>
    /// <para>
    /// Values are whole units at least 1, because the ledger rejects anything else as
    /// <c>RejectNoGain</c> (Docs/OPEN_DECISIONS.md (j); the runtime mirror is
    /// <c>PickupAbsorbMath.TryAbsorbValueToLedgerGain</c>). <see cref="TryValidateValueUnits"/>
    /// is still the one place that rule is spelled out, so the declaration path and the
    /// ledger cannot drift apart. It is 64-bit since Docs/OPEN_DECISIONS.md (r) step B, the
    /// width of the host column; the float-shaped overload only adds "must be exactly a whole
    /// number" and delegates to the integer entry point, so the two cannot disagree on the
    /// range either.
    /// </para>
    /// <para>
    /// An explicit absorb size for a prop whose visual is much longer than its pickup size
    /// (a gate bench, a fallen log) is a suffix on the GameObject name:
    /// <c>prop_bench_01@size=1.25</c>. <see cref="TryParseDeclaredSize"/> is the only reader
    /// of that convention, it strips the suffix from the item id, and the sync turns the
    /// declared edge into a cube for <c>propBounds</c>. Why a name suffix and not a marker
    /// child or a component: the name travels with the object through a rename or a prefab
    /// move, it needs no scene object and no runtime type, and stripping keeps the item id
    /// equal to the level plan's <c>prop_&lt;definitionId&gt;_&lt;NN&gt;</c> naming. A
    /// malformed suffix is a rejected row with the reason in the report, never a silent
    /// fallback to the AABB.
    /// </para>
    /// </remarks>
    public static class PickupFieldAuthoringRules
    {
        // The ledger consumes whole units and refuses anything below 1
        // (Docs/OPEN_DECISIONS.md (j), Runtime/Pickup/PickupAbsorbMath.cs:259-271). 64-bit
        // since (r) step B so the comparison against a declared long is long-to-long, with no
        // conversion on the way in.
        public const long MinValueUnits = 1L;

        // A prop whose longest renderer extent is below this has no usable size: an AABB
        // that small is a missing renderer or a degenerate mesh, not a tiny prop. Only the
        // longest side counts, so a flat prop (a plane or a leaf with a zero side) is fine.
        public const float MinBoundsExtentM = 0.0001f;

        // The one spelling of the declared-size suffix ("prop_bench_01@size=1.25"). Exact,
        // lowercase: a case variant is reported instead of being ignored, because a marker
        // that does nothing is the failure mode this convention exists to prevent.
        public const string DeclaredSizeMarker = "@size=";

        // Where a written valueUnits row came from. Strings rather than an enum because the
        // report prints them and the EditMode tests reach them through reflection.
        public const string ValueSourceDeclared = "declared";
        public const string ValueSourceExisting = "existing";
        public const string ValueSourceNone = "none";

        /// <summary>
        /// The per-item volume factor's default and its one legal range: `0 &lt; k &lt;= 32`.
        /// </summary>
        /// <remarks>
        /// Runtime twin: <c>PickupAbsorbMath.DefaultVolumeK</c>/<c>MaxVolumeK</c>. The
        /// authoring side spells the same numbers because this assembly cannot reference the
        /// runtime one, and one test pins them equal so the two cannot drift. 0.70 is the
        /// original's default factor (`Volume = k * bbox`, 1,008/1,425 types) and the
        /// ceiling catches a typo like `70`; see PARITY_AUDIT G13.
        /// </remarks>
        public const float DefaultVolumeK = 0.70f;

        /// <summary>The upper bound of the legal `volumeK` range, exclusive of 0.</summary>
        public const float MaxVolumeK = 32f;

        /// <summary>
        /// Why a declared <c>volumeK</c> is unusable, or null when it is a legal factor.
        /// </summary>
        /// <remarks>
        /// The single statement of the range. A non-finite value, `k &lt;= 0` and
        /// `k &gt; MaxVolumeK` are refused rather than clamped: 0 would price every item at
        /// zero volume and 70 (a typo for 0.70) would make a chair absorbable by a pinhead,
        /// so silently "fixing" either would hide a data error that changes gameplay.
        /// </remarks>
        public static string VolumeKError(float volumeK)
        {
            if (float.IsNaN(volumeK) || float.IsInfinity(volumeK)) return "must be finite";
            if (volumeK <= 0f) return "must be > 0 (0 would price every item at zero volume)";
            if (volumeK > MaxVolumeK)
                return "must be <= 32 (0.70 is the original default; a typo like 70 is refused)";
            return null;
        }

        /// <summary>
        /// The value k a row is written with: the declaration, else the row already on the
        /// host, else <see cref="DefaultVolumeK"/>.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="TryResolveValueUnits"/> there is no "none" outcome: k always has
        /// the original's default, so a row can never be left with an unusable factor. A
        /// declaration that fails <see cref="VolumeKError"/> returns false (a refused row,
        /// never a silent fallback), while an existing-but-invalid host value is replaced by
        /// the default and reported through <c>source</c> as
        /// <see cref="ValueSourceNone"/>.
        /// </remarks>
        public static bool TryResolveVolumeK(float declaredVolumeK, bool hasDeclaredVolumeK,
            float existingVolumeK, bool hasExistingVolumeK, out float volumeK, out string source)
        {
            volumeK = DefaultVolumeK;
            source = ValueSourceNone;

            if (hasDeclaredVolumeK)
            {
                if (VolumeKError(declaredVolumeK) != null) return false;
                volumeK = declaredVolumeK;
                source = ValueSourceDeclared;
                return true;
            }

            if (hasExistingVolumeK && VolumeKError(existingVolumeK) == null)
            {
                volumeK = existingVolumeK;
                source = ValueSourceExisting;
                return true;
            }

            return true;
        }

        /// <summary>
        /// True when a full-size AABB can size a prop: every side finite and >= 0, and the
        /// longest side at least <see cref="MinBoundsExtentM"/>.
        /// </summary>
        /// <remarks>
        /// A side of exactly 0 is not an error by itself - a plane, a sheet of paper or a
        /// leaf has a flat AABB - so the decision is made on the longest side only. The rule
        /// is independent of any value table: the tiers that used to turn this answer into a
        /// price are gone (Docs/OPEN_DECISIONS.md (q) U2), but a degenerate AABB is still a
        /// data error even when the pickup size is spelled out in the name.
        /// </remarks>
        public static bool HasUsableBounds(float sizeX, float sizeY, float sizeZ)
        {
            if (!IsFiniteNonNegative(sizeX) || !IsFiniteNonNegative(sizeY) || !IsFiniteNonNegative(sizeZ))
            {
                return false;
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

            return longest >= MinBoundsExtentM;
        }

        /// <summary>
        /// Decides the value units to write for one row.
        /// </summary>
        /// <param name="declaredValueUnits">
        /// The caller's declared value for the row; only read when
        /// <paramref name="hasDeclaredValueUnits"/> is true.
        /// </param>
        /// <param name="hasDeclaredValueUnits">
        /// True when the caller (the level importer) declared a value for this row.
        /// </param>
        /// <param name="existingValueUnits">
        /// The value already serialized on the host; only read when
        /// <paramref name="hasExistingValueUnits"/> is true.
        /// </param>
        /// <param name="hasExistingValueUnits">
        /// True when the host already had a row with this item id before the sync.
        /// </param>
        /// <param name="valueUnits">The number to write.</param>
        /// <param name="source">
        /// <see cref="ValueSourceDeclared"/>, <see cref="ValueSourceExisting"/> or
        /// <see cref="ValueSourceNone"/> when the function returns true; null when it
        /// returns false.
        /// </param>
        /// <returns>
        /// False only when a declaration exists and is not a whole number >= 1. A bad
        /// declaration is refused instead of falling back to the old value: silently
        /// keeping a different number than the level states is the bug U2 removes.
        /// </returns>
        /// <remarks>
        /// Order is deliberate. A declaration is the truth when present; an existing
        /// serialized value is preserved when there is no declaration (the un-migrated
        /// scene path); a new row with neither is written as 0, which the host notices at
        /// run time, rather than being dropped from the arrays or given a rule-derived
        /// number.
        /// <para>
        /// Every parameter and the result are 64-bit (Docs/OPEN_DECISIONS.md (r) step B), the
        /// width of the host column, so preserving an existing row cannot narrow it either.
        /// </para>
        /// </remarks>
        public static bool TryResolveValueUnits(long declaredValueUnits, bool hasDeclaredValueUnits,
            long existingValueUnits, bool hasExistingValueUnits, out long valueUnits, out string source)
        {
            valueUnits = 0L;
            source = null;

            if (hasDeclaredValueUnits)
            {
                long validated;
                if (!TryValidateValueUnits(declaredValueUnits, out validated))
                {
                    return false;
                }

                valueUnits = validated;
                source = ValueSourceDeclared;
                return true;
            }

            if (hasExistingValueUnits)
            {
                // Preserved bit-for-bit, including 0: an un-migrated scene must not have its
                // serialized values zeroed or repriced by a sync that only meant to refresh
                // transforms and bounds.
                valueUnits = existingValueUnits;
                source = ValueSourceExisting;
                return true;
            }

            source = ValueSourceNone;
            return true;
        }

        /// <summary>
        /// Returns one line per row whose host k differs from the declared k.
        /// </summary>
        /// <remarks>
        /// The k twin of <see cref="FindValueUnitMismatches"/>, and it exists for the same
        /// reason: the sync writes through a SerializedObject, so a re-read is the only proof
        /// that the value the importer declared reached the host. Comparison is exact (both
        /// sides are a float written and read back through the same property, so anything
        /// but bit equality means the write did not land). Rows without a declaration are
        /// not compared: preserving them is the intended behaviour.
        /// </remarks>
        public static string[] FindVolumeKMismatches(string[] itemIds, float[] hostVolumeK,
            IDictionary<string, float> declaredVolumeK)
        {
            if (itemIds == null || hostVolumeK == null || declaredVolumeK == null ||
                declaredVolumeK.Count == 0)
            {
                return new string[0];
            }

            int rows = itemIds.Length < hostVolumeK.Length ? itemIds.Length : hostVolumeK.Length;
            var mismatches = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rows; i++)
            {
                string itemId = itemIds[i];
                if (string.IsNullOrEmpty(itemId) || seen.Contains(itemId)) continue;
                seen.Add(itemId);
                float declared;
                if (!declaredVolumeK.TryGetValue(itemId, out declared)) continue;
                if (hostVolumeK[i] != declared)
                {
                    mismatches.Add(itemId + ": declared " + declared + ", host " + hostVolumeK[i]);
                }
            }

            for (int i = 0; i < itemIds.Length; i++)
            {
                string itemId = itemIds[i];
                if (!string.IsNullOrEmpty(itemId) && declaredVolumeK.ContainsKey(itemId) &&
                    !seen.Contains(itemId))
                {
                    mismatches.Add(itemId + ": declared " + declaredVolumeK[itemId] + ", no row");
                }
            }

            return mismatches.ToArray();
        }

        /// <summary>
        /// Returns one line per row whose host value differs from the declared value.
        /// </summary>
        /// <remarks>
        /// The sync calls this on a fresh read-back after its write. The comparison is by
        /// item id, not by array index, because a missing declaration must stay visible even
        /// if the row order changed. Rows without a declaration are not compared: preserving
        /// them is the intended behaviour.
        /// </remarks>
        public static string[] FindValueUnitMismatches(string[] itemIds, long[] hostValueUnits,
            IDictionary<string, long> declaredValueUnits)
        {
            if (itemIds == null || hostValueUnits == null || declaredValueUnits == null ||
                declaredValueUnits.Count == 0)
            {
                return new string[0];
            }

            int rows = itemIds.Length < hostValueUnits.Length ? itemIds.Length : hostValueUnits.Length;
            var mismatches = new List<string>();
            for (int i = 0; i < rows; i++)
            {
                string itemId = itemIds[i];
                if (string.IsNullOrEmpty(itemId))
                {
                    continue;
                }

                long declared;
                if (!declaredValueUnits.TryGetValue(itemId, out declared))
                {
                    continue;
                }

                if (hostValueUnits[i] != declared)
                {
                    mismatches.Add(itemId + ": host=" + hostValueUnits[i] + ", declared=" + declared);
                }
            }

            mismatches.Sort(StringComparer.Ordinal);
            return mismatches.ToArray();
        }

        /// <summary>
        /// Returns every declared item id that has no row on the host, in ordinal order.
        /// </summary>
        /// <remarks>
        /// A declaration without a row means the sync dropped that prop (a malformed
        /// <c>@size</c> marker, duplicate id, degenerate AABB, ...), or the caller passed an
        /// id that is not in this field. Either way the level's declared total and the host
        /// disagree, so the run must not be accepted as verified.
        /// </remarks>
        public static string[] FindDeclaredIdsWithoutRows(string[] itemIds,
            IDictionary<string, long> declaredValueUnits)
        {
            if (declaredValueUnits == null || declaredValueUnits.Count == 0)
            {
                return new string[0];
            }

            var present = new HashSet<string>(StringComparer.Ordinal);
            if (itemIds != null)
            {
                for (int i = 0; i < itemIds.Length; i++)
                {
                    if (!string.IsNullOrEmpty(itemIds[i]))
                    {
                        present.Add(itemIds[i]);
                    }
                }
            }

            var missing = new List<string>();
            foreach (KeyValuePair<string, long> pair in declaredValueUnits)
            {
                if (!present.Contains(pair.Key))
                {
                    missing.Add(pair.Key);
                }
            }

            missing.Sort(StringComparer.Ordinal);
            return missing.ToArray();
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
        /// Applies the ledger's value rule to a declaration that is already an exact integer:
        /// whole units, at least 1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the rule, once. It is 64-bit because the host column is
        /// (<c>PickupFieldHost.valueUnits</c>, Docs/OPEN_DECISIONS.md (r) step B), so a level
        /// may declare a value past <c>int.MaxValue</c> and it survives to the ledger: the
        /// old 2^31 ceiling here would have refused such a row even though every layer below
        /// it can hold the number.
        /// </para>
        /// <para>
        /// The unit is untouched: the minimum is still 1 whole unit, and no number is scaled
        /// or rounded. The float overload below exists only because a caller may hold the
        /// declaration in a float-shaped value; it rejects anything that is not exactly a
        /// whole number and then delegates here, so there is still one range rule.
        /// </para>
        /// </remarks>
        public static bool TryValidateValueUnits(long rawValueUnits, out long valueUnits)
        {
            valueUnits = 0L;
            if (rawValueUnits < MinValueUnits)
            {
                return false;
            }

            valueUnits = rawValueUnits;
            return true;
        }

        /// <summary>
        /// Applies the ledger's whole-unit rule to a float-shaped declaration.
        /// </summary>
        /// <remarks>
        /// Declarations arrive as JSON integers, so no shipped row goes through this overload;
        /// it exists because a hand-edited declaration (0, or a fractional literal in a
        /// caller's map) must be reported as a value error instead of being rounded, and
        /// because the ledger's rule must live in one place rather than being assumed from the
        /// field's type. 2^63 is exactly representable as a float, so comparing against it
        /// before the cast keeps the conversion inside the 64-bit range.
        /// </remarks>
        public static bool TryValidateValueUnits(float rawValueUnits, out long valueUnits)
        {
            valueUnits = 0L;
            if (float.IsNaN(rawValueUnits) || float.IsInfinity(rawValueUnits))
            {
                return false;
            }

            if (rawValueUnits >= 9223372036854775808f)
            {
                return false;
            }

            long truncated = (long)rawValueUnits;
            if ((float)truncated != rawValueUnits)
            {
                return false;
            }

            return TryValidateValueUnits(truncated, out valueUnits);
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
