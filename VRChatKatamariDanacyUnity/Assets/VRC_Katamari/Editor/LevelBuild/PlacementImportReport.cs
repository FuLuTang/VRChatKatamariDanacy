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

        /// <summary>
        /// Props whose authored scale had to be divided by a non-1 parent scale so the WORLD size
        /// matches the document. Zero on a clean managed tree; a non-zero value names a container
        /// that is not identity (the notes name it, prop by prop).
        /// </summary>
        public int PoseScaleCompensated;

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
        /// The level's total growth in mm^3, derived from each prop's aabb x k x penalty.
        /// 64-bit because the ledger is; the other two counters are row counts.
        /// </summary>
        public long GrowthMm3Declared;
        public int GrowthFileRows;
        public int GrowthBoundsRows;
        public bool GrowthVerified;
        /// <summary>Every declared collider proxy read back bit-identically.</summary>
        public bool ProxyVerified;

        public int ArenaBoxes;
        public int ArenaRamps;
        public int ArenaPlanes;
        public int ArenaKinds;
        public bool ArenaWired;

        // ---- v2 layers (a v1 document leaves every one of these at its default) ----
        /// <summary>True when the run read a <c>katamari.placement/2</c> document.</summary>
        public bool V2Document;
        /// <summary>How long the v2 reader took, for the operator's "is this slow" question.</summary>
        public double V2ParseMilliseconds;
        public int V2Props;
        public int V2Defs;
        public int V2Solids;
        public int V2ParentEdges;
        public int V2NonUniformScales;
        public int V2WallSegments;
        public long V2GrowthMm3Declared;
        /// <summary>A prop priced its growth from the <c>volumeMm3</c> alias (growth was absent).</summary>
        public bool V2GrowthAliasUsed;
        /// <summary>The document declared at least one <c>absorbable:false</c> prop (not enforced).</summary>
        public bool V2AbsorbableDeclarations;
        public int V2WallsCreated;
        public int V2WallsUpdated;
        public int V2WallsDeleted;
        public int V2WallsSkipped;
        public int V2MarkersCreated;
        public int V2MarkersUpdated;
        public int V2MarkersSkipped;
        public int V2MetaFieldsWritten;
        /// <summary>
        /// Writes to scene objects outside the managed tree that the importer owns for a level
        /// with <c>meta.playerStart</c> (the descriptor's spawn and respawn height, the ball's
        /// start). Counted in the headline's <c>other=</c> so the idempotency gate sees them.
        /// </summary>
        public int V2SceneWiringWrites;
        /// <summary>Rows the MotionHost carries (own program + riders + animation-only).</summary>
        public int V2MotionRows;
        public int V2MotionPaths;
        public int V2MotionAnimators;
        /// <summary>Host components created plus columns rewritten; 0 on a second import.</summary>
        public int V2MotionHostWrites;
        public int V2TerrainTriangles;
        public int V2TerrainChunks;

        public bool Success;
        public string Failure;

        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> GrowthProblems = new List<string>();

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
            string v2 = V2Document
                ? " v2=" + V2Props + "/" + V2Defs + "/" + V2Solids + "/" + V2WallSegments
                : "";
            return "[level]" + arena + " +" + PropsCreated + " ~" + PropsUpdated + " -" + PropsDeleted +
                   " props=" + (PropsCreated + PropsUpdated + PropsSkipped) +
                   " other=" + (StructuresCreated + StructuresUpdated + StructuresDeleted +
                                      V2SceneWiringWrites + V2MotionHostWrites) +
                   " errors=" + PropErrors + v2;
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
            text.Append("  growthMm3: declared=").Append(GrowthMm3Declared)
                .Append(", fromFile=").Append(GrowthFileRows)
                .Append(", fromBounds=").Append(GrowthBoundsRows)
                .Append(", read-back: ").Append(GrowthVerified ? "verified" : "NOT verified");
            text.AppendLine();
            text.Append("  arena: ").Append(ArenaWired ? "wired" : "not wired")
                .Append(" (boxes=").Append(ArenaBoxes).Append(", walls=").Append(ArenaPlanes)
                .Append(", ramps=").Append(ArenaRamps).Append(", kinds=")
                .Append(ArenaKinds).Append(")");

            if (V2Document)
            {
                text.AppendLine();
                text.Append("  v2 layers: solids=").Append(V2Solids)
                    .Append(", parents=").Append(V2ParentEdges)
                    .Append(", nonUniformScales=").Append(V2NonUniformScales)
                    .Append(", motion host rows=").Append(V2MotionRows).Append(" (paths=")
                    .Append(V2MotionPaths).Append(", animators=").Append(V2MotionAnimators)
                    .Append(", writes=").Append(V2MotionHostWrites).Append("); walls=").Append(V2WallSegments)
                    .Append(" segment(s): +").Append(V2WallsCreated).Append(" ~")
                    .Append(V2WallsUpdated).Append(" -").Append(V2WallsDeleted)
                    .Append(" skip=").Append(V2WallsSkipped);
                text.AppendLine();
                text.Append("  v2 meta: fields written=").Append(V2MetaFieldsWritten)
                    .Append(", scene wiring writes=").Append(V2SceneWiringWrites)
                    .Append(", markers: +").Append(V2MarkersCreated).Append(" ~")
                    .Append(V2MarkersUpdated).Append(" skip=").Append(V2MarkersSkipped)
                    .Append(", terrain=").Append(V2TerrainTriangles).Append(" tri/")
                    .Append(V2TerrainChunks).Append(" chunk");
                text.AppendLine();
                text.Append("  v2 read: ").Append(V2Props).Append(" props, ").Append(V2Defs)
                    .Append(" defs, ").Append(V2Solids).Append(" solids, growthMm3=")
                    .Append(V2GrowthMm3Declared)
                    .Append(V2GrowthAliasUsed ? " (alias used for >=1 prop)" : "")
                    .Append(", parsed in ")
                    .Append(V2ParseMilliseconds.ToString("0.0",
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" ms");
            }

            for (int i = 0; i < GrowthProblems.Count; i++)
            {
                text.AppendLine();
                text.Append("  growthMm3: ").Append(GrowthProblems[i]);
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

        public string AreaCollisionSummary;

        public void Log()
        {
            if (!string.IsNullOrEmpty(AreaCollisionSummary)) UnityEngine.Debug.Log(AreaCollisionSummary);
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
