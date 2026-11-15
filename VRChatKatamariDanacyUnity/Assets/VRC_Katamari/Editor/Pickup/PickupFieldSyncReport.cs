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
        public int TotalValueUnits;

        /// <summary>True when the UdonSharp compile + proxy copy was issued without throwing.</summary>
        public bool UdonCopyIssued;

        /// <summary>True when the six serialized arrays read back at the expected lengths.</summary>
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
        public void CountRow(string kind, int valueUnits)
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

            for (int i = 0; i < Notes.Count; i++)
            {
                text.AppendLine();
                text.Append("  note: ").Append(Notes[i]);
            }

            text.AppendLine();
            text.Append("  udon copy: ").Append(UdonCopyIssued ? "issued" : "not issued")
                .Append("; read-back: ").Append(ReadBackVerified ? "verified" : "not verified");

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
        public int ValueUnits;
    }

    /// <summary>One row the sync refused to write, with the reason it refused.</summary>
    public sealed class PickupFieldRejection
    {
        public string Path;
        public string ItemId;
        public string Reason;
    }
}
