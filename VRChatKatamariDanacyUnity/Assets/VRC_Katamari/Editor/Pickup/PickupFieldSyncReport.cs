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
        /// Sum of every written row's growth, in mm^3 (the ledger gain). 64-bit because the
        /// ledger is; the value is derived at sync time from the row's bounds x k x penalty.
        /// </summary>
        public long TotalGrowthMm3;

        /// <summary>True when this run derived and wrote the growth column.</summary>
        public bool GrowthPriced;

        /// <summary>Sum of the derived growth of the rows this run priced.</summary>
        public long GrowthMm3Priced;

        /// <summary>Rows whose growth came from the placement file's authored `growthMm3`.</summary>
        public int GrowthFileRows;

        /// <summary>Rows with no authored growth (old file): priced from the measured bounds.</summary>
        public int GrowthBoundsRows;

        /// <summary>True when every written row's growth read back from the host exactly.</summary>
        public bool GrowthVerified;
        /// <summary>Every declared collider proxy read back bit-identically.</summary>
        public bool ProxyVerified;

        /// <summary>
        /// Rows that had no Collider and were given a <c>BoxCollider</c> on the prop root by
        /// this run (the importer's <c>createMissingColliders</c> path). Zero on a repeated
        /// sync, because the created boxes are reused.
        /// </summary>
        public int CollidersCreated;

        /// <summary>Declared values that did not land, and declared ids with no row.</summary>
        public readonly List<string> GrowthMismatches = new List<string>();

        /// <summary>True when the UdonSharp compile + proxy copy was issued without throwing.</summary>
        public bool UdonCopyIssued;

        /// <summary>True when the eight serialized arrays read back at the expected lengths.</summary>
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
                Kinds[i].GrowthMm3 += valueUnits;
                return;
            }

            var entry = new PickupFieldKindCount();
            entry.Kind = kind;
            entry.Props = 1;
            entry.GrowthMm3 = valueUnits;
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
                .Append(TotalGrowthMm3).Append(" growth mm^3, ").Append(Rejected.Count)
                .Append(" rejected");

            if (CollidersCreated > 0)
            {
                text.AppendLine();
                text.Append("  propColliders: created=").Append(CollidersCreated)
                    .Append(" (from each prop's visual local AABB)");
            }

            text.AppendLine();
            text.Append("  growthMm3: written=").Append(GrowthMm3Priced)
                .Append(", fromFile=").Append(GrowthFileRows)
                .Append(", fromBounds=").Append(GrowthBoundsRows)
                .Append(", read-back: ").Append(!GrowthPriced
                    ? "n/a (no rows)"
                    : (GrowthVerified ? "verified" : "NOT verified"));

            if (!string.IsNullOrEmpty(FieldRootPath))
            {
                text.AppendLine();
                text.Append("  field root: ").Append(FieldRootPath);
            }

            for (int i = 0; i < Kinds.Count; i++)
            {
                text.AppendLine();
                text.Append("  kind ").Append(Kinds[i].Kind).Append(": ").Append(Kinds[i].Props)
                    .Append(" prop(s), ").Append(Kinds[i].GrowthMm3).Append(" mm^3");
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

            for (int i = 0; i < GrowthMismatches.Count && i < ConsoleRejectionLimit; i++)
            {
                text.AppendLine();
                text.Append("  growthMm3 ").Append(GrowthMismatches[i]);
            }

            if (GrowthMismatches.Count > ConsoleRejectionLimit)
            {
                text.AppendLine();
                text.Append("  ... ").Append(GrowthMismatches.Count - ConsoleRejectionLimit)
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

    /// <summary>Accepted prop rows per kind, and the growth in mm^3 they carry.</summary>
    public sealed class PickupFieldKindCount
    {
        public string Kind;
        public int Props;
        public long GrowthMm3;
    }

    /// <summary>One row the sync refused to write, with the reason it refused.</summary>
    public sealed class PickupFieldRejection
    {
        public string Path;
        public string ItemId;
        public string Reason;
    }
}
