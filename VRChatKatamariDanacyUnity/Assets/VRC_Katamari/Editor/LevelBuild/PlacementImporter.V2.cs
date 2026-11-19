using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UdonSharp;
using UdonSharpEditor;
using VRCKatamari.BallMotor;
using VRCKatamari.Story;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// The v2 half of <see cref="PlacementImporter"/>: reads a <c>katamari.placement/2</c>
    /// document and drives the SAME scene-building steps the v1 half uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shape of the branch. <see cref="PlacementV2Reader"/> produces the file model;
    /// <see cref="BuildV1View"/> maps it onto the <see cref="PlacementDocument"/> the existing
    /// builder already consumes (props, defs, structures, budget, arena); from there
    /// <c>ApplyProps</c>, <c>VerifyPropRows</c>, <c>ApplyStructures</c>, the pickup sync and
    /// <c>BindArenaHost</c> run unchanged. Only the layers v1 does not have are new here:
    /// <c>activityArea.walls</c>, the <c>meta</c> wiring and the terrain hand-off. No layer has
    /// a second implementation.
    /// </para>
    /// <para>
    /// Log tags. Everything that v1 also has keeps the v1 spelling and the plain
    /// <c>[level]</c> headline (<c>+N ~N -N props=… other=… errors=…</c>), so the operator's
    /// idempotency gate and habits do not change. The v2-only layers report their own lines
    /// through <see cref="PlacementImportReport"/> with the <c>[level:v2]</c> prefix: meta
    /// wiring, the activity-wall layer, the parent edges and the terrain hand-off. Errors that
    /// have a §9 tag keep that tag as their first token on top of the prefix, e.g.
    /// <c>[level:v2] [structure_capacity] …</c>, so a grep for either works.
    /// </para>
    /// <para>
    /// Idempotency. Every write is compared first (pose within 1 mm, rotation dot 0.99999,
    /// scale within 1 mm, string/float/number equality for the meta fields), and every layer
    /// reconciles by name: a second import of the same document reports
    /// <c>+0 ~0 -0 other=0</c>. Marker and wall objects carry no Renderer and no Collider, so
    /// the pickup sync's "topmost Renderer is a row" rule never sees them.
    /// </para>
    /// <para>
    /// Never saves the scene, never touches <c>ProjectSettings</c>, and writes nothing outside
    /// <c>LevelContent/&lt;id&gt;</c> plus the two components the edges are defined on (the
    /// motor's start size and the round controller's level data).
    /// </para>
    /// </remarks>
    public static partial class PlacementImporter
    {
        public const string ActivityWallsFolderName = "ActivityWalls";
        public const string MarkersFolderName = "Markers";
        public const string PlayerSpawnMarkerName = "PlayerSpawn";
        public const string BallStartMarkerName = "BallStart";
        public const string SharedSpawnMarkerName = "Spawn";

        /// <summary>Prefix of every v2-only report line (see the class remarks).</summary>
        private const string V2Tag = "[level:v2] ";

        // ------------------------------------------------------------------ entry ---

        /// <summary>
        /// Imports one v2 document. Called by <see cref="ImportInternal"/> once the text's
        /// schema is known to be <c>katamari.placement/2</c>; the v1 document never reaches
        /// here, and this method never falls back to the v1 reader.
        /// </summary>
        private static bool ImportV2(string levelId, string path, PlacementV2ReadResult read,
            PlacementImportReport report)
        {
            PlacementV2Document doc = read.Document;
            report.V2Document = true;
            report.V2ParseMilliseconds = read.ParseMilliseconds;

            PlacementV2Rules.PlacementV2Summary summary = PlacementV2Rules.Summarize(doc);
            report.V2Props = summary.PropCount;
            report.V2Defs = summary.DefCount;
            report.V2Solids = summary.SolidCount;
            report.V2ParentEdges = summary.ParentCount;
            report.V2NonUniformScales = summary.NonUniformScaleCount;
            report.V2WallSegments = summary.WallSegmentCount;
            report.V2GrowthMm3Declared = summary.GrowthMm3Total;

            report.Note(V2Tag + path + ": schema katamari.placement/2, parsed in " +
                        read.ParseMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) +
                        " ms: props=" + summary.PropCount + " defs=" + summary.DefCount +
                        " solids=" + summary.SolidCount + " parents=" + summary.ParentCount +
                        " nonUnitScales=" + (summary.UniformScaleCount + summary.NonUniformScaleCount) +
                        " wallSegments=" + summary.WallSegmentCount);

            for (int i = 0; i < read.Warnings.Count; i++) report.Note(read.Warnings[i]);
            for (int i = 0; i < read.Errors.Count; i++) report.Error(read.Errors[i]);
            if (!read.Ok)
            {
                report.Fail("the v2 document did not parse (" + read.Errors.Count +
                            " error(s)); nothing in the scene was changed");
                return false;
            }

            PlacementV2Meta meta = doc.Meta;
            if (meta == null || string.IsNullOrEmpty(meta.Id))
            {
                report.Fail("the v2 document has no meta.id; it names the managed root, so the " +
                            "import cannot start");
                return false;
            }

            // A reference level may only be written into a scene under Assets/_RefUnlicensed/
            // (§11.3): its props and terrain never enter git or an upload.
            if (doc.IsReference)
            {
                string scenePath;
                if (!LevelPaths.IsReferenceScene(out scenePath))
                {
                    report.Fail("[level_scene_guard] '" + meta.Id + "' is unlicensed reference data " +
                                "and may only be imported into a scene under " +
                                LevelPaths.ReferenceSceneRoot + " (the active scene is '" +
                                scenePath + "')");
                    return false;
                }

                report.Note(V2Tag + "reference level in '" + scenePath + "': its objects and terrain " +
                            "stay out of git and out of any upload");
            }

            bool structureOk = ValidateDocumentV2(doc, summary, report);

            Transform managedRoot;
            if (!FindOrCreateManagedRoot(report, out managedRoot))
            {
                return false;
            }

            // §17.5: the level id names its own root, so a scene may hold several levels. The
            // name is meta.id, NOT the argument, because the terrain import and every later run
            // resolve the root by the document's own id.
            Transform levelRoot = EnsureFolder(managedRoot, meta.Id, report);
            if (levelRoot == null)
            {
                report.Fail("the level root '" + meta.Id + "' collides with an object that is not " +
                            "a folder; rename the level in the data");
                return false;
            }

            if (!string.Equals(meta.Id, LevelPaths.LeafOf(levelId), StringComparison.Ordinal))
            {
                report.Note(V2Tag + "imported by id '" + levelId + "' into root '" + meta.Id +
                            "' (the document's own meta.id names the root)");
            }

            report.ArenaId = meta.Id;
            report.ManagedRootPath = HierarchyPath(levelRoot);

            Transform propsRoot = EnsureFolder(levelRoot, PropsFolderName, report);
            Transform structuresRoot = EnsureFolder(levelRoot, StructuresFolderName, report);
            if (propsRoot == null || structuresRoot == null)
            {
                report.Fail("the managed props/structures folder collides with an object that is not " +
                            "a folder; the managed subtree was not imported");
                return false;
            }

            PlacementDocument view = BuildV1View(doc);
            bool propsOk = ApplyProps(view, propsRoot, report);
            if (propsOk) propsOk = VerifyPropRows(view, propsRoot, report);
            bool structuresOk = ApplyStructures(view, structuresRoot, report);
            bool wallsOk = ApplyActivityWalls(doc, levelRoot, report);

            // Only the prop tree owns empty folders (kind/zone/Hierarchy/Groups).
            DeleteEmptyFolders(propsRoot);
            MarkSceneDirty();

            if (propsOk)
            {
                TrySyncDeclaredFields(view, levelRoot, propsRoot, report,
                    BuildNonFieldProps(doc, report), doc.IsReference ? path : null);
                ApplyMotionHost(doc, levelRoot, propsRoot, report);
            }

            BindArenaHost(view, levelRoot, structuresRoot, report,
                CollectWallMarkers(levelRoot, doc), meta.HasGroundY, meta.GroundY);
            BindLevelMeta(doc, levelRoot, report);
            ChainTerrainImport(doc, report);
            if (propsOk) ApplyPropFallHost(doc, levelRoot, report);

            return structureOk && propsOk && structuresOk && wallsOk;
        }

        // -------------------------------------------------------- document view ---

        /// <summary>
        /// Maps the v2 file model onto the v1 document the shared builder consumes.
        /// </summary>
        /// <remarks>
        /// The mapping is deliberately mechanical: one v2 block to one v1 DTO field, with the
        /// v2-only keys (<c>scale</c>, <c>behaviour</c>, <c>inherit</c>, <c>parent</c>,
        /// <c>absorbable</c>) carried on the DTO's optional columns. Nothing is derived here
        /// that the v2 reader did not read, and the growth column is the AUTHORITY the reader
        /// resolved (owner ruling B) rather than a re-pricing.
        /// </remarks>
        private static PlacementDocument BuildV1View(PlacementV2Document doc)
        {
            PlacementV2Meta meta = doc.Meta;
            var view = new PlacementDocument();
            view.schema = PlacementV2Reader.SchemaName;
            view.level = new LevelBlock
            {
                id = meta.Id,
                scene = meta.Scene,
                ballDiameterStart = meta.Ball != null ? meta.Ball.StartDiameterM : 0f,
                // The v1 DTO still has this column, but nothing in the shared builder reads
                // it: the one runtime size cap is the motor's global constant (owner
                // 2026-11-18), and a curve ramp's chord budget reads ballDiameterStart above.
                ballDiameterMax = KatamariBallMotor.GlobalMaxBallDiameterM,
                arena = new ArenaBlock { id = meta.Id },
                budget = new BudgetBlock
                {
                    planes = meta.Budget != null && meta.Budget.Planes > 0
                        ? meta.Budget.Planes
                        : 8,
                    boxes = meta.Budget != null && meta.Budget.Boxes > 0 ? meta.Budget.Boxes : 8,
                    ramps = meta.Budget != null && meta.Budget.Ramps > 0 ? meta.Budget.Ramps : 4,
                    blocking = meta.Budget != null && meta.Budget.Blocking > 0
                        ? meta.Budget.Blocking
                        : 6
                }
            };

            view.defs = new DefEntry[doc.Defs.Length];
            for (int i = 0; i < doc.Defs.Length; i++)
            {
                PlacementV2Def def = doc.Defs[i];
                view.defs[i] = def == null
                    ? null
                    : new DefEntry
                    {
                        id = def.Id,
                        kind = def.Kind,
                        model = def.Model,
                        valueUnits = def.ValueUnits,
                        volumeK = def.VolumeK,
                        volumePenalty = def.VolumePenalty,
                        collider = ToColliderEntry(def.Collider)
                    };
            }

            view.props = new PropEntry[doc.Props.Length];
            for (int i = 0; i < doc.Props.Length; i++)
            {
                PlacementV2Prop prop = doc.Props[i];
                if (prop == null)
                {
                    view.props[i] = null;
                    continue;
                }

                PlacementV2Def def = PlacementV2Rules.DefOf(doc, prop);
                long growth;
                bool fromVolumeAlias;
                PlacementV2Rules.GrowthError(prop, PlacementV2Rules.EffectiveVolumePenalty(prop, def),
                    out growth, out fromVolumeAlias);
                view.props[i] = new PropEntry
                {
                    itemId = prop.ItemId,
                    def = prop.Def,
                    kind = prop.Kind,
                    zone = prop.Zone,
                    group = prop.RigidGroup,
                    parent = prop.Parent,
                    behaviour = prop.Behaviour,
                    inherit = prop.Inherit,
                    scale = prop.Scale,
                    absorbable = prop.Absorbable,
                    position = prop.Position,
                    rotationQuat = prop.RotationQuat,
                    aabb = prop.Aabb,
                    support = prop.Support,
                    stackedOn = prop.StackedOn,
                    volumeK = prop.VolumeK,
                    volumePenalty = prop.VolumePenalty,
                    growthMm3 = growth,
                    notes = prop.Notes
                };
            }

            // A solid whose id is also a prop's id is that prop's collision: the prop carries
            // the model, so the solid's cube is not drawn (StructureEntry.bodyIsProp).
            var propIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < doc.Props.Length; i++)
            {
                if (doc.Props[i] != null && !string.IsNullOrEmpty(doc.Props[i].ItemId))
                {
                    propIds.Add(doc.Props[i].ItemId);
                }
            }

            view.structures = new StructureEntry[doc.Solids.Length];
            for (int i = 0; i < doc.Solids.Length; i++)
            {
                PlacementV2Solid solid = doc.Solids[i];
                if (solid == null)
                {
                    view.structures[i] = null;
                    continue;
                }

                PlacementV2Rules.PlacementSolidContactBox contact;
                string contactError;
                bool hasContact = PlacementV2Rules.TrySolidContactBox(solid, out contact,
                    out contactError);
                view.structures[i] = new StructureEntry
                {
                    id = solid.Id,
                    kind = solid.Kind,
                    role = solid.Role,
                    shape = solid.Shape,
                    center = solid.Center,
                    size = solid.Size,
                    rotationQuat = solid.RotationQuat,
                    collision = solid.Collision,
                    support = solid.Support,
                    meets = solid.Meets,
                    collider = ToColliderEntry(solid.Collider),
                    proxySource = solid.ProxySource,
                    contactIsProxy = hasContact,
                    contactCenter = hasContact ? contact.Center : null,
                    contactSize = hasContact ? contact.Size : null,
                    contactPartCenters = hasContact ? FlattenParts(contact.Parts, true) : null,
                    contactPartSizes = hasContact ? FlattenParts(contact.Parts, false) : null,
                    bodyIsProp = hasContact && string.Equals(solid.Shape, "proxy", StringComparison.Ordinal) &&
                                 propIds.Contains(solid.Id),
                    curve = solid.Curve == null
                        ? null
                        : new CurveEntry
                        {
                            radius_m = solid.Curve.RadiusM,
                            arc_deg = solid.Curve.ArcDeg,
                            segments = solid.Curve.Segments,
                            bend = solid.Curve.Bend,
                            width_m = solid.Curve.WidthM,
                            thickness_m = solid.Curve.ThicknessM
                        }
                };
            }

            return view;
        }

        /// <summary>A solid's part boxes as one flat <c>3N</c> array (centres or sizes).</summary>
        private static float[] FlattenParts(PlacementV2Rules.PlacementSolidContactBox[] parts,
            bool centers)
        {
            if (parts == null || parts.Length == 0) return null;
            var flat = new float[parts.Length * 3];
            for (int i = 0; i < parts.Length; i++)
            {
                float[] source = centers ? parts[i].Center : parts[i].Size;
                flat[i * 3] = source[0];
                flat[i * 3 + 1] = source[1];
                flat[i * 3 + 2] = source[2];
            }

            return flat;
        }

        private static ColliderEntry ToColliderEntry(PlacementV2Collider collider)
        {
            if (collider == null) return null;
            return new ColliderEntry
            {
                type = collider.Type,
                center = collider.Center,
                size = collider.Size,
                radius = collider.Radius,
                halfHeight = collider.HalfHeight,
                axis = collider.Axis,
                hull = collider.Hull,
                source = collider.Source,
                fit_error = collider.FitError
            };
        }

        // ------------------------------------------------------------ validation ---

        /// <summary>
        /// The v2 design rules that can be judged without a scene, reported one line each.
        /// </summary>
        /// <returns>
        /// False only when the document cannot be built at all (it cannot: the caller has
        /// already refused a missing meta). Capacity problems are errors in the REPORT, not a
        /// stop: the objects are still created so the operator can look at them (owner ruling E).
        /// </returns>
        private static bool ValidateDocumentV2(PlacementV2Document doc,
            PlacementV2Rules.PlacementV2Summary summary, PlacementImportReport report)
        {
            PlacementV2Meta meta = doc.Meta;

            if (meta.Expected != null)
            {
                if (meta.Expected.PropCount >= 0 && meta.Expected.PropCount != summary.PropCount)
                {
                    report.Error("[level:v2] [caps_props] meta.expected.propCount=" +
                                 meta.Expected.PropCount + " but the document has " +
                                 summary.PropCount + " props");
                }

                if (meta.Expected.StructureCount >= 0 &&
                    meta.Expected.StructureCount != summary.SolidCount)
                {
                    report.Error("[level:v2] [structure_role] meta.expected.structureCount=" +
                                 meta.Expected.StructureCount + " but structures.solids has " +
                                 summary.SolidCount);
                }

                if (meta.Expected.GrowthMm3 >= 0 && meta.Expected.GrowthMm3 != summary.GrowthMm3Total)
                {
                    report.Error("[level:v2] [prop_volume_stale] meta.expected.growthMm3=" +
                                 meta.Expected.GrowthMm3 + " but the props sum to " +
                                 summary.GrowthMm3Total);
                }
            }

            if (meta.Caps != null)
            {
                if (meta.Caps.Props >= 0 && summary.PropCount > meta.Caps.Props)
                {
                    report.Error("[level:v2] [caps_props] " + summary.PropCount +
                                 " props over meta.caps.props=" + meta.Caps.Props);
                }

                if (meta.Caps.StaticSolids >= 0 && summary.SolidCount > meta.Caps.StaticSolids)
                {
                    report.Error("[level:v2] [structure_capacity] " + summary.SolidCount +
                                 " solids over meta.caps.staticSolids=" + meta.Caps.StaticSolids);
                }

                if (meta.Caps.ActivityWalls >= 0 && summary.WallSegmentCount > meta.Caps.ActivityWalls)
                {
                    report.Error("[level:v2] [activity_wall_capacity] " + summary.WallSegmentCount +
                                 " wall segments over meta.caps.activityWalls=" +
                                 meta.Caps.ActivityWalls);
                }
            }

            if (meta.Budget != null && meta.Budget.Planes >= 0 &&
                summary.WallSegmentCount > meta.Budget.Planes)
            {
                report.Error("[level:v2] [activity_wall_capacity] " + summary.WallSegmentCount +
                             " wall segments over meta.budget.planes=" + meta.Budget.Planes +
                             " (the plane channel is the v1 registration; the v2 OBB slab table " +
                             "needs an ActivityAreaHost, which does not exist yet)");
            }

            // The motor's static OBB table. A document whose solids exceed it is an error that
            // names both numbers, because silently dropping the overflow is the failure mode
            // this check exists for.
            int boxCapacity = StaticObbCapacity;
            int boxes = 0;
            for (int i = 0; i < doc.Solids.Length; i++)
            {
                PlacementV2Solid solid = doc.Solids[i];
                if (solid == null) continue;
                if (string.Equals(solid.Role, "box", StringComparison.Ordinal) ||
                    string.Equals(solid.Role, "ramp", StringComparison.Ordinal))
                {
                    // A split proxy solid hands the table one OBB per part (§4).
                    boxes += PlacementV2Rules.SolidStaticObbCount(solid);
                }
            }

            if (boxes > boxCapacity)
            {
                report.Error("[level:v2] [structure_capacity] " + boxes + " > " + boxCapacity +
                             " static solids; the motor's table refuses the overflow. " +
                             "Raise the table or shrink the level. " +
                             "The objects are still created so you can inspect them");
            }

            for (int i = 0; i < doc.Props.Length; i++)
            {
                PlacementV2Prop prop = doc.Props[i];
                if (prop == null) continue;
                PlacementV2Def def = PlacementV2Rules.DefOf(doc, prop);
                string scaleError = PlacementV2Rules.ScaleError(prop, def);
                if (scaleError != null) report.Error("[level:v2] " + scaleError);

                long growth;
                bool fromAlias;
                string growthError = PlacementV2Rules.GrowthError(prop,
                    PlacementV2Rules.EffectiveVolumePenalty(prop, def), out growth, out fromAlias);
                if (growthError != null) report.Error("[level:v2] " + growthError);
                if (fromAlias && !report.V2GrowthAliasUsed)
                {
                    report.V2GrowthAliasUsed = true;
                    report.Note(V2Tag + "at least one prop prices its growth from the volumeMm3 " +
                                "alias (growthMm3 is the authority; the alias is read only when it " +
                                "is absent)");
                }

                if (!prop.HasAbsorbable)
                {
                    report.Note(V2Tag + prop.ItemId + " has no absorbable key; treated as absorbable" +
                                " (the schema requires it, the reader defaults to true)");
                }
                else if (!prop.Absorbable && !report.V2AbsorbableDeclarations)
                {
                    report.V2AbsorbableDeclarations = true;
                    report.Note(V2Tag + "the document declares at least one absorbable:false prop. " +
                                "This round stores and reports the declaration but does not enforce " +
                                "it: PickupFieldHost has no absorbable column, so the gate still " +
                                "decides from k * aabb (owner ruling D)");
                }
            }

            // §4/§9: a solid's contact shape may come from a proxy; the runtime's static table
            // is OBB-only, so a non-box proxy reaches the scene as its local AABB box. That is
            // stated per family rather than silently - owner ruling 3.
            int proxySolids = 0;
            int exactBoxProxies = 0;
            int boxedProxies = 0;
            int raisedTops = 0;
            int splitProxies = 0;
            int partBoxes = 0;
            int fallbackProxies = 0;
            int legacyPlaneSolids = 0;
            for (int i = 0; i < doc.Solids.Length; i++)
            {
                PlacementV2Solid solid = doc.Solids[i];
                if (solid == null) continue;
                string proxyError = PlacementV2Rules.SolidProxyError(solid);
                if (proxyError != null)
                {
                    report.Error("[level:v2] " + proxyError);
                    continue;
                }

                if (string.Equals(solid.Shape, PlacementV2Rules.ShapePlane, StringComparison.Ordinal))
                {
                    legacyPlaneSolids++;
                    continue;
                }

                if (!string.Equals(solid.Shape, PlacementV2Rules.ShapeProxy, StringComparison.Ordinal))
                {
                    continue;
                }

                proxySolids++;
                if (string.Equals(solid.ProxySource, PlacementV2Rules.ProxySourceAabbFallback,
                    StringComparison.Ordinal))
                {
                    fallbackProxies++;
                }

                PlacementV2Rules.PlacementSolidContactBox box;
                string contactError;
                if (!PlacementV2Rules.TrySolidContactBox(solid, out box, out contactError)) continue;
                if (box.Parts != null)
                {
                    splitProxies++;
                    partBoxes += box.Parts.Length;
                    continue;
                }

                if (box.Exact) exactBoxProxies++;
                else boxedProxies++;
                if (box.TopRaisedToSizeTop) raisedTops++;
            }

            if (proxySolids > 0)
            {
                report.Note(V2Tag + "[structure_proxy] " + proxySolids + " solid(s) carry a contact " +
                            "proxy: " + exactBoxProxies + " exact box(es)" +
                            (splitProxies > 0
                                ? ", " + splitProxies + " split into " + partBoxes +
                                  " part box(es) cut from the model"
                                : "") +
                            (boxedProxies > 0
                                ? ", " + boxedProxies + " boxed to the proxy's local AABB" +
                                  " (the motor's static table is OBB-only; a true convex/sphere/" +
                                  "capsule static collider needs a new channel)"
                                : "") +
                            (raisedTops > 0
                                ? "; " + raisedTops + " contact box top(s) raised to the size top" +
                                  " (the support face)"
                                : "") +
                            (fallbackProxies > 0
                                ? "; " + fallbackProxies + " declared proxySource=aabb-fallback"
                                : ""));
            }

            if (legacyPlaneSolids > 0)
            {
                report.Note(V2Tag + "[structure_role] " + legacyPlaneSolids +
                            " solid(s) are shape:\"plane\"; a v2 file should not write one " +
                            "(the boundary role moved to activityArea)");
            }

            if (summary.ParentError != null)
            {
                report.Error("[level:v2] " + summary.ParentError);
            }
            else if (summary.ParentCount > 0)
            {
                report.Note(V2Tag + summary.ParentCount + " parent edge(s), " +
                            summary.ParentRootCount + " root(s), max depth " +
                            summary.ParentMaxDepth + ": built as Hierarchy/<parent>_children folders (owner ruling A), " +
                            "not as GameObject parenting, so the pickup rows stay one-per-prop. " +
                            "The edges are also reported here for the future propParent column");
            }

            if (doc.Activity != null)
            {
                int gates = 0;
                for (int i = 0; i < doc.Activity.Walls.Length; i++)
                {
                    PlacementV2Wall wall = doc.Activity.Walls[i];
                    string wallError = PlacementV2Rules.WallError(wall);
                    if (wallError != null) report.Error("[level:v2] " + wallError);
                    if (wall != null && wall.HasMinBallDiameter) gates++;
                    if (wall != null && !string.Equals(wall.Kind, "invisible", StringComparison.Ordinal))
                    {
                        report.Note(V2Tag + "wall '" + wall.Id + "' is kind='" + wall.Kind +
                                    "': the plane channel registers the blocker but draws nothing, " +
                                    "so a visible wall needs its own art");
                    }
                }

                if (gates > 0)
                {
                    report.Note(V2Tag + gates + " wall(s) declare minBallDiameterM. The plane channel " +
                                "has no gate: a plane is registered for the whole round (the gate " +
                                "needs an ActivityAreaHost, which does not exist yet)");
                }

                if (doc.Activity.SoftMarginM != 0f)
                {
                    report.Note(V2Tag + "softMarginM=" + doc.Activity.SoftMarginM.ToString("0.###",
                                CultureInfo.InvariantCulture) + " is not implemented (v1 fixes it at 0)");
                }

                if (doc.Activity.KillPlane != null)
                {
                    PlacementV2KillPlane kill = doc.Activity.KillPlane;
                    if (meta.HasGroundY && kill.Y >= meta.GroundY)
                    {
                        report.Error("[level:v2] [activity_kill_plane] killPlane.y=" + kill.Y +
                                     " is not below groundY=" + meta.GroundY);
                    }

                    if (string.Equals(kill.Respawn, "ballStart", StringComparison.Ordinal) &&
                        (meta.Ball == null || meta.Ball.Start == null))
                    {
                        report.Error("[level:v2] [activity_kill_plane] killPlane.respawn is " +
                                     "'ballStart' but meta.ball.start is missing");
                    }

                    if (kill.RespawnPoint == null &&
                        !string.Equals(kill.Respawn, "ballStart", StringComparison.Ordinal))
                    {
                        report.Error("[level:v2] [activity_kill_plane] killPlane.respawn must be " +
                                     "'ballStart' or [x,y,z]");
                    }
                }
            }

            if (doc.Paths.Length > 0 || doc.Motions.Length > 0)
            {
                report.Note(V2Tag + "the document has " + doc.Motions.Length + " motion(s) and " +
                            doc.Paths.Length + " path(s); they are parsed and preserved but not " +
                            "executed (no MotionHost yet), so every prop imports as authored");
            }

            return true;
        }

        // -------------------------------------------------------- activity walls ---

        /// <summary>
        /// Builds one bare Transform per wall SEGMENT under
        /// <c>LevelContent/&lt;id&gt;/ActivityWalls/&lt;wall&gt;_segNN</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Why a separate layer and not a <c>structures</c> entry: a structure is a body
        /// (collider and, for a marker role, a visible one), while an activity wall is a RULE -
        /// "the ball cannot leave" - with no geometry the player may stand on (§5.3: a wall
        /// never supports). Keeping them out of <c>Structures/</c> also keeps
        /// <c>ApplyStructures</c>'s stale sweep from deleting them, because that sweep owns the
        /// names under <c>Structures/</c>.
        /// </para>
        /// <para>
        /// The plane the arena bakes is <c>normal = transform.forward</c>,
        /// <c>offset = dot(normal, position)</c>, free space on the positive side, so the
        /// Transform's forward is the segment's INWARD normal and its position is the segment
        /// midpoint at the level's ground height. Height and thickness are not representable in
        /// a plane and are reported instead of silently ignored (owner ruling F).
        /// </para>
        /// </remarks>
        private static bool ApplyActivityWalls(PlacementV2Document doc, Transform levelRoot,
            PlacementImportReport report)
        {
            PlacementV2Rules.PlacementV2WallSegment[] segments = PlacementV2Rules.AllWallSegments(doc, out string error);
            if (error != null)
            {
                // WallError already reported the per-wall reason in validation; this is the
                // "the layer as a whole cannot be built" line.
                report.Error("[level:v2] " + error);
                return false;
            }

            int segmentCount = segments.Length;
            report.V2WallSegments = segmentCount;
            if (segmentCount == 0)
            {
                return true;
            }

            Transform wallsRoot = EnsureFolder(levelRoot, ActivityWallsFolderName, report);
            if (wallsRoot == null)
            {
                report.Error("[level:v2] the walls folder collides with an object that is not a " +
                             "folder; the activity walls were not created");
                return false;
            }

            float groundY = doc.Meta != null && doc.Meta.HasGroundY ? doc.Meta.GroundY : 0f;
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            bool ok = true;
            for (int i = 0; i < segments.Length; i++)
            {
                PlacementV2Rules.PlacementV2WallSegment segment = segments[i];
                wanted.Add(segment.Id);
                Transform marker = FindChild(wallsRoot, segment.Id);
                bool created = false;
                if (marker == null)
                {
                    var go = new GameObject(segment.Id);
                    Undo.RegisterCreatedObjectUndo(go, UndoLabel);
                    Undo.SetTransformParent(go.transform, wallsRoot, UndoLabel);
                    marker = go.transform;
                    created = true;
                }

                var position = new Vector3(segment.Mid[0], groundY, segment.Mid[1]);
                var rotation = Quaternion.LookRotation(
                    new Vector3(segment.InwardNormal[0], 0f, segment.InwardNormal[1]), Vector3.up);
                bool matches = PoseMatches(marker, position, rotation, Vector3.one);
                if (!matches)
                {
                    Undo.RecordObject(marker, UndoLabel);
                    marker.position = position;
                    marker.rotation = rotation;
                    marker.localScale = Vector3.one;
                }

                if (created)
                {
                    report.V2WallsCreated++;
                }
                else if (!matches)
                {
                    report.V2WallsUpdated++;
                }
                else
                {
                    report.V2WallsSkipped++;
                }

                if (marker.GetComponent<Renderer>() != null || marker.GetComponent<Collider>() != null)
                {
                    report.Error("[level:v2] the wall marker '" + segment.Id + "' carries a Renderer " +
                                 "or a Collider; a wall is a rule and must have neither");
                    ok = false;
                }
            }

            var stale = new List<string>();
            for (int i = 0; i < wallsRoot.childCount; i++)
            {
                Transform child = wallsRoot.GetChild(i);
                if (!wanted.Contains(child.name)) stale.Add(child.name);
            }

            stale.Sort(StringComparer.Ordinal);
            for (int i = 0; i < stale.Count; i++)
            {
                Transform doomed = FindChild(wallsRoot, stale[i]);
                if (doomed == null) continue;
                Undo.DestroyObjectImmediate(doomed.gameObject);
                report.V2WallsDeleted++;
            }

            report.Note(V2Tag + "activity walls: " + segmentCount + " plane segment(s) in " +
                        HierarchyPath(wallsRoot) + " (+" + report.V2WallsCreated + " ~" +
                        report.V2WallsUpdated + " -" + report.V2WallsDeleted + "), all at groundY=" +
                        groundY.ToString("0.###", CultureInfo.InvariantCulture) +
                        ", forward = inward normal");
            return ok;
        }

        /// <summary>The wall markers this document owns, for <c>BindArenaHost</c>'s plane list.</summary>
        private static Transform[] CollectWallMarkers(Transform levelRoot, PlacementV2Document doc)
        {
            PlacementV2Rules.PlacementV2WallSegment[] segments = PlacementV2Rules.AllWallSegments(doc, out string error);
            if (error != null || segments.Length == 0) return null;
            Transform wallsRoot = FindChild(levelRoot, ActivityWallsFolderName);
            if (wallsRoot == null) return null;
            var markers = new List<Transform>();
            for (int i = 0; i < segments.Length; i++)
            {
                Transform marker = FindChild(wallsRoot, segments[i].Id);
                if (marker != null) markers.Add(marker);
            }

            return markers.ToArray();
        }

        // ------------------------------------------------------------------ meta ---

        /// <summary>
        /// Writes the <c>meta</c> layer where its consumers already are: the ball's start size
        /// on the motor, the level id / target / time limit / kill plane on the round, and the
        /// two spawn poses as marker Transforms.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The motor's <c>startDiameterWorldM</c> is the ONE start-size conversion
        /// (its <c>Start</c> recomputes <c>startVolumeMm3</c> through
        /// <c>SizeModelMath.TrySphereVolumeMm3</c>), so the importer writes the diameter and
        /// never the volume: writing both would be a second copy of the same arithmetic, and
        /// the motor's own semantic/core ratio is what the volume needs.
        /// </para>
        /// <para>
        /// UdonSharp behaviour fields are written through <see cref="SerializedObject"/> and
        /// then copied to the backing <c>UdonBehaviour</c>, the two-step contract
        /// <see cref="BindArenaHost"/> already uses. A refused copy is an error naming the
        /// behaviour, never a silent half-write.
        /// </para>
        /// </remarks>
        private static void BindLevelMeta(PlacementV2Document doc, Transform levelRoot,
            PlacementImportReport report)
        {
            PlacementV2Meta meta = doc.Meta;
            var behaviours = new List<UdonSharpBehaviour>();

            KatamariBallMotor motor = UnityEngine.Object.FindObjectOfType<KatamariBallMotor>();
            if (motor == null)
            {
                report.Note(V2Tag + "no KatamariBallMotor in the scene: meta.ball.startDiameterM=" +
                            (meta.Ball == null ? 0f : meta.Ball.StartDiameterM)
                                .ToString("0.####", CultureInfo.InvariantCulture) +
                            " m was not written; set the motor's start size by hand or add the motor");
            }
            else if (meta.Ball != null && meta.Ball.StartDiameterM > 0f)
            {
                var serialized = new SerializedObject(motor);
                SerializedProperty field = serialized.FindProperty("startDiameterWorldM");
                if (field == null)
                {
                    report.Error(V2Tag + "[meta_start_diameter] KatamariBallMotor has no " +
                                 "startDiameterWorldM field; the level's start size was not written");
                }
                else if (Mathf.Abs(field.floatValue - meta.Ball.StartDiameterM) > 1e-5f)
                {
                    field.floatValue = meta.Ball.StartDiameterM;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(motor);
                    report.V2MetaFieldsWritten++;
                    behaviours.Add(motor);
                    report.Note(V2Tag + "startDiameterWorldM=" +
                                meta.Ball.StartDiameterM.ToString("0.####",
                                    CultureInfo.InvariantCulture) +
                                " m on " + motor.name +
                                " (the motor recomputes startVolumeMm3 from it)");
                }
            }

            if (motor != null) AlignBallToStart(doc, motor, behaviours, report);

            StoryRoundController round = UnityEngine.Object.FindObjectOfType<StoryRoundController>();
            if (round == null)
            {
                report.Note(V2Tag + "no StoryRoundController in the scene: target diameter, time " +
                            "limit and kill plane were not written (a lab scene may not have a round)");
            }
            else
            {
                var serialized = new SerializedObject(round);
                bool changed = false;
                changed |= WriteString(serialized, "levelId", meta.Id, report, "levelId");
                if (meta.Ball != null && meta.Ball.HasTargetDiameter && meta.Ball.TargetDiameterM > 0f)
                {
                    changed |= WriteFloat(serialized, "targetDiameterM", meta.Ball.TargetDiameterM,
                        report, "targetDiameterM");
                }

                if (meta.HasTimeLimit && meta.TimeLimitS > 0f)
                {
                    changed |= WriteFloat(serialized, "timeLimitSeconds", meta.TimeLimitS, report,
                        "timeLimitSeconds");
                }

                if (doc.Activity != null && doc.Activity.KillPlane != null)
                {
                    PlacementV2KillPlane kill = doc.Activity.KillPlane;
                    changed |= WriteBool(serialized, "killPlaneEnabled", true, report,
                        "killPlaneEnabled");
                    changed |= WriteFloat(serialized, "killPlaneY", kill.Y, report, "killPlaneY");
                }

                if (changed)
                {
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(round);
                    report.V2MetaFieldsWritten++;
                    behaviours.Add(round);
                }
            }

            Transform playerMarker = ApplySpawnMarkers(doc, levelRoot, report);
            ApplySceneDescriptor(doc, playerMarker, report);

            for (int i = 0; i < behaviours.Count; i++)
            {
                try
                {
                    UdonSharpProgramAsset.CompileAllCsPrograms(false, false);
                    UdonSharpEditorUtility.CopyProxyToUdon(behaviours[i]);
                }
                catch (Exception exception)
                {
                    report.Error(V2Tag + "CopyProxyToUdon threw for " + behaviours[i].name + ": " +
                                 exception.Message + ". The fields are set on the proxy but not on " +
                                 "the backing UdonBehaviour; re-run the import once the compile has " +
                                 "finished");
                }
            }
        }

        /// <summary>
        /// Puts the ball where the level says it starts: the motor's <c>startCenterWorld</c>
        /// (serialized U# field) and the <c>MotorHost</c> object's world position are set to
        /// <c>meta.ball.start</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Same gate as the player's standing surface: only a level that declares
        /// <c>meta.playerStart</c> owns where its players and its ball begin, so a lab cell or
        /// a story level without it keeps whatever the operator placed. No motor in the scene is
        /// a note, never an error.
        /// </para>
        /// <para>
        /// The U# field is written the project's way (see <see cref="BindLevelMeta"/>): through
        /// the proxy's <see cref="SerializedObject"/>, then the caller copies every touched
        /// proxy to its backing <c>UdonBehaviour</c> with <c>CopyProxyToUdon</c> after one
        /// compile; the behaviour is never destroyed or re-created. Values are compared first
        /// (<see cref="PlacementV2Rules.NeedsAlignment"/>, 1 mm), so a second import writes
        /// nothing and counts nothing.
        /// </para>
        /// <para>
        /// There is no ball heading to align: the document carries none and the motor has no
        /// serialized start heading (it starts at heading 0 = +Z and is turned by input), so the
        /// importer only writes the position. The <c>MotorHost</c> rotation is left alone.
        /// </para>
        /// </remarks>
        private static void AlignBallToStart(PlacementV2Document doc, KatamariBallMotor motor,
            List<UdonSharpBehaviour> behaviours, PlacementImportReport report)
        {
            PlacementV2Meta meta = doc.Meta;
            if (meta == null || meta.PlayerStart == null || meta.Ball == null ||
                meta.Ball.Start == null || meta.Ball.Start.Length != 3)
            {
                return;
            }

            float[] target = meta.Ball.Start;
            bool changed = false;

            var serialized = new SerializedObject(motor);
            SerializedProperty field = serialized.FindProperty("startCenterWorld");
            if (field == null)
            {
                report.Error(V2Tag + "KatamariBallMotor has no startCenterWorld field; the ball's " +
                             "start was not aligned");
            }
            else
            {
                Vector3 current = field.vector3Value;
                if (PlacementV2Rules.NeedsAlignment(new[] { current.x, current.y, current.z },
                        target, PositionToleranceM))
                {
                    field.vector3Value = new Vector3(target[0], target[1], target[2]);
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(motor);
                    if (!behaviours.Contains(motor)) behaviours.Add(motor);
                    changed = true;
                }
            }

            Transform host = motor.transform;
            Vector3 position = host.position;
            if (PlacementV2Rules.NeedsAlignment(new[] { position.x, position.y, position.z },
                    target, PositionToleranceM))
            {
                Undo.RecordObject(host, UndoLabel);
                host.position = new Vector3(target[0], target[1], target[2]);
                EditorUtility.SetDirty(host);
                changed = true;
            }

            if (changed)
            {
                MarkSceneDirty();
                report.V2MetaFieldsWritten++;
                report.V2SceneWiringWrites++;
                report.Note(V2Tag + "ball aligned to meta.ball.start " + FormatPoint(target) +
                            ": startCenterWorld and " + HierarchyPath(host) +
                            " (the document carries no ball heading; the motor starts at +Z)");
            }
        }

        /// <summary>
        /// Creates or updates the spawn markers (<c>Markers/PlayerSpawn</c>,
        /// <c>Markers/BallStart</c>) and returns the player's marker (null when there is none).
        /// </summary>
        /// <remarks>
        /// One marker per DISTINCT authored pose, created by one method: the reference level's
        /// two poses differ, so collapsing them would move one of them, and a document without
        /// <c>meta.playerStart</c> whose two points coincide gets a single <c>Spawn</c> marker
        /// instead of two objects on top of each other. The player's pose is
        /// <c>meta.playerStart</c> (feet position, yaw from <c>facingDeg</c>) when the level
        /// declares one, else <c>meta.playerSpawn</c> with an identity rotation (the old
        /// behaviour). The importer never moves the ball or the player at runtime;
        /// <see cref="ApplySceneDescriptor"/> points the world descriptor at the player's marker.
        /// </remarks>
        private static Transform ApplySpawnMarkers(PlacementV2Document doc, Transform levelRoot,
            PlacementImportReport report)
        {
            PlacementV2Meta meta = doc.Meta;
            Transform markersRoot = EnsureFolder(levelRoot, MarkersFolderName, report);
            if (markersRoot == null)
            {
                report.Error(V2Tag + "the markers folder collides with an object that is not a " +
                             "folder; the spawn markers were not created");
                return null;
            }

            float[] ballStart = meta.Ball == null ? null : meta.Ball.Start;
            PlacementV2PlayerStart start = meta.PlayerStart;
            float[] playerPosition = start != null ? start.Position : meta.PlayerSpawn;
            Quaternion playerRotation = start != null
                ? RotationOfFacing(start.FacingDeg)
                : Quaternion.identity;
            bool same = start == null && SamePoint(ballStart, playerPosition);
            var written = new List<string>();
            Transform playerMarker = null;
            if (same)
            {
                float[] pose = playerPosition ?? ballStart;
                if (pose != null)
                {
                    playerMarker = EnsureMarker(markersRoot, SharedSpawnMarkerName, pose,
                        Quaternion.identity, report);
                    if (playerMarker != null) written.Add(SharedSpawnMarkerName);
                }
            }
            else
            {
                if (ballStart != null &&
                    EnsureMarker(markersRoot, BallStartMarkerName, ballStart, Quaternion.identity,
                        report) != null)
                {
                    written.Add(BallStartMarkerName);
                }

                if (playerPosition != null)
                {
                    playerMarker = EnsureMarker(markersRoot, PlayerSpawnMarkerName, playerPosition,
                        playerRotation, report);
                    if (playerMarker != null) written.Add(PlayerSpawnMarkerName);
                }
            }

            for (int i = 0; i < written.Count; i++)
            {
                report.Note(V2Tag + "spawn marker '" + HierarchyPath(markersRoot) + "/" + written[i] +
                            "'" + (start != null && written[i] == PlayerSpawnMarkerName
                                ? " (feet " + FormatPoint(start.Position) + ", facing " +
                                  start.FacingDeg.ToString("0.###", CultureInfo.InvariantCulture) +
                                  " deg)"
                                : ""));
            }

            return playerMarker;
        }

        /// <summary>The marker's rotation for a <c>facingDeg</c> yaw (heading 0 = +Z).</summary>
        private static Quaternion RotationOfFacing(float facingDeg)
        {
            float[] q = PlacementV2Rules.FacingQuatXyzw(facingDeg);
            return new Quaternion(q[0], q[1], q[2], q[3]);
        }

        private static string FormatPoint(float[] point)
        {
            if (point == null || point.Length != 3) return "none";
            return "(" + point[0].ToString("0.####", CultureInfo.InvariantCulture) + ", " +
                   point[1].ToString("0.####", CultureInfo.InvariantCulture) + ", " +
                   point[2].ToString("0.####", CultureInfo.InvariantCulture) + ")";
        }

        private static Transform EnsureMarker(Transform parent, string name, float[] pose,
            Quaternion rotation, PlacementImportReport report)
        {
            if (pose == null || pose.Length != 3) return null;
            var position = new Vector3(pose[0], pose[1], pose[2]);
            Transform marker = FindChild(parent, name);
            bool created = false;
            if (marker == null)
            {
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, UndoLabel);
                Undo.SetTransformParent(go.transform, parent, UndoLabel);
                marker = go.transform;
                created = true;
            }

            bool matches = PoseMatches(marker, position, rotation, Vector3.one);
            if (!matches)
            {
                Undo.RecordObject(marker, UndoLabel);
                marker.position = position;
                marker.rotation = rotation;
                marker.localScale = Vector3.one;
            }

            if (created) report.V2MarkersCreated++;
            else if (!matches) report.V2MarkersUpdated++;
            else report.V2MarkersSkipped++;
            return marker;
        }

        /// <summary>
        /// Points the world's <c>VRC_SceneDescriptor</c> at the player's marker and derives its
        /// <c>RespawnHeightY</c> from the level's kill plane.
        /// </summary>
        /// <remarks>
        /// Only for a level that declares <c>meta.playerStart</c>: that block says "this level
        /// owns where the player starts", so a document without it leaves the descriptor to the
        /// operator exactly as before. Writes <c>spawns = [marker]</c> and
        /// <c>RespawnHeightY = </c><see cref="PlacementV2Rules.RespawnHeightY"/> (kill plane
        /// minus one metre; the rule and its reason live there). The descriptor is the SDK's own
        /// component (<c>VRC.SDKBase.VRC_SceneDescriptor</c>, which <c>VRCSceneDescriptor</c>
        /// derives from); it is not an Udon behaviour, so there is no proxy copy. A second
        /// import compares first and writes nothing. A scene that already has a <c>HubSpawn</c>
        /// root object keeps its <c>spawns</c> (the player is born in the hub); only
        /// <c>RespawnHeightY</c> is written then.
        /// </remarks>
        private static void ApplySceneDescriptor(PlacementV2Document doc, Transform playerMarker,
            PlacementImportReport report)
        {
            if (doc.Meta == null || doc.Meta.PlayerStart == null) return;
            if (playerMarker == null)
            {
                report.Error(V2Tag + "[meta_player_start] no player marker, so the scene " +
                             "descriptor was not wired");
                return;
            }

            VRC.SDKBase.VRC_SceneDescriptor[] descriptors =
                UnityEngine.Object.FindObjectsOfType<VRC.SDKBase.VRC_SceneDescriptor>();
            if (descriptors.Length == 0)
            {
                report.Note(V2Tag + "no VRC_SceneDescriptor in the scene: spawns and " +
                            "RespawnHeightY were not written (a lab scene may not have one)");
                return;
            }

            if (descriptors.Length > 1)
            {
                report.Error(V2Tag + "the scene has " + descriptors.Length + " VRC_SceneDescriptors " +
                             "(a world has exactly one); spawns and RespawnHeightY were not written");
                return;
            }

            VRC.SDKBase.VRC_SceneDescriptor descriptor = descriptors[0];
            Transform[] spawns = descriptor.spawns;
            bool spawnsMatch = spawns != null && spawns.Length == 1 && spawns[0] == playerMarker;

            // A scene with a hub spawns the player in the hub; this level's spawn marker is
            // only where a start sends them (VRCKatamari.Hub.HubLoopMath.ChooseArrival), so
            // the descriptor's spawns are left alone. RespawnHeightY is still written.
            bool hubOwnsSpawn = !VRCKatamari.Hub.HubSelectMath.LevelMayOwnWorldSpawn(
                SceneHasRoot(VRCKatamari.Editor.Hub.HubAssembler.HubSpawnName));
            if (hubOwnsSpawn)
            {
                spawnsMatch = true;
                report.Note(V2Tag + "the scene has a '" +
                            VRCKatamari.Editor.Hub.HubAssembler.HubSpawnName + "': the world " +
                            "spawn stays in the hub, spawns were not written");
            }

            bool hasKill = doc.Activity != null && doc.Activity.KillPlane != null;
            float respawnY = hasKill
                ? PlacementV2Rules.RespawnHeightY(doc.Activity.KillPlane.Y)
                : descriptor.RespawnHeightY;
            bool respawnMatches = !hasKill ||
                                  Mathf.Abs(descriptor.RespawnHeightY - respawnY) <= 1e-5f;

            if (!hasKill)
            {
                report.Note(V2Tag + "no activityArea.killPlane: RespawnHeightY was left as it is");
            }

            if (spawnsMatch && respawnMatches) return;

            Undo.RecordObject(descriptor, UndoLabel);
            if (!spawnsMatch) descriptor.spawns = new[] { playerMarker };
            if (!respawnMatches) descriptor.RespawnHeightY = respawnY;
            EditorUtility.SetDirty(descriptor);
            MarkSceneDirty();
            report.V2MetaFieldsWritten++;
            report.V2SceneWiringWrites++;
            report.Note(V2Tag + "scene descriptor '" + descriptor.name + "': spawns=[" +
                        HierarchyPath(playerMarker) + "], RespawnHeightY=" +
                        descriptor.RespawnHeightY.ToString("0.###", CultureInfo.InvariantCulture) +
                        (hasKill ? " (kill plane " +
                                   doc.Activity.KillPlane.Y.ToString("0.###",
                                       CultureInfo.InvariantCulture) + " - " +
                                   PlacementV2Rules.RespawnBelowKillPlaneM.ToString("0.###",
                                       CultureInfo.InvariantCulture) + " m)" : ""));
        }

        private static bool SceneHasRoot(string name)
        {
            GameObject[] roots =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // --------------------------------------------------------- pickup exemption ---

        /// <summary>
        /// The props this document declares as NOT participating in the pickup field.
        /// </summary>
        /// <remarks>
        /// D-5's fourth layer (§18.3 ③), and nothing else: non-uniformly scaled, no collider in
        /// its def, not absorbable - the reference level's <c>sparrow_001</c> (a 1.7 cm decal). It
        /// has no contact shape and cannot be absorbed, so a gameplay row would be a row nothing
        /// can use. Saying so here beats a size threshold that drops it silently and then fails its
        /// own read-back with "declared k, no row". A non-uniform prop WITH a collider, an
        /// absorbable one, or a uniform decal all keep their rows: no general rule is loosened.
        /// </remarks>
        private static ISet<string> BuildNonFieldProps(PlacementV2Document doc,
            PlacementImportReport report)
        {
            var exempt = new HashSet<string>(StringComparer.Ordinal);
            if (doc == null || doc.Props == null) return exempt;
            for (int i = 0; i < doc.Props.Length; i++)
            {
                PlacementV2Prop prop = doc.Props[i];
                if (prop == null || string.IsNullOrEmpty(prop.ItemId)) continue;
                if (!prop.HasScale || PlacementV2Rules.IsUniformScale(prop.Scale)) continue;
                if (prop.Absorbable) continue;
                PlacementV2Def def = PlacementV2Rules.DefOf(doc, prop);
                if (def != null && def.Collider != null) continue;
                exempt.Add(prop.ItemId);
            }

            if (exempt.Count > 0)
            {
                var ids = new List<string>(exempt);
                ids.Sort(StringComparer.Ordinal);
                report.Note(V2Tag + exempt.Count + " prop(s) declared as non-participating decals " +
                            "(non-uniform scale, no collider, not absorbable), exempt from the pickup " +
                            "field by design: " + string.Join(", ", ids.ToArray()));
            }

            return exempt;
        }

        // --------------------------------------------------------------- terrain ---

        /// <summary>
        /// Hands the document's <c>terrain</c> block to <see cref="TerrainMeshImporter"/>, which
        /// owns the geometry, the sidecar and the <see cref="TerrainFieldHost"/> write.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Why chained rather than a second menu: one document is one level, and a level with
        /// terrain that needs two menu clicks in the right order is a documentation bug. The
        /// terrain import is idempotent (sha256 + counts), so re-running the placement import
        /// does not rewrite the arrays, and its log lines keep the <c>[terrain]</c> tag so they
        /// stay distinguishable from the <c>[level]</c> / <c>[level:v2]</c> ones.
        /// </para>
        /// <para>
        /// A v1 document NEVER reaches this method: it has no <c>terrain</c> layer, so the v1
        /// import path stays exactly two log lines shorter than a v2 one.
        /// </para>
        /// </remarks>
        private static void ChainTerrainImport(PlacementV2Document doc, PlacementImportReport report)
        {
            if (doc.Terrain == null)
            {
                report.Note(V2Tag + "no terrain block in this document: the motor keeps its " +
                            "groundY fallback plane");
                return;
            }

            TerrainMeshImporter.TerrainImportReport terrain =
                TerrainMeshImporter.Import(doc.Meta.Id);
            terrain.Log();

            report.V2TerrainTriangles = terrain.Triangles;
            report.V2TerrainChunks = terrain.Chunks;
            if (!terrain.Ok)
            {
                for (int i = 0; i < terrain.Errors.Count; i++)
                {
                    report.Error("[level:v2] terrain: " + terrain.Errors[i]);
                }

                return;
            }

            for (int i = 0; i < terrain.Notes.Count; i++) report.Note(V2Tag + "terrain: " +
                terrain.Notes[i]);
            report.Note(V2Tag + "terrain '" + terrain.TerrainId + "': " + terrain.Triangles +
                        " triangles in " + terrain.Chunks + " chunk(s), +" + terrain.Created +
                        " ~" + terrain.Updated + " skip=" + terrain.Skipped);
        }

        // --------------------------------------------------------------- helpers ---

        private static bool WriteFloat(SerializedObject serialized, string name, float value,
            PlacementImportReport report, string label)
        {
            SerializedProperty field = serialized.FindProperty(name);
            if (field == null)
            {
                report.Error("[level:v2] the target component has no " + label +
                             " field; that meta value was not written");
                return false;
            }

            if (Mathf.Abs(field.floatValue - value) <= 1e-5f) return false;
            field.floatValue = value;
            return true;
        }

        private static bool WriteBool(SerializedObject serialized, string name, bool value,
            PlacementImportReport report, string label)
        {
            SerializedProperty field = serialized.FindProperty(name);
            if (field == null)
            {
                report.Error("[level:v2] the target component has no " + label +
                             " field; that meta value was not written");
                return false;
            }

            if (field.boolValue == value) return false;
            field.boolValue = value;
            return true;
        }

        private static bool WriteString(SerializedObject serialized, string name, string value,
            PlacementImportReport report, string label)
        {
            SerializedProperty field = serialized.FindProperty(name);
            if (field == null)
            {
                report.Error("[level:v2] the target component has no " + label +
                             " field; that meta value was not written");
                return false;
            }

            if (string.Equals(field.stringValue, value, StringComparison.Ordinal)) return false;
            field.stringValue = value;
            return true;
        }

        private static bool SamePoint(float[] left, float[] right)
        {
            if (left == null || right == null) return false;
            if (left.Length != 3 || right.Length != 3) return false;
            return Mathf.Abs(left[0] - right[0]) <= PositionToleranceM &&
                   Mathf.Abs(left[1] - right[1]) <= PositionToleranceM &&
                   Mathf.Abs(left[2] - right[2]) <= PositionToleranceM;
        }
    }
}
