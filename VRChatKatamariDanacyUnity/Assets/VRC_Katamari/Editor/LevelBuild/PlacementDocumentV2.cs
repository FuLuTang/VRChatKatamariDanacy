using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using VRCKatamari.BallMotor;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// The v2 level document, read once from
    /// <c>Docs/Design/Levels/&lt;levelId&gt;/placement.json</c>
    /// (<c>katamari.placement/2</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the FILE model: one class per JSON block, field names and defaults matching
    /// <c>Docs/Design/Levels/LEVEL_FORMAT_V2.md</c>, and nothing else. It is deliberately
    /// separate from <see cref="PlacementImporter.PlacementDocument"/>, which is the shape the
    /// existing scene builder already consumes (v1). The v2 importer branch maps this model
    /// onto that one (<c>PlacementImporter.V2.cs</c>), so there is still exactly ONE
    /// implementation of "instantiate a prop, build a structure, sync the pickup field": the
    /// v1 DTOs - not a second set of build steps.
    /// </para>
    /// <para>
    /// Presence is a bool, never <c>field != null</c>. <c>JsonUtility</c> fills every nested
    /// <c>[Serializable]</c> field with a default instance, so <c>collider != null</c> is
    /// always true and <c>parent != null</c> cannot tell "absent" from "written null". Here a
    /// nested block is built ONLY when the author wrote it (<see cref="PlacementV2Prop.Parent"/>
    /// plus <see cref="PlacementV2Prop.HasParent"/>, <see cref="PlacementV2Def.Collider"/> null
    /// when the block is absent or has no <c>type</c>), and absent arrays are empty arrays.
    /// That is the rule the whole reader follows, and
    /// <c>PlacementV2DocumentTests</c> pins it.
    /// </para>
    /// <para>
    /// No <c>UnityEngine</c> dependency: vectors stay <c>float[]</c> (as the v1 DTOs already
    /// do) so this file plus <see cref="LevelJson"/> and <see cref="PlacementV2Rules"/> compile
    /// and run on any C# toolchain, not only inside Unity. The scene-building half converts to
    /// <c>Vector3</c>/<c>Quaternion</c> at the boundary.
    /// </para>
    /// <para>
    /// Error tags. This reader reports STRUCTURAL problems only, in the §9 vocabulary where a
    /// tag exists (<c>[v2_schema]</c>, <c>[meta_units]</c>, <c>[meta_convention]</c>,
    /// <c>[meta_start_diameter]</c>, <c>[prop_*]</c>, <c>[structure_*]</c>); design rules
    /// (scale layers, parent graph, walls, growth key) live in <see cref="PlacementV2Rules"/>
    /// so each is testable on its own. Unknown keys are WARNINGS tagged <c>[v2_key]</c> with
    /// the dotted path: §9 calls an unknown key an error, and it IS an error for a document
    /// our own writer produced - but the reference documents carry author-side extra keys
    /// (<c>meta.expected.growthMm3</c>, <c>migration.volumePenalty</c>) and refusing to open
    /// them would be a papercut, not a safety net. The warning names the key, so nothing is
    /// silently dropped.
    /// </para>
    /// </remarks>
    public sealed class PlacementV2Document
    {
        public string Schema;
        public PlacementV2Meta Meta;

        /// <summary>The <c>terrain</c> block's identity, or null when the document has none.</summary>
        /// <remarks>
        /// The geometry itself (<c>vertices</c>/<c>indices</c>/<c>meshRef</c> + sidecar) is
        /// <see cref="TerrainMeshImporter"/>'s business: it already reads, hashes and validates
        /// it, and duplicating that here would be a second terrain reader. This block only
        /// answers "is there terrain, what is it called", which the importer needs for its
        /// report and for the kill-plane / groundY cross-check.
        /// </remarks>
        public PlacementV2TerrainRef Terrain;

        public PlacementV2Solid[] Solids = new PlacementV2Solid[0];
        public PlacementV2Activity Activity;
        public PlacementV2Prop[] Props = new PlacementV2Prop[0];
        public PlacementV2Def[] Defs = new PlacementV2Def[0];
        public PlacementV2Motion[] Motions = new PlacementV2Motion[0];
        public PlacementV2Path[] Paths = new PlacementV2Path[0];

        /// <summary>Counts of the side tables the importer does not consume (editor-side data).</summary>
        public int CameraCount;
        public int RenderPresetCount;
        public int RegionCount;

        public readonly List<string> Warnings = new List<string>();

        public bool IsReference
        {
            get
            {
                if (Meta != null &&
                    string.Equals(Meta.SourceKind, "reference", StringComparison.Ordinal))
                {
                    return true;
                }

                return Meta != null && Meta.Id != null &&
                       Meta.Id.StartsWith("_ref_", StringComparison.Ordinal);
            }
        }
    }

    public sealed class PlacementV2Meta
    {
        public int AreaNo = -1;
        public string Id;
        public string Scene;
        public string Units;
        public string Convention;
        public PlacementV2Ball Ball;
        /// <summary>Seconds; 0 means "not declared" (an experiment has no clock).</summary>
        public float TimeLimitS;
        public bool HasTimeLimit;
        public float[] PlayerSpawn;
        /// <summary>
        /// The VRChat descriptor spawn (<c>meta.playerStart</c>); null when the level declares
        /// none and the importer keeps the <c>playerSpawn</c> marker only.
        /// </summary>
        public PlacementV2PlayerStart PlayerStart;
        /// <summary>The fallback floor. Absent and a declared 0 are the same thing.</summary>
        public float GroundY;
        public bool HasGroundY;
        /// <summary><c>[[minX,minZ],[maxX,maxZ]]</c>; empty when absent.</summary>
        public float[][] Bounds = new float[0][];
        public float SpawnClearanceM;
        public PlacementV2Zone[] Zones = new PlacementV2Zone[0];
        public PlacementV2Arena Arena;
        public PlacementV2Caps Caps;
        public PlacementV2Budget Budget;
        public PlacementV2Expected Expected;
        public string SourceKind;
        public string Provenance;
        public string Licence;
        public string MigrationFrom;
        public bool HasMigration;
    }

    /// <summary>
    /// <c>meta.playerStart</c>: where the player stands when the world starts and which way
    /// they face. <c>Position</c> is the FEET point (world metres); <c>FacingDeg</c> is the yaw
    /// in degrees with heading 0 = +Z (<c>Quaternion.Euler(0, FacingDeg, 0)</c>).
    /// </summary>
    public sealed class PlacementV2PlayerStart
    {
        public float[] Position;
        public float FacingDeg;
        public string Source;
    }

    public sealed class PlacementV2Ball
    {
        public float StartDiameterM;
        public float TargetDiameterM;
        public bool HasTargetDiameter;
        public float[] Start;
        public string SpawnSource;
    }

    public sealed class PlacementV2Zone
    {
        public string Id;
        public int Code;
        public string Kind;
    }

    public sealed class PlacementV2Arena
    {
        public string Id;
        public string Notes;
    }

    /// <summary>Data-size ceilings (§12). <c>-1</c> means "not declared".</summary>
    public sealed class PlacementV2Caps
    {
        public int Props = -1;
        public int TerrainTriangles = -1;
        public int TerrainVertices = -1;
        public int StaticSolids = -1;
        public int ActivityWalls = -1;
        public int MovingProps = -1;
        public int Animators = -1;
    }

    /// <summary>The motor's v1 registration budget. <c>-1</c> means "not declared".</summary>
    public sealed class PlacementV2Budget
    {
        public int Planes = -1;
        public int Boxes = -1;
        public int Ramps = -1;
        public int Blocking = -1;
        public int PropSupports = -1;
    }

    /// <summary>
    /// The author's declared totals (§2.2 <c>expected</c>), used as a design gate: the
    /// importer compares them with what it actually read and refuses a mismatch instead of
    /// guessing which side is stale. <c>-1</c> = the key was not written.
    /// </summary>
    public sealed class PlacementV2Expected
    {
        public int PropCount = -1;
        public long GrowthMm3 = -1L;
        public int StructureCount = -1;
        public int StaticObbs = -1;
        public int TerrainTriangles = -1;
        public long ValueUnits = -1L;
        public int OverlapBudget = -1;
    }

    public sealed class PlacementV2TerrainRef
    {
        public string Id;
        public string Kind;
        public string Winding;
        public bool HasMeshRef;
        public int TriangleCount = -1;
        public int BroadPhaseBoxCount;
        public int ChunkCount;
        /// <summary>
        /// <c>terrain.visual</c> (LEVEL_FORMAT_V2 §3.8) is present: the rendered surface is a
        /// separate asset from the contact mesh, and <see cref="TerrainMeshImporter"/> imports
        /// it (the contact mesh then carries only the player's <c>MeshCollider</c>).
        /// </summary>
        public bool HasVisual;
        public int VisualPartCount;
        public int VisualMaterialCount;
    }

    /// <summary>One <c>structures.solids[]</c> entry: a large, never-absorbable analytic body.</summary>
    public sealed class PlacementV2Solid
    {
        public int HitOnAreaNo = -1;
        public string Id;
        public string Kind;
        /// <summary><c>box</c> / <c>ramp</c> / <c>curve_ramp</c>.</summary>
        public string Role;
        /// <summary>
        /// <c>box</c> / <c>proxy</c> / <c>plane</c> (a legacy boundary only). <c>proxy</c> means
        /// "the contact shape is <see cref="Collider"/>, the support face is <see cref="Size"/>".
        /// </summary>
        public string Shape;
        public float[] Center;
        /// <summary>
        /// The AABB extents. For <c>shape:"proxy"</c> this is the SUPPORT face only (§4: the
        /// ball stands on this box's top, because a fitted convex hull's top can be a spike),
        /// not the contact shape.
        /// </summary>
        public float[] Size;
        public PlacementV2Curve Curve;
        public float[] RotationQuat;
        /// <summary><c>box</c> / <c>none</c>.</summary>
        public string Collision;
        public bool Absorbable;
        public bool Support;
        public string Meets;
        public string Material;
        public string Notes;
        /// <summary>
        /// The contact proxy of a <c>shape:"proxy"</c> solid: local to <see cref="Center"/> +
        /// <see cref="RotationQuat"/>, encoded exactly like <c>defs[].collider</c>
        /// (box/sphere/capsule/convex). Null for every other shape - and an empty block with no
        /// <c>type</c> is not a declared proxy either (presence by content).
        /// </summary>
        public PlacementV2Collider Collider;
        /// <summary>
        /// <c>def</c> / <c>aabb-fallback</c> / <c>manual</c>: where the proxy came from. Required
        /// with <c>shape:"proxy"</c>, because an AABB fallback must be visible in the data
        /// (§9 <c>[structure_proxy]</c>).
        /// </summary>
        public string ProxySource;
        /// <summary>
        /// Optional <c>parts</c> of a <c>shape:"proxy"</c> solid: 2..4 boxes, local to
        /// <see cref="Center"/> + <see cref="RotationQuat"/>, that the static table receives
        /// INSTEAD of one box around <see cref="Collider"/> (§4). Null when absent: the solid
        /// is then its one contact box.
        /// </summary>
        public PlacementV2SolidPart[] Parts;
    }

    /// <summary>One <c>structures.solids[].parts[]</c> box (local centre + full extents).</summary>
    public sealed class PlacementV2SolidPart
    {
        public float[] Center;
        public float[] Size;
    }

    /// <summary>The <c>curve</c> block of a <c>role=curve_ramp</c> solid (same shape as v1).</summary>
    public sealed class PlacementV2Curve
    {
        public float RadiusM;
        public float ArcDeg;
        public int Segments;
        public string Bend;
        public float WidthM;
        public float ThicknessM;
    }

    public sealed class PlacementV2Activity
    {
        public PlacementV2Wall[] Walls = new PlacementV2Wall[0];
        public PlacementV2KillPlane KillPlane;
        public float SoftMarginM;
        public string Source;
    }

    public sealed class PlacementV2Wall
    {
        public string Id;
        /// <summary>XZ polyline; <c>[i][0]</c> is x, <c>[i][1]</c> is z.</summary>
        public float[][] Points = new float[0][];
        public bool Closed;
        public float HeightM;
        public float ThicknessM;
        public bool HasMinBallDiameter;
        public float MinBallDiameterM;
        /// <summary><c>visible</c> / <c>invisible</c>.</summary>
        public string Kind;
        public string Material;
        public string Notes;
    }

    public sealed class PlacementV2KillPlane
    {
        public float Y;
        /// <summary><c>"ballStart"</c> or null when the block gives explicit coordinates.</summary>
        public string Respawn;
        /// <summary>Explicit respawn point when <see cref="Respawn"/> is null; else null.</summary>
        public float[] RespawnPoint;
        public int ZoneId;
        public bool HasZoneId;
    }

    /// <summary>
    /// One <c>props[]</c> entry. <c>Aabb</c> is ALREADY multiplied by <c>Scale</c> (§6.2/§18),
    /// which is why the importer multiplies the scale into the Transform and the collider proxy
    /// only - never into the box, never a second time.
    /// </summary>
    public sealed class PlacementV2Prop
    {
        public int HitOnAreaNo = -1;
        public string ItemId;
        public string Def;
        public string Kind;
        /// <summary><c>static</c> / <c>motion</c> / <c>attached</c> / <c>ambient</c> / <c>stacked</c>.</summary>
        public string Behaviour;
        public string Motion;
        public string Inherit;
        public string Zone;
        public float[] Position;
        public float[] RotationQuat;
        /// <summary>Null when the key is absent (default <c>[1,1,1]</c>).</summary>
        public float[] Scale;
        public float[] Aabb;
        public float VolumeK;
        public float VolumePenalty;
        /// <summary>The AUTHORITY for a prop's growth (owner ruling 2026-11-16, B).</summary>
        public long GrowthMm3;
        /// <summary>The legacy alias, read only when <see cref="GrowthMm3"/> is absent.</summary>
        public long VolumeMm3;
        public int AbsorbDiameterReqMm;
        public bool Absorbable;
        public long ValueUnits;
        public float Phase;
        public int ClipStart = -1;
        public bool ShakeOff;
        /// <summary>
        /// The extractor's spawn-record index (<c>instanceIndex</c>), 0-based and unique within
        /// the document. Preserved as provenance: the importer's identity is still
        /// <c>itemId</c>, but this is the number a report about "instance #411" means.
        /// </summary>
        public int InstanceIndex = -1;
        public bool HasInstanceIndex;
        public string Parent;
        public bool HasParent;
        public string RigidGroup;
        public string Support;
        public string StackedOn;
        public bool FloatingOk;
        public string Notes;

        public bool HasScale
        {
            get { return Scale != null && Scale.Length == 3; }
        }

        public bool HasGrowthMm3;
        public bool HasVolumeMm3;
        public bool HasVolumePenalty;
        public bool HasVolumeK;
        public bool HasAbsorbable;
        public bool HasPhase;
        public bool HasClipStart;

        /// <summary>The behaviour a rule should use: absent means <c>static</c>.</summary>
        public string EffectiveBehaviour
        {
            get { return string.IsNullOrEmpty(Behaviour) ? "static" : Behaviour; }
        }

        public bool IsStatic
        {
            get { return string.Equals(EffectiveBehaviour, "static", StringComparison.Ordinal); }
        }
    }

    /// <summary>One <c>defs[]</c> entry.</summary>
    public sealed class PlacementV2Def
    {
        public string Id;
        public string Kind;
        public string Model;
        public long ValueUnits;
        public float VolumeK;
        public float VolumePenalty;
        /// <summary>Null when the block is absent OR carries no <c>type</c> (presence by content).</summary>
        public PlacementV2Collider Collider;
        public string ScalePolicy;
        public int SpeedIndex = -1;
        public int ClipCount;
        public string Notes;
    }

    /// <summary>The per-def collider proxy (COLLIDER_PROXY.md §2 shape, unchanged in v2).</summary>
    public sealed class PlacementV2Collider
    {
        public string Type;
        public float[] Center;
        public float[] Size;
        public float Radius;
        public float HalfHeight;
        public string Axis;
        /// <summary>Flat <c>3N</c> hull (no jagged arrays), like the v1 DTO.</summary>
        public float[] Hull;
        public string Source;
        public float FitError;
    }

    /// <summary>
    /// One <c>motions[]</c> entry: a per-level motion program referenced by
    /// <c>props[].motion</c> (§6.7.2).
    /// </summary>
    /// <remarks>
    /// Consumed by <c>PlacementMotionRows</c>, which resolves it into <c>MotionHost</c>'s
    /// columns. The model keeps the documented keys plus the <c>params</c> keys and values, and
    /// the reader records unknown keys as warnings rather than errors - a non-empty table must
    /// never make a level fail to import.
    /// </remarks>
    public sealed class PlacementV2Motion
    {
        public string Id;
        public string Class;
        public string SpeedSource;
        public float SpeedCmPerSec;
        public bool HasSpeedCmPerSec;
        public string Path;
        public bool Loop;
        public float UpdatesPerSecond;
        public int ParamCount;
        public string[] ParamKeys = new string[0];
        /// <summary>The numeric values of <see cref="ParamKeys"/>, same order (non-numbers read 0).</summary>
        public float[] ParamValues = new float[0];
        public string ProvenanceMoveType;
        public string ProvenanceAction;
        public int ProvenanceBackupIndex = -1;
        public int ProvenanceMotionId = -1;
        public int ProvenancePathIndex = -1;
        public string Notes;
    }

    /// <summary>One <c>paths[]</c> entry (§6.7.4).</summary>
    public sealed class PlacementV2Path
    {
        public string Id;
        public float[][] Points = new float[0][];
        public bool Loop;
        /// <summary>The raw <c>loop</c> text (<c>loop</c>/<c>ping_pong</c>/<c>once</c>), null when absent.</summary>
        public string LoopMode;
        public float SpeedCmPerSec;
        public bool HasSpeedCmPerSec;
        public string Notes;
    }

    /// <summary>What one <see cref="PlacementV2Reader.Read"/> produced.</summary>
    public sealed class PlacementV2ReadResult
    {
        public PlacementV2Document Document;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Wall-clock parse time, for the operator's "how long does this take" line.</summary>
        public double ParseMilliseconds;

        public bool Ok
        {
            get { return Errors.Count == 0 && Document != null; }
        }
    }

    /// <summary>
    /// Reads a <c>katamari.placement/2</c> document into <see cref="PlacementV2Document"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never throws for data problems: a malformed file, a missing block and a wrong schema all
    /// become entries in <see cref="PlacementV2ReadResult.Errors"/> with the §9 tag, because
    /// the importer must report and stop, not leave a half-changed scene behind an exception.
    /// The one exception it lets escape is an out-of-memory style failure of
    /// <see cref="File.ReadAllText(string)"/>, which the caller's own try/catch reports.
    /// </para>
    /// <para>
    /// The reader is lenient on purpose in two places, and both are visible in the result
    /// rather than silent: unknown keys (a <c>[v2_key]</c> warning with the dotted path) and
    /// an undeclared <c>props[].scale</c>/<c>parent</c>/<c>behaviour</c> (defaults, with the
    /// presence flags false so a rule can tell "absent" from "written"). It is strict about
    /// everything the schema calls required: the schema string, the units/convention pair,
    /// <c>meta.id</c>/<c>scene</c>, the ball's start diameter, and a <c>props</c> array.
    /// </para>
    /// </remarks>
    public static class PlacementV2Reader
    {
        public const string SchemaName = "katamari.placement/2";

        /// <summary>The document's own keys (§1.2's five layers plus the side tables).</summary>
        private static readonly string[] DocumentKeys =
        {
            "schema", "meta", "terrain", "structures", "activityArea", "props", "defs",
            "motions", "paths", "cameras", "renderPresets", "regions"
        };

        /// <summary>Keys the reader knows per block; anything else is a <c>[v2_key]</c> warning.</summary>
        private static readonly string[] MetaKeys =
        {
            "id", "scene", "units", "convention", "ball", "timeLimitS", "playerSpawn",
            "playerStart", "groundY", "bounds", "spawnClearanceM", "zones", "arena", "caps", "budget",
            "expected", "source", "migration", "areaNo"
        };

        private static readonly string[] PlayerStartKeys = { "position", "facingDeg", "source" };

        // The maxDiameter keys are only accepted for old documents; no settings are read.
        private static readonly string[] BallKeys =
        {
            "startDiameterM", "targetDiameterM", "maxDiameterRef", "maxDiameterM", "start",
            "spawnSource"
        };

        private static readonly string[] CapsKeys =
        {
            "props", "terrainTriangles", "terrainVertices", "staticSolids", "activityWalls",
            "movingProps", "animators"
        };

        private static readonly string[] BudgetKeys =
        {
            "planes", "boxes", "ramps", "blocking", "propSupports"
        };

        // The design's EXPECTED_KEYS plus the three author-side extras the reference writer
        // emits (see the class remarks): a document we do not own must still open.
        private static readonly string[] ExpectedKeys =
        {
            "propCount", "valueUnits", "terrainTriangles", "growthMm3", "structureCount",
            "staticObbs", "overlapBudget"
        };

        private static readonly string[] SolidKeys =
        {
            "id", "kind", "role", "shape", "center", "size", "curve", "rotationQuat",
            "collision", "absorbable", "support", "meets", "material", "notes", "collider",
            "proxySource", "parts", "hitOnAreaNo"
        };

        private static readonly string[] CurveKeys =
        {
            "radius_m", "arc_deg", "segments", "bend", "width_m", "thickness_m"
        };

        private static readonly string[] ActivityKeys =
        {
            "walls", "killPlane", "softMarginM", "source"
        };

        private static readonly string[] WallKeys =
        {
            "id", "points", "closed", "heightM", "thicknessM", "minBallDiameterM", "kind",
            "material", "notes"
        };

        private static readonly string[] KillKeys = { "y", "respawn", "zoneId" };

        // PROP_KEYS_V2 plus `growthMm3` and `volumePenalty`: the owner ruling of 2026-11-16 (B)
        // makes growthMm3 the authority and keeps the penalty as a declared column, so both are
        // legal v2 keys even though the design's whitelist has not caught up yet.
        private static readonly string[] PropKeys =
        {
            "itemId", "def", "kind", "behaviour", "motion", "inherit", "phase", "clipStart",
            "shakeOff", "zone", "position", "rotationQuat", "scale", "aabb", "volumeK",
            "growthMm3", "volumePenalty", "absorbDiameterReqMm", "absorbable", "valueUnits",
            "parent", "rigidGroup", "support", "stackedOn", "floatingOk", "notes",
            "instanceIndex", "hitOnAreaNo"
        };

        private static readonly string[] DefKeys =
        {
            "id", "kind", "model", "valueUnits", "volumeK", "volumePenalty", "collider",
            "scalePolicy", "speedIndex", "clips", "notes"
        };

        private static readonly string[] ColliderKeys =
        {
            "type", "center", "size", "radius", "halfHeight", "axis", "hull", "source",
            "fit_error"
        };

        private static readonly string[] ClipKeys = { "id", "asset", "fps", "lengthSec" };

        private static readonly string[] MotionKeys =
        {
            "id", "class", "speed", "path", "params", "updatesPerSecond", "loop",
            "provenance", "notes"
        };

        private static readonly string[] MotionSpeedKeys = { "source", "cmPerSec" };

        private static readonly string[] MotionProvenanceKeys =
        {
            "moveType", "motionID", "action", "backupIndex", "pathIndex"
        };

        private static readonly string[] PathKeys =
        {
            "id", "points", "loop", "speedCmPerSec", "notes"
        };

        /// <summary>
        /// Cheap flavour test used before a full parse: the schema string appears in the text.
        /// </summary>
        /// <remarks>
        /// A substring test can be fooled by a v1 document that mentions v2 in a comment-ish
        /// string; the importer therefore confirms with the parsed <c>schema</c> value and
        /// falls back to the v1 path when the parse says otherwise. The point of this helper is
        /// only to choose which reader to instantiate, so it must not depend on the parse.
        /// </remarks>
        public static bool LooksLikeV2(string json)
        {
            return !string.IsNullOrEmpty(json) &&
                   json.IndexOf(SchemaName, StringComparison.Ordinal) >= 0;
        }

        /// <summary>Reads a file; a missing/unreadable file is a result error, not an exception.</summary>
        public static PlacementV2ReadResult ReadFile(string path)
        {
            var result = new PlacementV2ReadResult();
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception exception)
            {
                result.Errors.Add("[v2_source] cannot read " + path + ": " + exception.Message);
                return result;
            }

            var watch = Stopwatch.StartNew();
            PlacementV2ReadResult parsed = Read(text);
            watch.Stop();
            parsed.ParseMilliseconds = watch.Elapsed.TotalMilliseconds;
            return parsed;
        }

        public static PlacementV2ReadResult Read(string json)
        {
            var result = new PlacementV2ReadResult();
            LevelJsonNode root;
            try
            {
                root = LevelJsonParser.ParseText(json);
            }
            catch (Exception exception)
            {
                result.Errors.Add("[v2_source] not valid JSON: " + exception.Message);
                return result;
            }

            if (root == null || root.Kind != LevelJsonKind.Object)
            {
                result.Errors.Add("[v2_source] the document is not a JSON object");
                return result;
            }

            var document = new PlacementV2Document();
            result.Document = document;
            document.Schema = root.GetString("schema");

            if (!string.Equals(document.Schema, SchemaName, StringComparison.Ordinal))
            {
                result.Errors.Add("[v2_schema] schema is '" + document.Schema + "', expected '" +
                                  SchemaName + "'");
            }

            ReportUnknown(root, "", DocumentKeys, result, document);
            document.Meta = ReadMeta(root.Get("meta"), result, document);
            document.Terrain = ReadTerrainRef(root.Get("terrain"), result, document);
            document.Solids = ReadSolids(root.Get("structures"), result, document);
            document.Activity = ReadActivity(root.Get("activityArea"), result, document);
            document.Props = ReadProps(root.Get("props"), result, document);
            document.Defs = ReadDefs(root.Get("defs"), result, document);
            document.Motions = ReadMotions(root.Get("motions"), result, document);
            document.Paths = ReadPaths(root.Get("paths"), result, document);
            document.CameraCount = ArrayLength(root.Get("cameras"));
            document.RenderPresetCount = ArrayLength(root.Get("renderPresets"));
            document.RegionCount = ArrayLength(root.Get("regions"));

            ValidateDocumentShape(document, result);
            return result;
        }

        // ------------------------------------------------------------------ blocks ---

        private static PlacementV2Meta ReadMeta(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            if (node == null || node.Kind != LevelJsonKind.Object)
            {
                result.Errors.Add("[v2_source] no meta block; a v2 document always has one");
                return null;
            }

            var meta = new PlacementV2Meta();
            ReportUnknown(node, "meta", MetaKeys, result, document);
            meta.Id = node.GetString("id");
            meta.AreaNo = ReadAreaNo(node, "areaNo", "meta", 0, result);
            meta.Scene = node.GetString("scene");
            meta.Units = node.GetString("units");
            meta.Convention = node.GetString("convention");
            meta.Ball = ReadBall(node.Get("ball"), result, document);
            meta.HasTimeLimit = node.Has("timeLimitS");
            meta.TimeLimitS = node.GetFloat("timeLimitS", 0f);
            meta.PlayerSpawn = ReadVector(node, "playerSpawn", 3);
            meta.PlayerStart = ReadPlayerStart(node.Get("playerStart"), result, document);
            meta.HasGroundY = node.Has("groundY");
            meta.GroundY = node.GetFloat("groundY", 0f);
            meta.SpawnClearanceM = node.GetFloat("spawnClearanceM", 0f);
            meta.Zones = ReadZones(node.Get("zones"), result, document);
            meta.Arena = ReadArena(node.Get("arena"), result, document);
            meta.Caps = ReadCaps(node.Get("caps"), result, document);
            meta.Budget = ReadBudget(node.Get("budget"), result, document);
            meta.Expected = ReadExpected(node.Get("expected"), result, document);
            LevelJsonNode source = node.Get("source");
            if (source != null && source.Kind == LevelJsonKind.Object)
            {
                ReportUnknown(source, "meta.source",
                    new[] { "kind", "provenance", "windingFix", "licence" }, result, document);
                meta.SourceKind = source.GetString("kind");
                meta.Provenance = source.GetString("provenance");
                meta.Licence = source.GetString("licence");
            }

            LevelJsonNode migration = node.Get("migration");
            if (migration != null && migration.Kind == LevelJsonKind.Object)
            {
                meta.HasMigration = true;
                ReportUnknown(migration, "meta.migration",
                    new[] { "from", "maxDiameterM", "volumePenalty" }, result, document);
                meta.MigrationFrom = migration.GetString("from");
            }

            float[][] bounds;
            if (node.Has("bounds"))
            {
                if (LevelJsonValues.TryNumberGroups(node.Get("bounds"), 2, out bounds))
                {
                    meta.Bounds = bounds;
                }
                else
                {
                    result.Errors.Add("[meta_bounds] bounds must be [[minX,minZ],[maxX,maxZ]]");
                }
            }

            return meta;
        }

        private static PlacementV2PlayerStart ReadPlayerStart(LevelJsonNode node,
            PlacementV2ReadResult result, PlacementV2Document document)
        {
            if (node == null) return null;
            if (node.Kind != LevelJsonKind.Object)
            {
                result.Errors.Add("[meta_player_start] meta.playerStart must be an object " +
                                  "{position, facingDeg}");
                return null;
            }

            ReportUnknown(node, "meta.playerStart", PlayerStartKeys, result, document);
            float[] position = ReadVector(node, "position", 3);
            if (position == null)
            {
                result.Errors.Add("[meta_player_start] meta.playerStart.position must be 3 numbers " +
                                  "(the feet point, world metres)");
                return null;
            }

            LevelJsonNode facing = node.Get("facingDeg");
            if (facing == null || facing.Kind != LevelJsonKind.Number ||
                !PlacementV2Rules.IsFacingDegValid(facing.AsFloat()))
            {
                result.Errors.Add("[meta_player_start] meta.playerStart.facingDeg must be a number " +
                                  "in -360..360 (yaw, heading 0 = +Z)");
                return null;
            }

            return new PlacementV2PlayerStart
            {
                Position = position,
                FacingDeg = facing.AsFloat(),
                Source = node.GetString("source")
            };
        }

        private static PlacementV2Ball ReadBall(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            if (node == null || node.Kind != LevelJsonKind.Object)
            {
                result.Errors.Add("[meta_start_diameter] meta.ball is missing");
                return null;
            }

            ReportUnknown(node, "meta.ball", BallKeys, result, document);
            var ball = new PlacementV2Ball();
            ball.StartDiameterM = node.GetFloat("startDiameterM", 0f);
            ball.HasTargetDiameter = node.Has("targetDiameterM");
            ball.TargetDiameterM = node.GetFloat("targetDiameterM", 0f);
            ball.Start = ReadVector(node, "start", 3);
            ball.SpawnSource = node.GetString("spawnSource");
            return ball;
        }

        private static PlacementV2Zone[] ReadZones(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            if (node == null) return new PlacementV2Zone[0];
            if (node.Kind != LevelJsonKind.Array)
            {
                result.Errors.Add("[meta_zone_code] zones must be an array");
                return new PlacementV2Zone[0];
            }

            var zones = new PlacementV2Zone[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Object)
                {
                    result.Errors.Add("zones[" + i + "] is not an object");
                    continue;
                }

                ReportUnknown(item, "zones[" + i + "]", new[] { "id", "code", "kind" }, result,
                    document);
                zones[i] = new PlacementV2Zone
                {
                    Id = item.GetString("id"),
                    Code = item.GetInt("code", 0),
                    Kind = item.GetString("kind")
                };
            }

            return zones;
        }

        private static PlacementV2Arena ReadArena(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            if (node == null || node.Kind != LevelJsonKind.Object) return null;
            ReportUnknown(node, "meta.arena", new[] { "id", "notes" }, result, document);
            return new PlacementV2Arena { Id = node.GetString("id"), Notes = node.GetString("notes") };
        }

        private static PlacementV2Caps ReadCaps(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            var caps = new PlacementV2Caps();
            if (node == null || node.Kind != LevelJsonKind.Object) return caps;
            ReportUnknown(node, "meta.caps", CapsKeys, result, document);
            caps.Props = node.GetInt("props", -1);
            caps.TerrainTriangles = node.GetInt("terrainTriangles", -1);
            caps.TerrainVertices = node.GetInt("terrainVertices", -1);
            caps.StaticSolids = node.GetInt("staticSolids", -1);
            caps.ActivityWalls = node.GetInt("activityWalls", -1);
            caps.MovingProps = node.GetInt("movingProps", -1);
            caps.Animators = node.GetInt("animators", -1);
            return caps;
        }

        private static PlacementV2Budget ReadBudget(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            var budget = new PlacementV2Budget();
            if (node == null || node.Kind != LevelJsonKind.Object) return budget;
            ReportUnknown(node, "meta.budget", BudgetKeys, result, document);
            budget.Planes = node.GetInt("planes", -1);
            budget.Boxes = node.GetInt("boxes", -1);
            budget.Ramps = node.GetInt("ramps", -1);
            budget.Blocking = node.GetInt("blocking", -1);
            budget.PropSupports = node.GetInt("propSupports", -1);
            return budget;
        }

        private static PlacementV2Expected ReadExpected(LevelJsonNode node,
            PlacementV2ReadResult result, PlacementV2Document document)
        {
            var expected = new PlacementV2Expected();
            if (node == null || node.Kind != LevelJsonKind.Object) return expected;
            ReportUnknown(node, "meta.expected", ExpectedKeys, result, document);
            expected.PropCount = node.GetInt("propCount", -1);
            expected.GrowthMm3 = node.GetLong("growthMm3", -1L);
            expected.StructureCount = node.GetInt("structureCount", -1);
            expected.StaticObbs = node.GetInt("staticObbs", -1);
            expected.TerrainTriangles = node.GetInt("terrainTriangles", -1);
            expected.ValueUnits = node.GetLong("valueUnits", -1L);
            expected.OverlapBudget = node.GetInt("overlapBudget", -1);
            return expected;
        }

        private static readonly string[] TerrainKeys =
        {
            "id", "kind", "winding", "vertices", "indices", "meshRef", "triangles", "broadPhase",
            "chunks", "grid", "materialSegments", "visual", "source"
        };

        private static readonly string[] VisualKeys =
            { "meshRef", "parts", "materials", "filter", "source" };

        private static readonly string[] VisualMeshRefKeys =
        {
            "path", "sha256", "vertexCount", "indexCount", "triangleCount", "indexWidth",
            "normals", "uvs"
        };

        private static readonly string[] VisualPartKeys =
            { "id", "role", "vertexRange", "indexRange", "bounds", "submeshes" };

        private static readonly string[] VisualMaterialKeys =
            { "id", "texture", "sha256", "width", "height", "blend" };

        private static PlacementV2TerrainRef ReadTerrainRef(LevelJsonNode node,
            PlacementV2ReadResult result, PlacementV2Document document)
        {
            if (node == null || node.Kind != LevelJsonKind.Object) return null;
            ReportUnknown(node, "terrain", TerrainKeys, result, document);
            var terrain = new PlacementV2TerrainRef();
            terrain.Id = node.GetString("id");
            terrain.Kind = node.GetString("kind");
            terrain.Winding = node.GetString("winding");
            LevelJsonNode meshRef = node.Get("meshRef");
            terrain.HasMeshRef = meshRef != null && meshRef.Kind == LevelJsonKind.Object;
            LevelJsonNode triangles = node.Get("triangles");
            if (triangles != null && triangles.Kind == LevelJsonKind.Object)
            {
                terrain.TriangleCount = triangles.GetInt("count", -1);
            }

            LevelJsonNode broad = node.Get("broadPhase");
            if (broad != null && broad.Kind == LevelJsonKind.Object)
            {
                terrain.BroadPhaseBoxCount = ArrayLength(broad.Get("boxes"));
            }

            terrain.ChunkCount = ArrayLength(node.Get("chunks"));
            ReadVisualRef(node.Get("visual"), terrain, result, document);
            return terrain;
        }

        /// <summary>
        /// <c>terrain.visual</c>: only its shape is checked here (unknown keys, counts); the
        /// geometry, the textures and the sidecar are read - and verified - by
        /// <see cref="TerrainVisualDocument"/> when <see cref="TerrainMeshImporter"/> imports.
        /// </summary>
        private static void ReadVisualRef(LevelJsonNode visual, PlacementV2TerrainRef terrain,
            PlacementV2ReadResult result, PlacementV2Document document)
        {
            if (visual == null) return;
            if (visual.Kind != LevelJsonKind.Object)
            {
                result.Errors.Add("[terrain_visual] terrain.visual must be an object");
                return;
            }

            terrain.HasVisual = true;
            ReportUnknown(visual, "terrain.visual", VisualKeys, result, document);
            ReportUnknown(visual.Get("meshRef"), "terrain.visual.meshRef", VisualMeshRefKeys,
                result, document);
            LevelJsonNode parts = visual.Get("parts");
            terrain.VisualPartCount = ArrayLength(parts);
            for (int i = 0; parts != null && parts.Kind == LevelJsonKind.Array && i < parts.Count;
                 i++)
            {
                ReportUnknown(parts.At(i), "terrain.visual.parts[" + i + "]", VisualPartKeys,
                    result, document);
            }

            LevelJsonNode materials = visual.Get("materials");
            terrain.VisualMaterialCount = ArrayLength(materials);
            for (int i = 0; materials != null && materials.Kind == LevelJsonKind.Array &&
                            i < materials.Count; i++)
            {
                ReportUnknown(materials.At(i), "terrain.visual.materials[" + i + "]",
                    VisualMaterialKeys, result, document);
            }
        }

        private static PlacementV2Solid[] ReadSolids(LevelJsonNode structures,
            PlacementV2ReadResult result, PlacementV2Document document)
        {
            if (structures == null) return new PlacementV2Solid[0];
            if (structures.Kind != LevelJsonKind.Object)
            {
                result.Errors.Add("[structure_role] structures must be an object with solids[]");
                return new PlacementV2Solid[0];
            }

            ReportUnknown(structures, "structures", new[] { "solids" }, result, document);
            LevelJsonNode node = structures.Get("solids");
            if (node == null) return new PlacementV2Solid[0];
            if (node.Kind != LevelJsonKind.Array)
            {
                result.Errors.Add("[structure_role] structures.solids must be an array");
                return new PlacementV2Solid[0];
            }

            var solids = new PlacementV2Solid[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Object)
                {
                    result.Errors.Add("structures.solids[" + i + "] is not an object");
                    continue;
                }

                ReportUnknown(item, "structures.solids[" + i + "]", SolidKeys, result, document);
                var solid = new PlacementV2Solid
                {
                    HitOnAreaNo = ReadAreaNo(item, "hitOnAreaNo", "structures.solids[" + i + "]", -1, result),
                    Id = item.GetString("id"),
                    Kind = item.GetString("kind"),
                    Role = item.GetString("role"),
                    Shape = item.GetString("shape"),
                    Center = ReadVector(item, "center", 3),
                    Size = ReadVector(item, "size", 3),
                    RotationQuat = ReadVector(item, "rotationQuat", 4),
                    Collision = item.GetString("collision"),
                    Absorbable = item.GetBool("absorbable", false),
                    Support = item.GetBool("support", false),
                    Meets = item.GetString("meets"),
                    Material = item.GetString("material"),
                    Notes = item.GetString("notes")
                };
                solid.Curve = ReadCurve(item.Get("curve"), result, document, i);
                solid.ProxySource = item.GetString("proxySource");
                solid.Collider = ReadCollider(item.Get("collider"), result, document,
                    "structures.solids[" + i + "].collider");
                solid.Parts = ReadSolidParts(item.Get("parts"), result, document,
                    "structures.solids[" + i + "].parts");
                solids[i] = solid;
            }

            return solids;
        }

        private static readonly string[] SolidPartKeys = { "center", "size" };

        private static PlacementV2SolidPart[] ReadSolidParts(LevelJsonNode node,
            PlacementV2ReadResult result, PlacementV2Document document, string path)
        {
            if (node == null) return null;
            if (node.Kind != LevelJsonKind.Array)
            {
                result.Errors.Add("[structure_proxy] " + path + " must be an array of {center, size}");
                return null;
            }

            var parts = new PlacementV2SolidPart[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Object)
                {
                    result.Errors.Add("[structure_proxy] " + path + "[" + i + "] is not an object");
                    continue;
                }

                ReportUnknown(item, path + "[" + i + "]", SolidPartKeys, result, document);
                parts[i] = new PlacementV2SolidPart
                {
                    Center = ReadVector(item, "center", 3),
                    Size = ReadVector(item, "size", 3)
                };
            }

            return parts;
        }

        private static PlacementV2Curve ReadCurve(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document, int solidIndex)
        {
            if (node == null || node.Kind != LevelJsonKind.Object) return null;
            ReportUnknown(node, "structures.solids[" + solidIndex + "].curve", CurveKeys, result,
                document);
            return new PlacementV2Curve
            {
                RadiusM = node.GetFloat("radius_m", 0f),
                ArcDeg = node.GetFloat("arc_deg", 0f),
                Segments = node.GetInt("segments", 0),
                Bend = node.GetString("bend"),
                WidthM = node.GetFloat("width_m", 0f),
                ThicknessM = node.GetFloat("thickness_m", 0f)
            };
        }

        private static PlacementV2Activity ReadActivity(LevelJsonNode node,
            PlacementV2ReadResult result, PlacementV2Document document)
        {
            if (node == null || node.Kind != LevelJsonKind.Object) return null;
            ReportUnknown(node, "activityArea", ActivityKeys, result, document);
            var activity = new PlacementV2Activity
            {
                SoftMarginM = node.GetFloat("softMarginM", 0f),
                Source = node.GetString("source"),
                Walls = ReadWalls(node.Get("walls"), result, document),
                KillPlane = ReadKillPlane(node.Get("killPlane"), result, document)
            };
            return activity;
        }

        private static PlacementV2Wall[] ReadWalls(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            if (node == null) return new PlacementV2Wall[0];
            if (node.Kind != LevelJsonKind.Array)
            {
                result.Errors.Add("[activity_wall] activityArea.walls must be an array");
                return new PlacementV2Wall[0];
            }

            var walls = new PlacementV2Wall[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Object)
                {
                    result.Errors.Add("activityArea.walls[" + i + "] is not an object");
                    continue;
                }

                ReportUnknown(item, "activityArea.walls[" + i + "]", WallKeys, result, document);
                var wall = new PlacementV2Wall
                {
                    Id = item.GetString("id"),
                    Closed = item.GetBool("closed", false),
                    HeightM = item.GetFloat("heightM", 0f),
                    ThicknessM = item.GetFloat("thicknessM", 0f),
                    HasMinBallDiameter = item.Has("minBallDiameterM"),
                    MinBallDiameterM = item.GetFloat("minBallDiameterM", 0f),
                    Kind = item.GetString("kind"),
                    Material = item.GetString("material"),
                    Notes = item.GetString("notes")
                };

                float[][] points;
                if (item.Has("points"))
                {
                    if (LevelJsonValues.TryNumberGroups(item.Get("points"), 2, out points))
                    {
                        wall.Points = points;
                    }
                    else
                    {
                        result.Errors.Add("[activity_wall] walls[" + i +
                                          "].points must be [[x,z],...]");
                    }
                }

                walls[i] = wall;
            }

            return walls;
        }

        private static PlacementV2KillPlane ReadKillPlane(LevelJsonNode node,
            PlacementV2ReadResult result, PlacementV2Document document)
        {
            if (node == null) return null;
            if (node.Kind != LevelJsonKind.Object)
            {
                result.Errors.Add("[activity_kill_plane] killPlane must be an object");
                return null;
            }

            ReportUnknown(node, "activityArea.killPlane", KillKeys, result, document);
            var kill = new PlacementV2KillPlane();
            kill.Y = node.GetFloat("y", 0f);
            LevelJsonNode respawn = node.Get("respawn");
            if (respawn != null && respawn.Kind == LevelJsonKind.String)
            {
                kill.Respawn = respawn.StringValue;
            }
            else if (respawn != null && respawn.Kind == LevelJsonKind.Array)
            {
                kill.RespawnPoint = ReadVector(node, "respawn", 3);
                if (kill.RespawnPoint == null)
                {
                    result.Errors.Add("[activity_kill_plane] respawn must be 'ballStart' or [x,y,z]");
                }
            }

            kill.HasZoneId = node.Has("zoneId");
            kill.ZoneId = node.GetInt("zoneId", 0);
            return kill;
        }

        private static PlacementV2Prop[] ReadProps(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            if (node == null)
            {
                result.Errors.Add("[v2_source] no props[]; every v2 document has one");
                return new PlacementV2Prop[0];
            }

            if (node.Kind != LevelJsonKind.Array)
            {
                result.Errors.Add("[v2_source] props must be an array");
                return new PlacementV2Prop[0];
            }

            var props = new PlacementV2Prop[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Object)
                {
                    result.Errors.Add("props[" + i + "] is not an object");
                    continue;
                }

                ReportUnknown(item, "props[" + i + "]", PropKeys, result, document);
                var prop = new PlacementV2Prop();
                prop.HitOnAreaNo = ReadAreaNo(item, "hitOnAreaNo", "props[" + i + "]", -1, result);
                prop.ItemId = item.GetString("itemId");
                prop.Def = item.GetString("def");
                prop.Kind = item.GetString("kind");
                prop.Behaviour = item.GetString("behaviour");
                prop.Motion = item.GetString("motion");
                prop.Inherit = item.GetString("inherit");
                prop.Zone = item.GetString("zone");
                prop.Position = ReadVector(item, "position", 3);
                prop.RotationQuat = ReadVector(item, "rotationQuat", 4);
                prop.Scale = item.Has("scale") ? ReadVector(item, "scale", 3) : null;
                prop.Aabb = ReadVector(item, "aabb", 3);
                prop.HasVolumeK = item.Has("volumeK");
                prop.VolumeK = item.GetFloat("volumeK", 0f);
                prop.HasVolumePenalty = item.Has("volumePenalty");
                prop.VolumePenalty = item.GetFloat("volumePenalty", 0f);
                prop.HasGrowthMm3 = item.Has("growthMm3");
                prop.GrowthMm3 = item.GetLong("growthMm3", 0L);
                prop.HasVolumeMm3 = item.Has("volumeMm3");
                prop.VolumeMm3 = item.GetLong("volumeMm3", 0L);
                prop.AbsorbDiameterReqMm = item.GetInt("absorbDiameterReqMm", 0);
                prop.HasAbsorbable = item.Has("absorbable");
                prop.Absorbable = item.GetBool("absorbable", true);
                prop.ValueUnits = item.GetLong("valueUnits", 0L);
                prop.HasPhase = item.Has("phase");
                prop.Phase = item.GetFloat("phase", 0f);
                prop.HasClipStart = item.Has("clipStart");
                prop.ClipStart = item.GetInt("clipStart", -1);
                prop.ShakeOff = item.GetBool("shakeOff", false);
                prop.HasInstanceIndex = item.Has("instanceIndex");
                prop.InstanceIndex = item.GetInt("instanceIndex", -1);
                prop.HasParent = item.Has("parent") && item.Get("parent") != null &&
                                 item.Get("parent").Kind == LevelJsonKind.String;
                prop.Parent = prop.HasParent ? item.GetString("parent") : null;
                prop.RigidGroup = item.GetString("rigidGroup");
                prop.Support = item.GetString("support");
                prop.StackedOn = item.GetString("stackedOn");
                prop.FloatingOk = item.GetBool("floatingOk", false);
                prop.Notes = item.GetString("notes");
                props[i] = prop;
            }

            return props;
        }

        private static PlacementV2Def[] ReadDefs(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            if (node == null) return new PlacementV2Def[0];
            if (node.Kind != LevelJsonKind.Array)
            {
                result.Errors.Add("[v2_source] defs must be an array");
                return new PlacementV2Def[0];
            }

            var defs = new PlacementV2Def[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Object)
                {
                    result.Errors.Add("defs[" + i + "] is not an object");
                    continue;
                }

                ReportUnknown(item, "defs[" + i + "]", DefKeys, result, document);
                var def = new PlacementV2Def
                {
                    Id = item.GetString("id"),
                    Kind = item.GetString("kind"),
                    Model = item.GetString("model"),
                    ValueUnits = item.GetLong("valueUnits", 0L),
                    VolumeK = item.GetFloat("volumeK", 0f),
                    VolumePenalty = item.GetFloat("volumePenalty", 0f),
                    ScalePolicy = item.GetString("scalePolicy"),
                    SpeedIndex = item.GetInt("speedIndex", -1),
                    Notes = item.GetString("notes")
                };
                LevelJsonNode clips = item.Get("clips");
                def.ClipCount = ArrayLength(clips);
                if (clips != null && clips.Kind == LevelJsonKind.Array)
                {
                    for (int c = 0; c < clips.Count; c++)
                    {
                        ReportUnknown(clips.At(c), "defs[" + i + "].clips[" + c + "]", ClipKeys,
                            result, document);
                    }
                }

                def.Collider = ReadCollider(item.Get("collider"), result, document,
                    "defs[" + i + "].collider");
                defs[i] = def;
            }

            return defs;
        }

        private static PlacementV2Collider ReadCollider(LevelJsonNode node,
            PlacementV2ReadResult result, PlacementV2Document document, string path)
        {
            if (node == null || node.Kind != LevelJsonKind.Object) return null;
            ReportUnknown(node, path, ColliderKeys, result, document);
            // Presence by CONTENT, not by null: an empty block is not a declared proxy. This is
            // the v1 bug (44 "collider.type must be ... (got '')" errors) made structural.
            string type = node.GetString("type");
            if (string.IsNullOrEmpty(type) || type.Trim().Length == 0) return null;

            var collider = new PlacementV2Collider
            {
                Type = type.Trim().ToLowerInvariant(),
                Center = ReadVector(node, "center", 3),
                Size = ReadVector(node, "size", 3),
                Radius = node.GetFloat("radius", 0f),
                HalfHeight = node.GetFloat("halfHeight", 0f),
                Axis = node.GetString("axis"),
                Source = node.GetString("source"),
                FitError = node.GetFloat("fit_error", 0f)
            };

            LevelJsonNode hull = node.Get("hull");
            if (hull != null)
            {
                float[] flat;
                if (LevelJsonValues.TryNumbersFlat(hull, out flat))
                {
                    collider.Hull = flat;
                }
                else
                {
                    result.Errors.Add(path + ".hull must be a flat list of numbers (3N)");
                }
            }

            return collider;
        }

        private static PlacementV2Motion[] ReadMotions(LevelJsonNode node,
            PlacementV2ReadResult result, PlacementV2Document document)
        {
            if (node == null) return new PlacementV2Motion[0];
            if (node.Kind != LevelJsonKind.Array)
            {
                result.Errors.Add("[motion_key] motions must be an array");
                return new PlacementV2Motion[0];
            }

            var motions = new PlacementV2Motion[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Object)
                {
                    result.Errors.Add("motions[" + i + "] is not an object");
                    continue;
                }

                ReportUnknown(item, "motions[" + i + "]", MotionKeys, result, document);
                var motion = new PlacementV2Motion
                {
                    Id = item.GetString("id"),
                    Class = item.GetString("class"),
                    Path = item.GetString("path"),
                    Loop = item.GetBool("loop", false),
                    UpdatesPerSecond = item.GetFloat("updatesPerSecond", 0f),
                    Notes = item.GetString("notes")
                };

                LevelJsonNode speed = item.Get("speed");
                if (speed != null && speed.Kind == LevelJsonKind.Object)
                {
                    ReportUnknown(speed, "motions[" + i + "].speed", MotionSpeedKeys, result,
                        document);
                    motion.SpeedSource = speed.GetString("source");
                    motion.HasSpeedCmPerSec = speed.Has("cmPerSec");
                    motion.SpeedCmPerSec = speed.GetFloat("cmPerSec", 0f);
                }

                LevelJsonNode parameters = item.Get("params");
                if (parameters != null && parameters.Kind == LevelJsonKind.Object)
                {
                    motion.ParamKeys = parameters.KeyArray();
                    motion.ParamCount = motion.ParamKeys.Length;
                    motion.ParamValues = new float[motion.ParamCount];
                    for (int k = 0; k < motion.ParamCount; k++)
                    {
                        motion.ParamValues[k] = parameters.GetFloat(motion.ParamKeys[k], 0f);
                    }
                }

                LevelJsonNode provenance = item.Get("provenance");
                if (provenance != null && provenance.Kind == LevelJsonKind.Object)
                {
                    ReportUnknown(provenance, "motions[" + i + "].provenance",
                        MotionProvenanceKeys, result, document);
                    motion.ProvenanceMoveType = provenance.GetString("moveType");
                    motion.ProvenanceAction = provenance.GetString("action");
                    motion.ProvenanceBackupIndex = provenance.GetInt("backupIndex", -1);
                    motion.ProvenanceMotionId = provenance.GetInt("motionID", -1);
                    motion.ProvenancePathIndex = provenance.GetInt("pathIndex", -1);
                }

                motions[i] = motion;
            }

            return motions;
        }

        private static PlacementV2Path[] ReadPaths(LevelJsonNode node, PlacementV2ReadResult result,
            PlacementV2Document document)
        {
            if (node == null) return new PlacementV2Path[0];
            if (node.Kind != LevelJsonKind.Array)
            {
                result.Errors.Add("[paths_id] paths must be an array");
                return new PlacementV2Path[0];
            }

            var paths = new PlacementV2Path[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Object)
                {
                    result.Errors.Add("paths[" + i + "] is not an object");
                    continue;
                }

                ReportUnknown(item, "paths[" + i + "]", PathKeys, result, document);
                var path = new PlacementV2Path
                {
                    Id = item.GetString("id"),
                    Loop = item.GetBool("loop", false),
                    LoopMode = item.GetString("loop"),
                    HasSpeedCmPerSec = item.Has("speedCmPerSec"),
                    SpeedCmPerSec = item.GetFloat("speedCmPerSec", 0f),
                    Notes = item.GetString("notes")
                };
                float[][] points;
                if (LevelJsonValues.TryNumberGroups(item.Get("points"), 3, out points))
                {
                    path.Points = points;
                }
                else if (item.Has("points"))
                {
                    result.Errors.Add("[path_points] paths[" + i + "].points must be [[x,y,z],...]");
                }

                paths[i] = path;
            }

            return paths;
        }

        // ------------------------------------------------------------- validation ---

        private static void ValidateDocumentShape(PlacementV2Document document,
            PlacementV2ReadResult result)
        {
            PlacementV2Meta meta = document.Meta;
            if (meta == null) return;

            if (!string.Equals(meta.Units, "m", StringComparison.Ordinal))
            {
                result.Errors.Add("[meta_units] units is '" + meta.Units + "', expected 'm'");
            }

            if (!string.Equals(meta.Convention, "unity-y-up-left-handed-quat-xyzw",
                StringComparison.Ordinal))
            {
                result.Errors.Add("[meta_convention] convention is '" + meta.Convention +
                                  "', expected 'unity-y-up-left-handed-quat-xyzw'");
            }

            if (string.IsNullOrEmpty(meta.Id))
            {
                result.Errors.Add("[v2_source] meta.id is required (it names the level root)");
            }

            if (string.IsNullOrEmpty(meta.Scene))
            {
                result.Errors.Add("[v2_source] meta.scene is required");
            }

            if (meta.Ball == null) return;
            const float cap = KatamariBallMotor.GlobalMaxBallDiameterM;
            if (!(meta.Ball.StartDiameterM > 0f && meta.Ball.StartDiameterM < cap))
            {
                result.Errors.Add("[meta_start_diameter] ball.startDiameterM must be > 0 and < "
                                  + cap + " m (got " + meta.Ball.StartDiameterM + ")");
            }

            if (meta.Ball.Start == null)
            {
                result.Errors.Add("[meta_start_diameter] ball.start must be [x,y,z]");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < document.Props.Length; i++)
            {
                PlacementV2Prop prop = document.Props[i];
                if (prop == null) continue;
                if (string.IsNullOrEmpty(prop.ItemId))
                {
                    result.Errors.Add("props[" + i + "] has no itemId");
                    continue;
                }

                if (!ids.Add(prop.ItemId))
                {
                    result.Errors.Add("[prop_id] itemId '" + prop.ItemId + "' is duplicated");
                }

                if (string.IsNullOrEmpty(prop.Def))
                {
                    result.Errors.Add(prop.ItemId + " has no def");
                }

                if (prop.Position == null)
                {
                    result.Errors.Add(prop.ItemId + " has no usable position[3]");
                }

                if (prop.RotationQuat == null)
                {
                    result.Errors.Add(prop.ItemId + " has no usable rotationQuat[4]");
                }

                if (prop.Aabb == null)
                {
                    result.Errors.Add(prop.ItemId + " has no usable aabb[3]");
                }

                if (prop.HasInstanceIndex && prop.InstanceIndex < 0)
                {
                    result.Errors.Add("[prop_instance_index] " + prop.ItemId + " instanceIndex " +
                                      prop.InstanceIndex + " must be a whole number >= 0");
                }
            }

            var solidIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < document.Solids.Length; i++)
            {
                PlacementV2Solid solid = document.Solids[i];
                if (solid == null) continue;
                if (string.IsNullOrEmpty(solid.Id))
                {
                    result.Errors.Add("structures.solids[" + i + "] has no id");
                    continue;
                }

                if (!solidIds.Add(solid.Id))
                {
                    result.Errors.Add("[structure_role] solid id '" + solid.Id + "' is duplicated");
                }
            }
        }

        // ------------------------------------------------------------------ helpers ---

        private static int ReadAreaNo(LevelJsonNode node, string key, string path, int minimum,
            PlacementV2ReadResult result)
        {
            LevelJsonNode value = node.Get(key);
            if (value == null) return -1;
            double number = value.NumberValue;
            if (value.Kind == LevelJsonKind.Number && number >= minimum && number <= int.MaxValue &&
                number == Math.Truncate(number)) return (int)number;
            result.Errors.Add("[area_collision] " + path + "." + key +
                " must be an integer >= " + minimum + " within Int32 range");
            return -1;
        }

        private static float[] ReadVector(LevelJsonNode node, string name, int length)
        {
            if (node == null || !node.Has(name)) return null;
            float[] values;
            return LevelJsonValues.TryNumbers(node.Get(name), length, out values) ? values : null;
        }

        private static int ArrayLength(LevelJsonNode node)
        {
            return node != null && node.Kind == LevelJsonKind.Array ? node.Count : 0;
        }

        /// <summary>
        /// One <c>[v2_key]</c> warning per key the block's whitelist does not know.
        /// </summary>
        /// <remarks>
        /// A warning, not an error: the reference documents carry author-side extras
        /// (<c>meta.expected.growthMm3</c>, <c>migration.volumePenalty</c>) that a reader must
        /// not refuse, and the message names the dotted path so nothing is silently dropped.
        /// </remarks>
        private static void ReportUnknown(LevelJsonNode node, string path, string[] allowed,
            PlacementV2ReadResult result, PlacementV2Document document)
        {
            if (node == null || node.Kind != LevelJsonKind.Object || node.Fields == null) return;
            string[] keys = node.KeyArray();
            for (int i = 0; i < keys.Length; i++)
            {
                if (Array.IndexOf(allowed, keys[i]) >= 0) continue;
                string message = "[v2_key] " + (path.Length == 0 ? keys[i] : path + "." + keys[i]) +
                                 " is not a known v2 key (it is preserved nowhere; fix the writer)";
                result.Warnings.Add(message);
                if (document != null) document.Warnings.Add(message);
            }
        }
    }
}
