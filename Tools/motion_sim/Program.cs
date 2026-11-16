// motion_sim - the editor's one and only motion evaluator.
//
// It compiles VRC_Katamari/Runtime/Pickup/MotionMath.cs (and the files that file depends
// on: Runtime/Core/KatamariMath.cs and Runtime/Pickup/PhaseSeed.cs) as SOURCE and exposes
// them to the Python level editor:
//
//   motion_sim --level <placement.v2.json> --mode params
//       Resolve, per moving prop, everything the editor needs to *display* the motion
//       data: class, update tier, D-12 phase seed and phase, resolved speed, the half
//       height the class moves about, the WaitForPlayer trigger radius, and the source of
//       every number.  No time is simulated.
//
//   motion_sim --level <placement.v2.json> --mode trajectory --seconds 30 --rate 30
//       Step every moving prop through MotionMath at the original's 30 Hz (dt = 1 game
//       frame per step) and sample its WORLD pose.  Attached props and props with a parent
//       are composed onto their parent's transform.
//
// Why a separate process instead of reimplementing the classes in Python or JavaScript:
// the repository rule is one implementation of each piece of logic.  MotionMath.cs is the
// authority for the 13 classes (Docs/Research/KatamariBallRules/ORIGINAL_MOTION_PROGRAM.md
// sections 12-15), so the editor runs that file rather than a translation of it.  Nothing
// here restates a motion constant: every number comes from calling MotionMath, and any
// behaviour this front end cannot express is listed in "assumptions" instead of guessed.
//
// Composition is the one part that is genuinely this tool's decision, so it is stated once
// here and echoed in every response:
//   * MotionMath's own class comment names three output spaces.  A spawn-local class
//     (spin, flip, bob, sway, whack_a_mole, hop, wait_for_player, dragged_along) returns an
//     offset and an euler in the spawn frame, so world = spawn * T(offset) * R(euler).  A
//     world-axis class (sporadic_walk) returns a world-axis offset and a world yaw; clouds
//     pass ignoreYaw, so they keep the spawn rotation.  The absolute-world classes
//     (path_collision, sloping_path, back_and_forth) return a world position whose yaw and
//     pitch are the path's own world angles.
//   * A parent's motion is carried by the child:
//         world(child) = delta(parent) * inverse(spawn(parent)) * spawn(child) * local(child)
//     where delta(parent) = world(parent) * inverse(spawn(parent)).  A static parent makes
//     delta the identity, so the child of a static parent does not move - which is the case
//     for 13 of Cancer's 15 attached props.
//   * inherit = "position" follows the parent's translation but keeps its own rotation;
//     inherit = "none" (or no parent) means the prop's own pose stands alone.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using UnityEngine;
using VRCKatamari.Pickup;

namespace MotionSim
{
    /// <summary>One rigid transform: a position and a rotation, composed in that order.</summary>
    internal struct Pose
    {
        public Vector3 Position;
        public float Qx;
        public float Qy;
        public float Qz;
        public float Qw;

        public static Pose Identity
        {
            get { return new Pose { Position = Vector3.zero, Qx = 0f, Qy = 0f, Qz = 0f, Qw = 1f }; }
        }

        public static Pose From(Vector3 position, float qx, float qy, float qz, float qw)
        {
            return new Pose { Position = position, Qx = qx, Qy = qy, Qz = qz, Qw = qw };
        }

        public static Pose Translation(Vector3 offset)
        {
            Pose pose = Identity;
            pose.Position = offset;
            return pose;
        }
    }

    /// <summary>Rigid-transform algebra on <see cref="Pose"/> (there is no scale anywhere).</summary>
    internal static class PoseMath
    {
        public static Pose Compose(Pose a, Pose b)
        {
            // a then b: rotate b's position into a's frame, multiply the rotations.
            Vector3 rotated = Rotate(a, b.Position);
            float x;
            float y;
            float z;
            float w;
            Multiply(a.Qx, a.Qy, a.Qz, a.Qw, b.Qx, b.Qy, b.Qz, b.Qw, out x, out y, out z, out w);
            return Pose.From(new Vector3(a.Position.x + rotated.x, a.Position.y + rotated.y,
                a.Position.z + rotated.z), x, y, z, w);
        }

        public static Pose Inverse(Pose pose)
        {
            float x = -pose.Qx;
            float y = -pose.Qy;
            float z = -pose.Qz;
            float w = pose.Qw;
            Vector3 rotated = Rotate(Pose.From(Vector3.zero, x, y, z, w), pose.Position);
            return Pose.From(new Vector3(-rotated.x, -rotated.y, -rotated.z), x, y, z, w);
        }

        public static Vector3 Rotate(Pose pose, Vector3 point)
        {
            // v' = v + 2 * cross(q.xyz, cross(q.xyz, v) + q.w * v)
            float tx = 2f * (pose.Qy * point.z - pose.Qz * point.y);
            float ty = 2f * (pose.Qz * point.x - pose.Qx * point.z);
            float tz = 2f * (pose.Qx * point.y - pose.Qy * point.x);
            return new Vector3(
                point.x + pose.Qw * tx + (pose.Qy * tz - pose.Qz * ty),
                point.y + pose.Qw * ty + (pose.Qz * tx - pose.Qx * tz),
                point.z + pose.Qw * tz + (pose.Qx * ty - pose.Qy * tx));
        }

        public static void Multiply(float ax, float ay, float az, float aw,
            float bx, float by, float bz, float bw,
            out float x, out float y, out float z, out float w)
        {
            x = aw * bx + ax * bw + ay * bz - az * by;
            y = aw * by - ax * bz + ay * bw + az * bx;
            z = aw * bz + ax * by - ay * bx + az * bw;
            w = aw * bw - ax * bx - ay * by - az * bz;
        }

        /// <summary>
        /// Euler radians to a quaternion in Unity's <c>Quaternion.Euler(x, y, z)</c> order:
        /// the Z rotation is applied first, then X, then Y, i.e. q = qy * qx * qz.
        /// </summary>
        public static void EulerToQuaternion(Vector3 euler, out float x, out float y, out float z,
            out float w)
        {
            float hx = euler.x * 0.5f;
            float hy = euler.y * 0.5f;
            float hz = euler.z * 0.5f;
            float qxX = Mathf.Sin(hx);
            float qxW = Mathf.Cos(hx);
            float qyY = Mathf.Sin(hy);
            float qyW = Mathf.Cos(hy);
            float qzZ = Mathf.Sin(hz);
            float qzW = Mathf.Cos(hz);
            float tx;
            float ty;
            float tz;
            float tw;
            Multiply(0f, qyY, 0f, qyW, qxX, 0f, 0f, qxW, out tx, out ty, out tz, out tw);
            Multiply(tx, ty, tz, tw, 0f, 0f, qzZ, qzW, out x, out y, out z, out w);
        }

        public static Pose FromSpawn(float[] position, float[] quaternion)
        {
            return Pose.From(new Vector3(position[0], position[1], position[2]),
                quaternion[0], quaternion[1], quaternion[2], quaternion[3]);
        }
    }

    /// <summary>Which space a class's position answer lives in (MotionMath's own comment).</summary>
    internal enum OutputSpace
    {
        SpawnLocal,
        WorldAxis,
        AbsoluteWorld,
    }

    internal sealed class MotionSpec
    {
        public string Id;
        public string Class;
        public string SpeedSource = "none";
        public float ExplicitCmPerSec;
        public string PathId;
        public int UpdatesPerSecond;
        public bool Loop = true;
        public int MotionIdCode = -1;
        public readonly Dictionary<string, double> Params = new Dictionary<string, double>();
    }

    internal sealed class PathSpec
    {
        public string Id;
        public float[] Points = new float[0];
        public int PointCount;
        public string Loop;
        public float SpeedCmPerSec;
        public bool Closed = true;
    }

    internal sealed class PropSpec
    {
        public string ItemId;
        public string Def;
        public string Behaviour;
        public string Inherit = "none";
        public string ParentId;
        public string MotionId;
        public MotionSpec Motion;
        public PathSpec Path;
        public float[] Position = { 0f, 0f, 0f };
        public float[] RotationQuat = { 0f, 0f, 0f, 1f };
        public float[] Aabb = { 0f, 0f, 0f };
        public bool HasDocumentPhase;
        public double DocumentPhase;
        public readonly List<string> Assumptions = new List<string>();

        // Resolved by Resolve().
        public OutputSpace Space = OutputSpace.SpawnLocal;
        public bool KeepSpawnRotation;
        public bool SwayAppliesTranslation = true;
        public float HalfHeightM;
        public int SizeGroup;
        public float HopSpeedCmPerFrame;
        public float SpeedMps;
        public float SpeedCmPerSec;
        public int SpeedIndex = -1;
        public float TriggerRadiusM;
        public int PhaseSeed;
        public float Phase;
        public bool Moves;
        public string MovementError;
        public float[] State = new float[0];
        public Pose SpawnPose;

        // Per-frame class output, before parent composition.
        public Vector3 MotionOffset;
        public Vector3 MotionAbsolutePosition;
        public Vector3 MotionEuler;
        public bool StepOk = true;
        public bool Triggered;
        public Pose CachedPose;
        public bool CachedPoseValid;
    }

    internal sealed class LevelDocument
    {
        public string LevelId = "";
        public readonly List<MotionSpec> Motions = new List<MotionSpec>();
        public readonly List<PathSpec> Paths = new List<PathSpec>();
        public readonly List<PropSpec> Props = new List<PropSpec>();
        public readonly Dictionary<string, int> SpeedIndexByDef = new Dictionary<string, int>();
        public readonly Dictionary<string, PropSpec> ById = new Dictionary<string, PropSpec>();
        public readonly Dictionary<string, MotionSpec> MotionById =
            new Dictionary<string, MotionSpec>();
        public readonly Dictionary<string, PathSpec> PathById = new Dictionary<string, PathSpec>();
    }

    internal sealed class Options
    {
        public string LevelPath;
        public string Mode = "params";
        public readonly List<string> Items = new List<string>();
        public float Seconds = 10f;
        public int Rate = 30;
        public string Out = "-";
    }

    public static class Program
    {
        // MotionMath.SizeGroup(size) for the original size ids: 0,0,0,1,2,3,3,4,4,4,4,4.  v2
        // carries no original size id, so the tool has to be told; this default is the group
        // Cancer's 18 hop and 15 wait-for-player props actually use (all original size 2), and
        // every response says so in "assumptions".
        private const int AssumedSizeGroup = 0;

        // A level preview has no player: the WaitForPlayer gate is reported, not simulated.
        private const float NoObserverDistanceM = 1e6f;

        public static int Main(string[] args)
        {
            Options options;
            try
            {
                options = ParseArguments(args);
            }
            catch (ArgumentException error)
            {
                Console.Error.WriteLine("motion_sim: " + error.Message);
                Console.Error.WriteLine(Usage);
                return 2;
            }
            if (options == null)
            {
                Console.Out.WriteLine(Usage);
                return 0;
            }

            LevelDocument document;
            try
            {
                document = Load(options.LevelPath);
            }
            catch (Exception error) when (error is IOException || error is JsonException
                || error is UnauthorizedAccessException || error is FormatException)
            {
                Console.Error.WriteLine("motion_sim: cannot read " + options.LevelPath + ": "
                    + error.Message);
                return 2;
            }

            List<string> unknown = new List<string>();
            foreach (string item in options.Items)
            {
                if (!document.ById.ContainsKey(item)) unknown.Add(item);
            }
            if (unknown.Count > 0)
            {
                Console.Error.WriteLine("motion_sim: no such prop: " + string.Join(", ", unknown));
                return 3;
            }

            Resolve(document);
            MemoryStream buffer = new MemoryStream();
            using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
            {
                if (options.Mode == "trajectory")
                {
                    WriteTrajectory(writer, document, options);
                }
                else
                {
                    WriteParams(writer, document, options);
                }
            }

            string json = Encoding.UTF8.GetString(buffer.ToArray());
            if (options.Out == "-")
            {
                Console.Out.Write(json);
                Console.Out.Write('\n');
            }
            else
            {
                File.WriteAllText(options.Out, json + "\n", new UTF8Encoding(false));
            }
            return 0;
        }

        private static string Usage
        {
            get
            {
                return "usage: motion_sim --level <placement.v2.json> "
                    + "[--mode params|trajectory] [--items a,b] [--seconds N] [--rate 5|10|15|30] "
                    + "[--out file|-]";
            }
        }

        private static Options ParseArguments(string[] args)
        {
            if (args.Length == 0) return null;
            Options options = new Options();
            for (int index = 0; index < args.Length; index++)
            {
                string name = args[index];
                string value = null;
                if (name == "--help" || name == "-h") return null;
                if (index + 1 >= args.Length)
                    throw new ArgumentException(name + " needs a value");
                value = args[index + 1];
                index++;
                switch (name)
                {
                    case "--level":
                        options.LevelPath = value;
                        break;
                    case "--mode":
                        options.Mode = value;
                        break;
                    case "--items":
                        foreach (string part in value.Split(','))
                        {
                            string trimmed = part.Trim();
                            if (trimmed.Length > 0) options.Items.Add(trimmed);
                        }
                        break;
                    case "--seconds":
                        options.Seconds = ParseFloat(value, name);
                        break;
                    case "--rate":
                        options.Rate = ParseInt(value, name);
                        break;
                    case "--out":
                        options.Out = value;
                        break;
                    default:
                        throw new ArgumentException("unknown argument " + name);
                }
            }
            if (string.IsNullOrEmpty(options.LevelPath))
                throw new ArgumentException("--level is required");
            if (options.Mode != "params" && options.Mode != "trajectory")
                throw new ArgumentException("--mode must be params or trajectory");
            if (options.Seconds <= 0f || options.Seconds > 3600f)
                throw new ArgumentException("--seconds must be 0..3600");
            if (options.Rate != 5 && options.Rate != 10 && options.Rate != 15 && options.Rate != 30)
                throw new ArgumentException("--rate must be 5, 10, 15 or 30");
            return options;
        }

        private static float ParseFloat(string raw, string name)
        {
            float value;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new ArgumentException(name + " expects a number");
            return value;
        }

        private static int ParseInt(string raw, string name)
        {
            int value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new ArgumentException(name + " expects an integer");
            return value;
        }

        // ------------------------------------------------------------------ //
        // document
        // ------------------------------------------------------------------ //
        private static LevelDocument Load(string path)
        {
            LevelDocument document = new LevelDocument();
            byte[] bytes = File.ReadAllBytes(path);
            using (JsonDocument json = JsonDocument.Parse(bytes))
            {
                JsonElement root = json.RootElement;
                string schema = Text(root, "schema");
                if (schema != "katamari.placement/2")
                {
                    throw new FormatException("expected schema katamari.placement/2, found "
                        + (schema ?? "nothing"));
                }
                JsonElement meta;
                document.LevelId = root.TryGetProperty("meta", out meta) ? Text(meta, "id") : null;
                if (string.IsNullOrEmpty(document.LevelId))
                {
                    string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                    document.LevelId = folder == null ? "" : Path.GetFileName(folder);
                }

                JsonElement motions;
                if (root.TryGetProperty("motions", out motions)
                    && motions.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement element in motions.EnumerateArray())
                    {
                        MotionSpec spec = new MotionSpec();
                        spec.Id = Text(element, "id");
                        spec.Class = Text(element, "class");
                        if (string.IsNullOrEmpty(spec.Id) || string.IsNullOrEmpty(spec.Class))
                            continue;
                        JsonElement speed;
                        if (element.TryGetProperty("speed", out speed)
                            && speed.ValueKind == JsonValueKind.Object)
                        {
                            string source = Text(speed, "source");
                            if (!string.IsNullOrEmpty(source)) spec.SpeedSource = source;
                            spec.ExplicitCmPerSec = Number(speed, "cmPerSec", 0f);
                        }
                        spec.PathId = Text(element, "path");
                        spec.UpdatesPerSecond = (int)Number(element, "updatesPerSecond", 0f);
                        spec.Loop = Bool(element, "loop", true);
                        JsonElement provenance;
                        if (element.TryGetProperty("provenance", out provenance)
                            && provenance.ValueKind == JsonValueKind.Object)
                        {
                            spec.MotionIdCode = (int)Number(provenance, "motionID", -1f);
                        }
                        JsonElement parameters;
                        if (element.TryGetProperty("params", out parameters)
                            && parameters.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty property in parameters.EnumerateObject())
                            {
                                if (property.Value.ValueKind == JsonValueKind.Number)
                                {
                                    spec.Params[property.Name] = property.Value.GetDouble();
                                }
                            }
                        }
                        document.Motions.Add(spec);
                        document.MotionById[spec.Id] = spec;
                    }
                }

                JsonElement paths;
                if (root.TryGetProperty("paths", out paths)
                    && paths.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement element in paths.EnumerateArray())
                    {
                        PathSpec spec = new PathSpec();
                        spec.Id = Text(element, "id");
                        if (string.IsNullOrEmpty(spec.Id)) continue;
                        spec.Loop = Text(element, "loop");
                        spec.SpeedCmPerSec = Number(element, "speedCmPerSec", 0f);
                        List<float> points = new List<float>();
                        JsonElement array;
                        if (element.TryGetProperty("points", out array)
                            && array.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement point in array.EnumerateArray())
                            {
                                int axis = 0;
                                foreach (JsonElement component in point.EnumerateArray())
                                {
                                    points.Add((float)component.GetDouble());
                                    axis++;
                                    if (axis == 3) break;
                                }
                            }
                        }
                        spec.Points = points.ToArray();
                        spec.PointCount = spec.Points.Length / 3;
                        // Level format 6.7.4 allows "loop", "ping_pong" and "once"; the
                        // reference level leaves it out and the class decides:
                        // path_collision and sloping_path wrap (the original's
                        // pathIndex % pointCount), back_and_forth folds by definition.
                        spec.Closed = spec.Loop != "once";
                        document.Paths.Add(spec);
                        document.PathById[spec.Id] = spec;
                    }
                }

                JsonElement defs;
                if (root.TryGetProperty("defs", out defs) && defs.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement element in defs.EnumerateArray())
                    {
                        string id = Text(element, "id");
                        if (string.IsNullOrEmpty(id)) continue;
                        if (element.TryGetProperty("speedIndex", out JsonElement index)
                            && index.ValueKind == JsonValueKind.Number)
                        {
                            document.SpeedIndexByDef[id] = index.GetInt32();
                        }
                    }
                }

                JsonElement props;
                if (!root.TryGetProperty("props", out props)
                    || props.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("the document has no props[] array");
                }
                foreach (JsonElement element in props.EnumerateArray())
                {
                    PropSpec prop = new PropSpec();
                    prop.ItemId = Text(element, "itemId");
                    if (string.IsNullOrEmpty(prop.ItemId)) continue;
                    prop.Def = Text(element, "def");
                    prop.Behaviour = Text(element, "behaviour");
                    prop.Inherit = Text(element, "inherit");
                    prop.ParentId = Text(element, "parent");
                    prop.MotionId = Text(element, "motion");
                    prop.Position = Floats(element, "position", 3, new[] { 0f, 0f, 0f });
                    prop.RotationQuat = Floats(element, "rotationQuat", 4,
                        new[] { 0f, 0f, 0f, 1f });
                    prop.Aabb = Floats(element, "aabb", 3, new[] { 0f, 0f, 0f });
                    JsonElement phase;
                    if (element.TryGetProperty("phase", out phase)
                        && phase.ValueKind == JsonValueKind.Number)
                    {
                        prop.HasDocumentPhase = true;
                        prop.DocumentPhase = phase.GetDouble();
                    }
                    document.Props.Add(prop);
                    document.ById[prop.ItemId] = prop;
                }
            }
            return document;
        }

        private static string Text(JsonElement element, string name)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static float Number(JsonElement element, string name, float fallback)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value)) return fallback;
            return value.ValueKind == JsonValueKind.Number ? (float)value.GetDouble() : fallback;
        }

        private static bool Bool(JsonElement element, string name, bool fallback)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value)) return fallback;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            return fallback;
        }

        private static float[] Floats(JsonElement element, string name, int count, float[] fallback)
        {
            JsonElement value;
            if (!element.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Array)
                return fallback;
            float[] result = new float[count];
            int index = 0;
            foreach (JsonElement component in value.EnumerateArray())
            {
                if (index >= count) break;
                if (component.ValueKind != JsonValueKind.Number) return fallback;
                result[index] = (float)component.GetDouble();
                index++;
            }
            return index < count ? fallback : result;
        }

        // ------------------------------------------------------------------ //
        // resolution: document facts + MotionMath's own constants
        // ------------------------------------------------------------------ //
        private static void Resolve(LevelDocument document)
        {
            foreach (PropSpec prop in document.Props)
            {
                prop.SpawnPose = PoseMath.FromSpawn(prop.Position, prop.RotationQuat);
                prop.SizeGroup = AssumedSizeGroup;
                prop.HalfHeightM = Mathf.Abs(prop.Aabb[1]) * 0.5f;
                prop.PhaseSeed = PhaseSeed.From(document.LevelId, prop.ItemId);
                prop.Phase = MotionMath.Phase01(prop.PhaseSeed);
                prop.SpeedIndex = document.SpeedIndexByDef.ContainsKey(prop.Def ?? "")
                    ? document.SpeedIndexByDef[prop.Def ?? ""]
                    : -1;

                if (!string.IsNullOrEmpty(prop.MotionId))
                {
                    MotionSpec motion;
                    if (document.MotionById.TryGetValue(prop.MotionId, out motion))
                    {
                        prop.Motion = motion;
                        prop.Moves = true;
                    }
                    else
                    {
                        prop.MovementError = "missing motion " + prop.MotionId;
                        prop.Assumptions.Add("props[].motion refers to " + prop.MotionId
                            + ", which is not in motions[]; the prop is treated as static");
                    }
                }

                if (prop.Motion != null)
                {
                    MotionSpec motion = prop.Motion;
                    prop.Space = SpaceOf(motion.Class);
                    prop.KeepSpawnRotation = motion.Class == "clouds";
                    // The path is resolved before the speed: 6.7.4 makes a path record's own
                    // speed override the type speed, so the path has to be in hand first.
                    if (motion.PathId != null)
                    {
                        PathSpec path;
                        if (document.PathById.TryGetValue(motion.PathId, out path))
                        {
                            prop.Path = path;
                        }
                        else
                        {
                            prop.MovementError = "missing path " + motion.PathId;
                            prop.Assumptions.Add("motion " + motion.Id + " refers to path "
                                + motion.PathId + ", which is not in paths[]; the prop freezes");
                        }
                    }
                    ResolveSpeed(prop, motion);
                    ResolveClassSpecifics(document, prop, motion);
                }
                else if (prop.Behaviour == "attached")
                {
                    if (string.IsNullOrEmpty(prop.ParentId))
                    {
                        prop.Assumptions.Add("behaviour attached without a parent (the "
                            + "original's 'joint part' flag, parent = -1): the prop does not "
                            + "move");
                    }
                }
            }

            // A prop that does not move itself but rides a moving parent still has to be
            // evaluated: that is the original's 43 "still child rides a moving parent".  A
            // child of a *static* parent does not move (the delta is the identity), so it is
            // left out - that is 13 of Cancer's 15 attached props and the 40 "join-only"
            // records with no parent at all.
            foreach (PropSpec prop in document.Props)
            {
                if (prop.Moves) continue;
                if (RidesAMovingParent(document, prop, 0)) prop.Moves = true;
            }
        }

        private static bool RidesAMovingParent(LevelDocument document, PropSpec prop, int depth)
        {
            if (prop.Motion != null) return true;
            if (depth >= 32) return false;
            if (string.IsNullOrEmpty(prop.ParentId) || prop.Inherit == "none") return false;
            PropSpec parent;
            if (!document.ById.TryGetValue(prop.ParentId, out parent)) return false;
            if (ReferenceEquals(parent, prop)) return false;
            return RidesAMovingParent(document, parent, depth + 1);
        }

        private static void ResolveClassSpecifics(LevelDocument document, PropSpec prop,
            MotionSpec motion)
        {
            if (motion.Class == "hop")
            {
                prop.HopSpeedCmPerFrame = MotionMath.HopSpeedCmPerFrame(prop.SizeGroup);
                prop.Assumptions.Add("hop speed from size group " + prop.SizeGroup + " = "
                    + Number(prop.HopSpeedCmPerFrame) + " cm/game-frame (v2 carries no "
                    + "original size id)");
            }
            if (motion.Class == "wait_for_player")
            {
                prop.TriggerRadiusM = MotionMath.PanicRadiusM(prop.SizeGroup);
                prop.Assumptions.Add("trigger radius = PanicRadiusM(size group " + prop.SizeGroup
                    + ") = " + Number(prop.TriggerRadiusM) + " m; no player is simulated");
                if (motion.Params.ContainsKey("triggerRadiusM"))
                {
                    prop.Assumptions.Add("motions[].params.triggerRadiusM is declared but "
                        + "MotionMath's gate takes the size group; the size-group table wins");
                }
            }
            if (motion.Class == "spin" || motion.Class == "flip")
            {
                prop.Assumptions.Add("rate is MotionMath's 0.05 rad/game-frame constant; a "
                    + "motions[].params.degPerSec is not applied by MotionMath");
            }
            if (motion.Class == "sway")
            {
                // 12.4 skips Sway's translation for the original's BALANCEDOLL01_C.  v2 keeps
                // the model's internal name only in notes, so the def id is the best handle
                // there is; Cancer has no such def, and the assumption is reported when one
                // matches.
                string def = (prop.Def ?? "").ToLowerInvariant();
                bool balanced = def.Contains("balancedoll") || def.Contains("balance_doll")
                    || def.Contains("roly");
                prop.SwayAppliesTranslation = !balanced;
                if (balanced)
                {
                    prop.Assumptions.Add("def looks like the original's BALANCEDOLL01_C: Sway's "
                        + "translation is skipped (12.4)");
                }
            }
            if (prop.HasDocumentPhase)
            {
                prop.Assumptions.Add("props[].phase = " + Number((float)prop.DocumentPhase)
                    + " is declared but MotionMath's Reset* API takes only a seed; the D-12 hash "
                    + "seed is used and the declared phase is ignored");
            }
            if (prop.Space == OutputSpace.AbsoluteWorld && !string.IsNullOrEmpty(prop.ParentId))
            {
                prop.Assumptions.Add("absolute-world class: the parent link is not composed "
                    + "onto its pose");
            }
        }

        private static string Number(float value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static OutputSpace SpaceOf(string motionClass)
        {
            switch (motionClass)
            {
                case "sporadic_walk":
                case "clouds":
                    return OutputSpace.WorldAxis;
                case "path_collision":
                case "sloping_path":
                case "back_and_forth":
                    return OutputSpace.AbsoluteWorld;
                default:
                    return OutputSpace.SpawnLocal;
            }
        }

        private static void ResolveSpeed(PropSpec prop, MotionSpec motion)
        {
            switch (motion.SpeedSource)
            {
                case "path":
                    if (prop.Path != null)
                    {
                        prop.SpeedCmPerSec = prop.Path.SpeedCmPerSec;
                        prop.SpeedMps = prop.Path.SpeedCmPerSec * MotionMath.MetresPerCentimetre;
                        return;
                    }
                    // 6.7.4: only a path record with a speed overrides the type speed.
                    ResolveTypeSpeed(prop);
                    return;
                case "type":
                    ResolveTypeSpeed(prop);
                    return;
                case "explicit":
                    prop.SpeedCmPerSec = motion.ExplicitCmPerSec;
                    prop.SpeedMps = motion.ExplicitCmPerSec * MotionMath.MetresPerCentimetre;
                    return;
                default:
                    return;
            }
        }

        private static void ResolveTypeSpeed(PropSpec prop)
        {
            if (prop.SpeedIndex < 0)
            {
                prop.Assumptions.Add("speed source 'type' but defs[] has no speedIndex for "
                    + (prop.Def ?? "?") + "; the class runs at 0 m/s");
                return;
            }
            // The one conversion: MotionMath's speed table is cm per 30 Hz game frame.
            float metresPerFrame = MotionMath.SpeedCmPerFrame(prop.SpeedIndex)
                * MotionMath.MetresPerCentimetre;
            prop.SpeedMps = metresPerFrame * MotionMath.GameFramesPerSecond;
            prop.SpeedCmPerSec = prop.SpeedMps * 100f;
        }

        // ------------------------------------------------------------------ //
        // simulation
        // ------------------------------------------------------------------ //
        private static void PrepareStates(LevelDocument document)
        {
            foreach (PropSpec prop in document.Props)
            {
                if (prop.Motion == null || prop.MovementError != null) continue;
                prop.State = new float[StateFloats(prop.Motion.Class)];
                Reset(prop);
            }
        }

        private static int StateFloats(string motionClass)
        {
            switch (motionClass)
            {
                case "spin": return MotionMath.SpinStateFloats;
                case "flip": return MotionMath.FlipStateFloats;
                case "bob": return MotionMath.BobStateFloats;
                case "sway": return MotionMath.SwayStateFloats;
                case "whack_a_mole": return MotionMath.WhackAMoleStateFloats;
                case "hop": return MotionMath.HopStateFloats;
                case "sporadic_walk":
                case "clouds":
                    return MotionMath.WanderStateFloats;
                case "wait_for_player": return MotionMath.WaitForPlayerStateFloats;
                case "dragged_along": return MotionMath.DraggedAlongStateFloats;
                case "path_collision": return MotionMath.PathCollisionStateFloats;
                case "sloping_path": return MotionMath.SlopingPathStateFloats;
                case "back_and_forth": return MotionMath.BackAndForthNoYawStateFloats;
                default: return 0;
            }
        }

        private static void Reset(PropSpec prop)
        {
            switch (prop.Motion.Class)
            {
                case "spin": MotionMath.ResetSpin(prop.State, prop.PhaseSeed); break;
                case "flip": MotionMath.ResetFlip(prop.State, prop.PhaseSeed); break;
                case "bob": MotionMath.ResetBob(prop.State, prop.PhaseSeed); break;
                case "sway": MotionMath.ResetSway(prop.State, prop.PhaseSeed); break;
                case "whack_a_mole": MotionMath.ResetWhackAMole(prop.State, prop.PhaseSeed); break;
                case "hop": MotionMath.ResetHop(prop.State, prop.PhaseSeed); break;
                case "sporadic_walk":
                    MotionMath.ResetSporadicWalk(prop.State, prop.PhaseSeed);
                    break;
                case "clouds": MotionMath.ResetClouds(prop.State, prop.PhaseSeed); break;
                case "wait_for_player": MotionMath.ResetWaitForPlayer(prop.State); break;
                case "path_collision": MotionMath.ResetPathCollision(prop.State); break;
                case "sloping_path": MotionMath.ResetSlopingPath(prop.State); break;
                case "back_and_forth":
                    MotionMath.ResetBackAndForthNoYaw(prop.State);
                    break;
            }
        }

        /// <summary>
        /// One step of dt game frames for one prop, leaving the class's raw answer in
        /// MotionOffset / MotionAbsolutePosition / MotionEuler (dt = 0 recomputes the pose
        /// without advancing, which is how MotionMath lets a frozen prop report itself).
        /// </summary>
        private static void Step(PropSpec prop, float dt)
        {
            Vector3 offset = Vector3.zero;
            Vector3 euler = Vector3.zero;
            bool ok;
            switch (prop.Motion.Class)
            {
                case "spin":
                    ok = MotionMath.TryStepSpin(prop.State, dt, out offset, out euler);
                    break;
                case "flip":
                    ok = MotionMath.TryStepFlip(prop.State, dt, out offset, out euler);
                    break;
                case "bob":
                    ok = MotionMath.TryStepBob(prop.State, dt, prop.HalfHeightM, out offset,
                        out euler);
                    break;
                case "sway":
                    ok = MotionMath.TryStepSway(prop.State, dt, prop.HalfHeightM,
                        prop.SwayAppliesTranslation, out offset, out euler);
                    break;
                case "whack_a_mole":
                    ok = MotionMath.TryStepWhackAMole(prop.State, dt, prop.PhaseSeed,
                        prop.HalfHeightM, prop.Motion.Loop, out offset, out euler);
                    break;
                case "hop":
                    ok = MotionMath.TryStepHop(prop.State, dt, prop.PhaseSeed,
                        prop.HopSpeedCmPerFrame, out offset, out euler);
                    break;
                case "sporadic_walk":
                    // The host's zone and obstacle rays are not modelled: every step is
                    // allowed and every turn candidate is open (the approximation is
                    // reported in "assumptions").
                    ok = MotionMath.TryStepSporadicWalk(prop.State, dt, prop.PhaseSeed,
                        prop.SpeedMps, 0x7F, false, out offset, out euler);
                    break;
                case "clouds":
                    ok = MotionMath.TryStepClouds(prop.State, dt, prop.PhaseSeed, prop.SpeedMps,
                        0x7F, out offset, out euler);
                    break;
                case "wait_for_player":
                {
                    bool triggered;
                    ok = MotionMath.TryStepWaitForPlayer(prop.State, prop.Motion.MotionIdCode,
                        NoObserverDistanceM, prop.SizeGroup, false, false, out triggered,
                        out offset, out euler);
                    prop.Triggered = triggered;
                    break;
                }
                case "path_collision":
                {
                    Vector3 position;
                    ok = MotionMath.TryStepPathCollision(prop.State, dt, SpawnVector(prop),
                        prop.Path.Points, prop.Path.PointCount, prop.SpeedMps, prop.HalfHeightM,
                        prop.Path.Closed, out position, out euler);
                    prop.MotionAbsolutePosition = position;
                    prop.MotionEuler = euler;
                    prop.StepOk = ok;
                    return;
                }
                case "sloping_path":
                {
                    Vector3 position;
                    ok = MotionMath.TryStepSlopingPath(prop.State, dt, SpawnVector(prop),
                        prop.Path.Points, prop.Path.PointCount, prop.SpeedMps, prop.HalfHeightM,
                        prop.Path.Closed, out position, out euler);
                    prop.MotionAbsolutePosition = position;
                    prop.MotionEuler = euler;
                    prop.StepOk = ok;
                    return;
                }
                case "back_and_forth":
                {
                    Vector3 position;
                    ok = MotionMath.TryStepBackAndForthNoYaw(prop.State, dt, SpawnVector(prop),
                        prop.Path.Points, prop.Path.PointCount, prop.SpeedMps, prop.HalfHeightM,
                        out position, out euler);
                    prop.MotionAbsolutePosition = position;
                    prop.MotionEuler = euler;
                    prop.StepOk = ok;
                    return;
                }
                case "dragged_along":
                    ok = MotionMath.TryStepDraggedAlong(dt, out offset, out euler);
                    break;
                default:
                    ok = false;
                    break;
            }
            prop.MotionOffset = offset;
            prop.MotionEuler = euler;
            prop.StepOk = ok;
        }

        private static Vector3 SpawnVector(PropSpec prop)
        {
            return new Vector3(prop.Position[0], prop.Position[1], prop.Position[2]);
        }

        private static void StepFrame(LevelDocument document, float dt)
        {
            foreach (PropSpec prop in document.Props)
            {
                prop.CachedPoseValid = false;
                prop.MotionOffset = Vector3.zero;
                prop.MotionEuler = Vector3.zero;
                prop.MotionAbsolutePosition = Vector3.zero;
                prop.StepOk = true;
                if (prop.Motion == null || prop.MovementError != null) continue;
                Step(prop, dt);
            }
        }

        /// <summary>The prop's world pose this frame, with its parent chain applied.</summary>
        private static Pose WorldPose(LevelDocument document, PropSpec prop, int depth)
        {
            if (prop.CachedPoseValid) return prop.CachedPose;

            // The pose the prop has with no parent at all; a parent's motion then carries it.
            Pose own = StandalonePose(prop);
            Pose result = own;
            if (depth < 32 && !string.IsNullOrEmpty(prop.ParentId) && prop.Inherit != "none"
                && prop.Space != OutputSpace.AbsoluteWorld)
            {
                PropSpec parent;
                if (document.ById.TryGetValue(prop.ParentId, out parent)
                    && !ReferenceEquals(parent, prop))
                {
                    Pose parentWorld = WorldPose(document, parent, depth + 1);
                    if (prop.Inherit == "position")
                    {
                        // Translation only: the child rides the parent's translation but keeps
                        // its own rotation, so `own` is simply translated by the parent's delta.
                        Vector3 delta = new Vector3(
                            parentWorld.Position.x - parent.SpawnPose.Position.x,
                            parentWorld.Position.y - parent.SpawnPose.Position.y,
                            parentWorld.Position.z - parent.SpawnPose.Position.z);
                        result.Position = new Vector3(
                            own.Position.x + delta.x,
                            own.Position.y + delta.y,
                            own.Position.z + delta.z);
                    }
                    else
                    {
                        // world(child) = world(parent) * inverse(spawn(parent)) * own(child),
                        // because own(child) is already spawn(child) * localMotion(child).
                        Pose carry = PoseMath.Compose(parentWorld,
                            PoseMath.Inverse(parent.SpawnPose));
                        result = PoseMath.Compose(carry, own);
                    }
                }
            }
            prop.CachedPose = result;
            prop.CachedPoseValid = true;
            return result;
        }

        /// <summary>
        /// The pose the prop's class produces on its own, i.e. what the original would show
        /// with no parent in the world.  The class's own output space decides how the class's
        /// answer turns into a pose (MotionMath's class comment names the three spaces).
        /// </summary>
        private static Pose StandalonePose(PropSpec prop)
        {
            Vector3 euler = prop.MotionEuler;
            float qx;
            float qy;
            float qz;
            float qw;
            switch (prop.Space)
            {
                case OutputSpace.AbsoluteWorld:
                    PoseMath.EulerToQuaternion(euler, out qx, out qy, out qz, out qw);
                    return Pose.From(prop.MotionAbsolutePosition, qx, qy, qz, qw);
                case OutputSpace.WorldAxis:
                {
                    Pose spawn = prop.SpawnPose;
                    Vector3 position = new Vector3(
                        spawn.Position.x + prop.MotionOffset.x,
                        spawn.Position.y + prop.MotionOffset.y,
                        spawn.Position.z + prop.MotionOffset.z);
                    if (prop.KeepSpawnRotation)
                    {
                        return Pose.From(position, spawn.Qx, spawn.Qy, spawn.Qz, spawn.Qw);
                    }
                    PoseMath.EulerToQuaternion(euler, out qx, out qy, out qz, out qw);
                    return Pose.From(position, qx, qy, qz, qw);
                }
                default:
                {
                    // Spawn-local: world = spawn * T(offset) * R(euler), the composition
                    // section 12.0 of the motion research specifies.
                    PoseMath.EulerToQuaternion(euler, out qx, out qy, out qz, out qw);
                    Pose motion = PoseMath.Compose(Pose.Translation(prop.MotionOffset),
                        Pose.From(Vector3.zero, qx, qy, qz, qw));
                    return PoseMath.Compose(prop.SpawnPose, motion);
                }
            }
        }

        // ------------------------------------------------------------------ //
        // output
        // ------------------------------------------------------------------ //
        private static void WriteParams(Utf8JsonWriter writer, LevelDocument document,
            Options options)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("mode", "params");
            writer.WriteString("schema", "katamari.placement/2");
            writer.WriteString("level", document.LevelId);
            writer.WriteString("evaluator", "Tools/motion_sim (compiles MotionMath.cs)");
            WriteAssumptions(writer);
            WriteCounts(writer, document);

            writer.WriteStartArray("motions");
            foreach (MotionSpec motion in document.Motions)
            {
                writer.WriteStartObject();
                writer.WriteString("id", motion.Id);
                writer.WriteString("class", motion.Class);
                writer.WriteString("speedSource", motion.SpeedSource);
                writer.WriteString("path", motion.PathId);
                writer.WriteNumber("updatesPerSecond", motion.UpdatesPerSecond);
                writer.WriteBoolean("loop", motion.Loop);
                writer.WriteNumber("motionID", motion.MotionIdCode);
                writer.WriteString("outputSpace", SpaceName(SpaceOf(motion.Class)));
                writer.WriteStartObject("params");
                foreach (KeyValuePair<string, double> pair in motion.Params)
                {
                    writer.WriteNumber(pair.Key, pair.Value);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("paths");
            foreach (PathSpec path in document.Paths)
            {
                writer.WriteStartObject();
                writer.WriteString("id", path.Id);
                writer.WriteNumber("pointCount", path.PointCount);
                writer.WriteString("loop", path.Loop);
                writer.WriteBoolean("closed", path.Closed);
                writer.WriteNumber("speedCmPerSec", path.SpeedCmPerSec);
                WriteRoundedArray(writer, "points", path.Points, path.Points.Length, 4);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("props");
            foreach (PropSpec prop in document.Props)
            {
                if (!IncludeInParams(prop, options)) continue;
                writer.WriteStartObject();
                writer.WriteString("itemId", prop.ItemId);
                writer.WriteString("def", prop.Def);
                writer.WriteString("behaviour", prop.Behaviour);
                writer.WriteString("inherit", prop.Inherit);
                writer.WriteString("parent", prop.ParentId);
                writer.WriteString("motion", prop.Motion == null ? null : prop.Motion.Id);
                writer.WriteString("motionClass", ClassOf(prop));
                writer.WriteBoolean("moves", prop.Moves);
                writer.WriteNumber("updatesPerSecond",
                    prop.Motion == null ? 0 : prop.Motion.UpdatesPerSecond);
                writer.WriteBoolean("loop", prop.Motion != null && prop.Motion.Loop);
                writer.WriteNumber("phaseSeed", prop.PhaseSeed);
                writer.WriteNumber("phase", (float)Math.Round(prop.Phase, 4));
                writer.WriteNumber("sizeGroup", prop.SizeGroup);
                writer.WriteNumber("halfHeightM", (float)Math.Round(prop.HalfHeightM, 4));
                writer.WriteString("speedSource",
                    prop.Motion == null ? "none" : prop.Motion.SpeedSource);
                writer.WriteNumber("speedMps", (float)Math.Round(prop.SpeedMps, 4));
                writer.WriteNumber("speedCmPerSec", (float)Math.Round(prop.SpeedCmPerSec, 2));
                writer.WriteNumber("speedIndex", prop.SpeedIndex);
                if (prop.Motion != null && prop.Motion.Class == "hop")
                {
                    writer.WriteNumber("hopSpeedCmPerFrame", prop.HopSpeedCmPerFrame);
                }
                if (prop.Motion != null && prop.Motion.Class == "wait_for_player")
                {
                    writer.WriteNumber("triggerRadiusM", prop.TriggerRadiusM);
                    writer.WriteNumber("triggerMotionId", prop.Motion.MotionIdCode);
                }
                writer.WriteString("path", prop.Path == null ? null : prop.Path.Id);
                writer.WriteNumber("pathPointCount", prop.Path == null ? 0 : prop.Path.PointCount);
                if (prop.Path != null)
                {
                    writer.WriteNumber("pathSpeedCmPerSec", prop.Path.SpeedCmPerSec);
                    writer.WriteBoolean("pathClosed", prop.Path.Closed);
                }
                writer.WriteString("outputSpace", SpaceName(prop.Space));
                writer.WriteStartArray("assumptions");
                foreach (string assumption in prop.Assumptions) writer.WriteStringValue(assumption);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static void WriteTrajectory(Utf8JsonWriter writer, LevelDocument document,
            Options options)
        {
            PrepareStates(document);
            int frames = (int)Math.Round(options.Seconds * MotionMath.GameFramesPerSecond) + 1;
            if (frames < 1) frames = 1;
            int stride = Math.Max(1, (int)MotionMath.GameFramesPerSecond / options.Rate);
            List<PropSpec> emitted = new List<PropSpec>();
            foreach (PropSpec prop in document.Props)
            {
                if (!Include(prop, options)) continue;
                if (!prop.Moves) continue;
                emitted.Add(prop);
            }

            List<int> sampleFrames = new List<int>();
            for (int frame = 0; frame < frames; frame += stride) sampleFrames.Add(frame);
            int sampleCount = sampleFrames.Count;

            float[][] positions = new float[emitted.Count][];
            float[][] rotations = new float[emitted.Count][];
            for (int index = 0; index < emitted.Count; index++)
            {
                positions[index] = new float[sampleCount * 3];
                rotations[index] = new float[sampleCount * 4];
            }

            // Frame 0 is the reset pose: a dt = 0 step computes it without advancing, which is
            // what resolves a path class's start point from its spawn position.
            int sample = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                StepFrame(document, frame == 0 ? 0f : 1f);
                if (sample >= sampleCount || sampleFrames[sample] != frame) continue;
                for (int index = 0; index < emitted.Count; index++)
                {
                    Pose pose = WorldPose(document, emitted[index], 0);
                    positions[index][sample * 3] = (float)Math.Round(pose.Position.x, 4);
                    positions[index][sample * 3 + 1] = (float)Math.Round(pose.Position.y, 4);
                    positions[index][sample * 3 + 2] = (float)Math.Round(pose.Position.z, 4);
                    rotations[index][sample * 4] = (float)Math.Round(pose.Qx, 5);
                    rotations[index][sample * 4 + 1] = (float)Math.Round(pose.Qy, 5);
                    rotations[index][sample * 4 + 2] = (float)Math.Round(pose.Qz, 5);
                    rotations[index][sample * 4 + 3] = (float)Math.Round(pose.Qw, 5);
                }
                sample++;
            }

            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("mode", "trajectory");
            writer.WriteString("schema", "katamari.placement/2");
            writer.WriteString("level", document.LevelId);
            writer.WriteString("evaluator", "Tools/motion_sim (compiles MotionMath.cs)");
            writer.WriteNumber("gameFramesPerSecond", MotionMath.GameFramesPerSecond);
            writer.WriteNumber("rate", options.Rate);
            writer.WriteNumber("frames", frames);
            writer.WriteNumber("sampleCount", sampleCount);
            writer.WriteNumber("seconds",
                (float)Math.Round((frames - 1) / MotionMath.GameFramesPerSecond, 4));
            WriteAssumptions(writer);
            WriteCounts(writer, document);
            writer.WriteStartArray("props");
            for (int index = 0; index < emitted.Count; index++)
            {
                PropSpec prop = emitted[index];
                writer.WriteStartObject();
                writer.WriteString("itemId", prop.ItemId);
                writer.WriteString("motionClass", ClassOf(prop));
                writer.WriteString("parent", prop.ParentId);
                writer.WriteBoolean("followsParent",
                    !string.IsNullOrEmpty(prop.ParentId) && prop.Inherit != "none");
                writer.WriteBoolean("stepped", prop.StepOk);
                WriteRoundedArray(writer, "pos", positions[index], positions[index].Length, 4);
                WriteRoundedArray(writer, "rot", rotations[index], rotations[index].Length, 5);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static bool Include(PropSpec prop, Options options)
        {
            if (options.Items.Count == 0) return true;
            return options.Items.Contains(prop.ItemId);
        }

        private static bool IncludeInParams(PropSpec prop, Options options)
        {
            if (options.Items.Count == 0)
            {
                // The editor needs the moving and the parented props; a plain static prop has
                // no motion facts to report and is left out of the payload.
                return ClassOf(prop) != null || !string.IsNullOrEmpty(prop.ParentId);
            }
            return options.Items.Contains(prop.ItemId);
        }

        private static string ClassOf(PropSpec prop)
        {
            if (prop.Motion != null) return prop.Motion.Class;
            if (prop.Behaviour == "attached") return "dragged_along";
            return null;
        }

        private static string SpaceName(OutputSpace space)
        {
            if (space == OutputSpace.SpawnLocal) return "spawn_local";
            return space == OutputSpace.WorldAxis ? "world_axis" : "absolute_world";
        }

        private static void WriteCounts(Utf8JsonWriter writer, LevelDocument document)
        {
            int motionProps = 0;
            int attached = 0;
            int moving = 0;
            Dictionary<string, int> byClass = new Dictionary<string, int>();
            foreach (PropSpec prop in document.Props)
            {
                if (prop.Motion != null) motionProps++;
                if (prop.Behaviour == "attached") attached++;
                if (prop.Moves) moving++;
                string cls = ClassOf(prop);
                if (cls == null) continue;
                int count;
                byClass.TryGetValue(cls, out count);
                byClass[cls] = count + 1;
            }
            writer.WriteStartObject("counts");
            writer.WriteNumber("props", document.Props.Count);
            writer.WriteNumber("motions", document.Motions.Count);
            writer.WriteNumber("paths", document.Paths.Count);
            writer.WriteNumber("motionProps", motionProps);
            writer.WriteNumber("attachedProps", attached);
            writer.WriteNumber("movingProps", moving);
            writer.WriteStartObject("byClass");
            List<string> keys = new List<string>(byClass.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys) writer.WriteNumber(key, byClass[key]);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private static void WriteAssumptions(Utf8JsonWriter writer)
        {
            writer.WriteStartObject("assumptions");
            writer.WriteNumber("sizeGroup", AssumedSizeGroup);
            writer.WriteString("sizeGroupNote", "v2 carries no original size id; every Cancer "
                + "motion prop is original size 2, i.e. size group 0");
            writer.WriteString("observer", "none: the level preview has no player, so "
                + "WaitForPlayer reports its radius and never triggers");
            writer.WriteString("hostQueries", "ground height, zone membership and the "
                + "forward-blocked / allowed-turn rays are not modelled; walkers and clouds "
                + "move as if every step and every turn were allowed");
            writer.WriteString("phase", "PhaseSeed.Hash32(level.id + \"/\" + itemId) from "
                + "Runtime/Pickup/PhaseSeed.cs; a props[].phase in the document is not applied");
            writer.WriteString("composition", "world = spawn * local; a child's placement is "
                + "rigid relative to its parent and its own motion is local on top; the "
                + "absolute-world classes ignore the parent link");
            writer.WriteString("stepping", "every prop is stepped at dt = 1 game frame (30 Hz) "
                + "and sampled at the requested rate; a 30 Hz sample series reproduces the "
                + "MotionMath tests' hand values");
            writer.WriteEndObject();
        }

        private static void WriteRoundedArray(Utf8JsonWriter writer, string name, float[] values,
            int count, int digits)
        {
            writer.WriteStartArray(name);
            for (int index = 0; index < count; index++)
            {
                writer.WriteNumberValue((float)Math.Round(values[index], digits));
            }
            writer.WriteEndArray();
        }
    }
}
