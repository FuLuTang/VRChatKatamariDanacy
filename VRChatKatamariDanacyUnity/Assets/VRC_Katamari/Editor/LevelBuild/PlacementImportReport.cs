using System.Collections.Generic;
using System.Text;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// What one <see cref="PlacementImporter"/> run did, and everything the operator
    /// needs to decide whether to keep it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape follows <c>Editor/Pickup/PickupFieldSyncReport.cs</c>: counters for a
    /// one-line console summary (the idempotency gate is <c>+0 ~0 -0</c> on the second
    /// run), plus a bounded list of notes so a broken placement file cannot flood the
    /// console. The report never claims success for a run that had errors.
    /// </para>
    /// <para>
    /// The value-unit counters are specific to the single-source-of-truth decision: the
    /// level's declared total, the rows the sync preserved from an existing host value, the
    /// rows it could not give any value, and whether the declared values read back. A
    /// level's value total is gameplay data, so it is verified after the one write rather
    /// than assumed. There is no "rewritten over the sync" counter any more: the sync is the
    /// only writer (U2, 2026-11-15).
    /// </para>
    /// </remarks>
    public sealed class PlacementImportReport
    {
        /// <summary>Ceiling on per-row lines in one console message.</summary>
        private const int ConsoleRowLimit = 40;

        public string LevelId;
        /// <summary>The arena cell this run imported, or null for a whole-level document.</summary>
        public string ArenaId;
        public string PlacementPath;
        public string ManagedRootPath;
        public string SceneName;

        public int PropsCreated;
        public int PropsUpdated;
        public int PropsDeleted;
        public int PropsSkipped;
        public int PropErrors;

        public int StructuresCreated;
        public int StructuresUpdated;
        public int StructuresDeleted;

        /// <summary>Logical curved ramps imported (one entry each, however many segments).</summary>
        public int CurveRamps;

        /// <summary>Slab segments every curve expanded into (the sum over the ramps).</summary>
        public int CurveSegments;

        public int CurveSegmentsCreated;
        public int CurveSegmentsUpdated;
        public int CurveSegmentsDeleted;

        public int SyncPropCount;
        public int SyncRejected;
        public bool SyncRan;
        public bool UdonCopyIssued;

        /// <summary>
        /// Prop colliders the pickup sync created on this run (zero when every prop already
        /// had one, and on the idempotent second import).
        /// </summary>
        public int SyncCollidersCreated;

        /// <summary>
        /// The level's declared value-unit total. 64-bit because the host column is
        /// (Docs/OPEN_DECISIONS.md (r) step B), so a total past <c>int.MaxValue</c> prints
        /// exactly; the other two counters are row counts and stay 32-bit.
        /// </summary>
        public long ValueUnitsDeclared;
        public int ValueUnitsPreserved;
        public int ValueUnitsUndeclared;
        public bool ValueUnitsVerified;

        public int ArenaBoxes;
        public int ArenaRamps;
        public int ArenaPlanes;
        public int ArenaKinds;
        public bool ArenaWired;

        public bool Success;
        public string Failure;

        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> ValueUnitProblems = new List<string>();

        public void Fail(string reason)
        {
            Success = false;
            Failure = reason;
        }

        public void Note(string message)
        {
            if (Notes.Count < ConsoleRowLimit)
            {
                Notes.Add(message);
            }
        }

        public void Error(string message)
        {
            Errors.Add(message);
        }

        /// <summary>The one-line gate the Unity operator reads first.</summary>
        public string Headline()
        {
            string arena = string.IsNullOrEmpty(ArenaId) ? "" : " cell=" + ArenaId;
            return "[level]" + arena + " +" + PropsCreated + " ~" + PropsUpdated + " -" + PropsDeleted +
                   " props=" + (PropsCreated + PropsUpdated + PropsSkipped) +
                   " other=" + (StructuresCreated + StructuresUpdated + StructuresDeleted) +
                   " errors=" + PropErrors;
        }

        public string BuildSummary()
        {
            var text = new StringBuilder();
            text.Append(Headline());

            if (!string.IsNullOrEmpty(LevelId))
            {
                text.Append("  level=").Append(LevelId);
            }

            if (!string.IsNullOrEmpty(ArenaId))
            {
                text.Append("  arena=").Append(ArenaId);
            }

            if (!string.IsNullOrEmpty(SceneName))
            {
                text.Append("  scene=").Append(SceneName);
            }

            text.AppendLine();
            text.Append("  placement: ").Append(PlacementPath);
            text.AppendLine();
            text.Append("  managed root: ").Append(ManagedRootPath);
            text.AppendLine();
            text.Append("  props: ").Append(PropsSkipped).Append(" unchanged, ")
                .Append(PropErrors).Append(" error(s)");
            text.AppendLine();
            text.Append("  structures: +").Append(StructuresCreated).Append(" ~")
                .Append(StructuresUpdated).Append(" -").Append(StructuresDeleted);
            text.AppendLine();
            if (CurveRamps > 0)
            {
                // One logical ramp, N slab children (CONTACT_MODEL_V2 5.7): the parent is
                // counted in the structures line, its segments here.
                text.Append("  curves: ").Append(CurveRamps).Append(" ramp(s), ")
                    .Append(CurveSegments).Append(" slab segment(s): +")
                    .Append(CurveSegmentsCreated).Append(" ~").Append(CurveSegmentsUpdated)
                    .Append(" -").Append(CurveSegmentsDeleted);
                text.AppendLine();
            }
            text.Append("  pickup sync: ").Append(SyncRan ? "ran" : "not run")
                .Append(", rows=").Append(SyncPropCount)
                .Append(", rejected=").Append(SyncRejected)
                .Append("; udon copy: ").Append(UdonCopyIssued ? "issued" : "not issued");
            text.AppendLine();
            text.Append("  propColliders: created=").Append(SyncCollidersCreated)
                .Append(SyncCollidersCreated > 0 ? " (from each prop's visual local AABB)" : "");
            text.AppendLine();
            text.Append("  valueUnits: declared=").Append(ValueUnitsDeclared)
                .Append(", preserved=").Append(ValueUnitsPreserved)
                .Append(", undeclared=").Append(ValueUnitsUndeclared)
                .Append(", read-back: ").Append(ValueUnitsVerified ? "verified" : "NOT verified");
            text.AppendLine();
            text.Append("  arena: ").Append(ArenaWired ? "wired" : "not wired")
                .Append(" (boxes=").Append(ArenaBoxes).Append(", walls=").Append(ArenaPlanes)
                .Append(", ramps=").Append(ArenaRamps).Append(", kinds=")
                .Append(ArenaKinds).Append(")");

            for (int i = 0; i < ValueUnitProblems.Count; i++)
            {
                text.AppendLine();
                text.Append("  valueUnits: ").Append(ValueUnitProblems[i]);
            }

            for (int i = 0; i < Notes.Count; i++)
            {
                text.AppendLine();
                text.Append("  note: ").Append(Notes[i]);
            }

            for (int i = 0; i < Errors.Count && i < ConsoleRowLimit; i++)
            {
                text.AppendLine();
                text.Append("  error: ").Append(Errors[i]);
            }

            if (Errors.Count > ConsoleRowLimit)
            {
                text.AppendLine();
                text.Append("  ... ").Append(Errors.Count - ConsoleRowLimit).Append(" more error(s)");
            }

            if (!string.IsNullOrEmpty(Failure))
            {
                text.AppendLine();
                text.Append("  FAILURE: ").Append(Failure);
            }
            else
            {
                text.AppendLine();
                text.Append("  the scene is left dirty on purpose: this tool never saves (save when you have checked it)");
            }

            return text.ToString();
        }

        public void Log()
        {
            if (Success)
            {
                UnityEngine.Debug.Log(BuildSummary());
            }
            else
            {
                UnityEngine.Debug.LogError(BuildSummary());
            }
        }
    }
}
