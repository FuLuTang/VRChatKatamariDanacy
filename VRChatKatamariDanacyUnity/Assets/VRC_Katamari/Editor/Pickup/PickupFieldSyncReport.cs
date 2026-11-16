using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VRCKatamari.Editor.Pickup
{
    /// <summary>
    /// What one <see cref="PickupFieldSync.SyncFromChildren"/> run did, including every row
    /// it refused and why.
    /// </summary>
    /// <remarks>
    /// The report is the point of the sync. A wrong row is never repaired silently: it is
    /// left out of the written arrays and named here, so "the field lost a prop" has a
    /// reason in the console instead of being discovered in Play Mode.
    /// </remarks>
    public sealed class PickupFieldSyncReport
    {
        /// <summary>Ceiling on rejected-row lines in one console message.</summary>
        private const int ConsoleRejectionLimit = 50;

        public bool Success;
        public string Failure;
        public string HostPath;
        public string FieldRootPath;
        public int PropCount;

        /// <summary>
        /// Sum of every written row's value units. 64-bit: the host column is
        /// (Docs/OPEN_DECISIONS.md (r) step B), so a level total past <c>int.MaxValue</c>
        /// prints the same number the ledger will see.
        /// </summary>
        public long TotalValueUnits;

        /// <summary>
        /// True when the caller supplied a non-empty declaration map. False means no values
        /// were verified because none were declared (the un-migrated scene path).
        /// </summary>
        public bool ValueUnitsRequested;

        /// <summary>Sum of the units taken from the caller's declarations.</summary>
        public long ValueUnitsDeclared;

        /// <summary>Rows whose value was read back from the host and preserved unchanged.</summary>
        public int ValueUnitsPreserved;

        /// <summary>Rows written as 0 because they had neither a declaration nor an existing value.</summary>
        public int ValueUnitsUndeclared;

        /// <summary>True when every declared value read back from the host exactly.</summary>
        public bool ValueUnitsVerified;

        /// <summary>
        /// Rows that had no Collider and were given a <c>BoxCollider</c> on the prop root by
        /// this run (the importer's <c>createMissingColliders</c> path). Zero on a repeated
        /// sync, because the created boxes are reused.
        /// </summary>
        public int CollidersCreated;

        /// <summary>Declared values that did not land, and declared ids with no row.</summary>
        public readonly List<string> ValueUnitMismatches = new List<string>();

        /// <summary>True when the UdonSharp compile + proxy copy was issued without throwing.</summary>
        public bool UdonCopyIssued;

        /// <summary>True when the seven serialized arrays read back at the expected lengths.</summary>
        public bool ReadBackVerified;

        public readonly List<PickupFieldKindCount> Kinds = new List<PickupFieldKindCount>();
        public readonly List<PickupFieldRejection> Rejected = new List<PickupFieldRejection>();
        public readonly List<string> Notes = new List<string>();

        public void Fail(string reason)
        {
            Success = false;
            Failure = reason;
        }

        /// <summary>Adds or updates the per-kind tally for one accepted row.</summary>
        public void CountRow(string kind, long valueUnits)
        {
            for (int i = 0; i < Kinds.Count; i++)
            {
                if (!string.Equals(Kinds[i].Kind, kind, System.StringComparison.Ordinal))
                {
                    continue;
                }

                Kinds[i].Props++;
                Kinds[i].ValueUnits += valueUnits;
                return;
            }

            var entry = new PickupFieldKindCount();
            entry.Kind = kind;
            entry.Props = 1;
            entry.ValueUnits = valueUnits;
            Kinds.Add(entry);
        }

        public void Reject(string path, string itemId, string reason)
        {
            var rejection = new PickupFieldRejection();
            rejection.Path = path;
            rejection.ItemId = itemId;
            rejection.Reason = reason;
            Rejected.Add(rejection);
        }

        public string BuildSummary()
        {
            var text = new StringBuilder();
            text.Append("[PickupFieldSync] ");
            if (!string.IsNullOrEmpty(HostPath))
            {
                text.Append(HostPath);
                text.Append(": ");
            }

            text.Append(PropCount).Append(" prop(s), ").Append(Kinds.Count).Append(" kind(s), ")
                .Append(TotalValueUnits).Append(" value unit(s), ").Append(Rejected.Count)
                .Append(" rejected");

            if (CollidersCreated > 0)
            {
                text.AppendLine();
                text.Append("  propColliders: created=").Append(CollidersCreated)
                    .Append(" (from each prop's visual local AABB)");
            }

            text.AppendLine();
            text.Append("  valueUnits: declared=").Append(ValueUnitsDeclared)
                .Append(", preserved=").Append(ValueUnitsPreserved)
                .Append(", undeclared=").Append(ValueUnitsUndeclared)
                .Append(", read-back: ").Append(!ValueUnitsRequested
                    ? "n/a (no declarations)"
                    : (ValueUnitsVerified ? "verified" : "NOT verified"));

            if (!string.IsNullOrEmpty(FieldRootPath))
            {
                text.AppendLine();
                text.Append("  field root: ").Append(FieldRootPath);
            }

            for (int i = 0; i < Kinds.Count; i++)
            {
                text.AppendLine();
                text.Append("  kind ").Append(Kinds[i].Kind).Append(": ").Append(Kinds[i].Props)
                    .Append(" prop(s), ").Append(Kinds[i].ValueUnits).Append(" unit(s)");
            }

            int printed = 0;
            for (int i = 0; i < Rejected.Count && printed < ConsoleRejectionLimit; i++, printed++)
            {
                text.AppendLine();
                text.Append("  rejected ").Append(Rejected[i].Path).Append(": ").Append(Rejected[i].Reason);
            }

            if (Rejected.Count > printed)
            {
                text.AppendLine();
                text.Append("  ... ").Append(Rejected.Count - printed)
                    .Append(" more rejected row(s) in the returned report");
            }

            for (int i = 0; i < ValueUnitMismatches.Count && i < ConsoleRejectionLimit; i++)
            {
                text.AppendLine();
                text.Append("  valueUnits ").Append(ValueUnitMismatches[i]);
            }

            if (ValueUnitMismatches.Count > ConsoleRejectionLimit)
            {
                text.AppendLine();
                text.Append("  ... ").Append(ValueUnitMismatches.Count - ConsoleRejectionLimit)
                    .Append(" more valueUnits problem(s) in the returned report");
            }

            for (int i = 0; i < Notes.Count; i++)
            {
                text.AppendLine();
                text.Append("  note: ").Append(Notes[i]);
            }

            text.AppendLine();
            text.Append("  udon copy: ").Append(UdonCopyIssued ? "issued" : "not issued")
                .Append("; array read-back: ").Append(ReadBackVerified ? "verified" : "not verified");

            if (!string.IsNullOrEmpty(Failure))
            {
                text.AppendLine();
                text.Append("  failure: ").Append(Failure);
            }

            return text.ToString();
        }

        /// <summary>
        /// Writes the summary at the severity it deserves: an error when the run failed, a
        /// warning when rows were refused, and a plain log only when everything was clean.
        /// </summary>
        public void Log()
        {
            if (!Success)
            {
                Debug.LogError(BuildSummary());
                return;
            }

            if (Rejected.Count > 0)
            {
                Debug.LogWarning(BuildSummary());
                return;
            }

            Debug.Log(BuildSummary());
        }
    }

    /// <summary>Accepted prop rows per kind, and the value units they carry.</summary>
    public sealed class PickupFieldKindCount
    {
        public string Kind;
        public int Props;
        public long ValueUnits;
    }

    /// <summary>One row the sync refused to write, with the reason it refused.</summary>
    public sealed class PickupFieldRejection
    {
        public string Path;
        public string ItemId;
        public string Reason;
    }
}
