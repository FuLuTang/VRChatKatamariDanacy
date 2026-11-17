using System;
using System.Collections.Generic;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// The columns <see cref="MotionHost"/> serializes, as plain arrays: what the document says
    /// about moving props, resolved once so the runtime reads numbers and not tables.
    /// </summary>
    public sealed class MotionRowTable
    {
        public string[] ItemIds = new string[0];
        public int[] ParentRow = new int[0];
        public int[] Inherit = new int[0];
        public int[] MotionClass = new int[0];
        public int[] PhaseSeed = new int[0];
        public float[] UpdatesPerSecond = new float[0];
        public float[] HalfHeightM = new float[0];
        public float[] SpeedMps = new float[0];
        public int[] SizeGroup = new int[0];
        public float[] TriggerRadiusM = new float[0];
        public int[] MotionIdCode = new int[0];
        public bool[] Loop = new bool[0];
        public bool[] SwayTranslation = new bool[0];
        public int[] PathIndex = new int[0];

        public int[] PathPointStart = new int[0];
        public int[] PathPointCount = new int[0];
        public bool[] PathClosed = new bool[0];
        public float[] PathPoints = new float[0];

        /// <summary>Rows with an own program (class other than none).</summary>
        public int OwnProgramRows;
        /// <summary>Rows that only ride a moving parent.</summary>
        public int RiderRows;
        /// <summary>Rows that exist only to run an Animator.</summary>
        public int AnimationOnlyRows;

        public int RowCount { get { return ItemIds.Length; } }

        /// <summary>Per-class row counts, indexed by <see cref="MotionHostMath"/> class code.</summary>
        public int[] ClassCounts = new int[MotionHostMath.ClassCount];

        /// <summary>Problems (errors) and provisional-value notes the importer prints.</summary>
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Builds the <see cref="MotionRowTable"/> of a v2 document. Pure C#: no Unity object, no
    /// scene, so the whole decision (which props get a row, in what order, with which numbers)
    /// is testable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A prop gets a row when it has a motion program, when it rides a moving parent chain
    /// (13 of Cancer's 15 attached props do not: their parent is static), or when it carries
    /// an Animator (<paramref name="animatedItemIds"/>). That is the same "moves" rule the
    /// editor's motion preview uses (<c>Tools/motion_sim</c>), so the preview and the game agree
    /// on what moves. Rows are ordered parents first (a stable sort by parent depth), which is
    /// what lets the host compose a rider from a parent it already wrote this frame.
    /// </para>
    /// <para>
    /// Phase (D-12): <c>props[].phase</c> when the author wrote one, through
    /// <see cref="MotionMath.SeedForPhase01"/>; otherwise <see cref="PhaseSeed.From"/> of
    /// <c>meta.id</c> and the item id. Nothing here reads a clock or a random source.
    /// </para>
    /// <para>
    /// No new JSON key is introduced. <c>params.triggerRadiusM</c> is the key LEVEL_FORMAT_V2
    /// 6.7.3 already names for WaitForPlayer's gate; the validator accepts any finite numeric
    /// <c>params</c> value, so the key and its checker are already in step. Other <c>params</c>
    /// (<c>degPerSec</c>, <c>periodSec</c>, ...) are not applied by MotionMath; each is listed
    /// as a note, not silently dropped.
    /// </para>
    /// </remarks>
    public static class PlacementMotionRows
    {
        private const int MaxChain = 32;

        public static MotionRowTable Build(PlacementV2Document doc, ISet<string> animatedItemIds)
        {
            var table = new MotionRowTable();
            if (doc == null || doc.Props == null) return table;

            string levelId = doc.Meta == null ? "" : doc.Meta.Id;
            var propById = new Dictionary<string, PlacementV2Prop>(StringComparer.Ordinal);
            for (int i = 0; i < doc.Props.Length; i++)
            {
                PlacementV2Prop prop = doc.Props[i];
                if (prop != null && !string.IsNullOrEmpty(prop.ItemId)) propById[prop.ItemId] = prop;
            }

            var motionById = new Dictionary<string, PlacementV2Motion>(StringComparer.Ordinal);
            for (int i = 0; i < doc.Motions.Length; i++)
            {
                PlacementV2Motion motion = doc.Motions[i];
                if (motion != null && !string.IsNullOrEmpty(motion.Id)) motionById[motion.Id] = motion;
            }

            var pathById = new Dictionary<string, PlacementV2Path>(StringComparer.Ordinal);
            for (int i = 0; i < doc.Paths.Length; i++)
            {
                PlacementV2Path path = doc.Paths[i];
                if (path != null && !string.IsNullOrEmpty(path.Id)) pathById[path.Id] = path;
            }

            var speedIndexByDef = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < doc.Defs.Length; i++)
            {
                PlacementV2Def def = doc.Defs[i];
                if (def != null && !string.IsNullOrEmpty(def.Id)) speedIndexByDef[def.Id] = def.SpeedIndex;
            }

            // Pass 1: which props move by themselves.
            var ownMotion = new Dictionary<string, PlacementV2Motion>(StringComparer.Ordinal);
            for (int i = 0; i < doc.Props.Length; i++)
            {
                PlacementV2Prop prop = doc.Props[i];
                if (prop == null || string.IsNullOrEmpty(prop.Motion)) continue;
                PlacementV2Motion motion;
                if (!motionById.TryGetValue(prop.Motion, out motion))
                {
                    table.Errors.Add("[prop_motion] prop '" + prop.ItemId + "' names motion '" +
                                     prop.Motion + "', which is not in motions[]; it stays static");
                    continue;
                }
                if (MotionHostMath.ClassFromName(motion.Class) == MotionHostMath.ClassNone ||
                    MotionHostMath.ClassFromName(motion.Class) == MotionHostMath.ClassDraggedAlong)
                {
                    if (!string.Equals(motion.Class, "dragged_along", StringComparison.Ordinal))
                    {
                        table.Errors.Add("[motion_class] motion '" + motion.Id + "' has class '" +
                                         motion.Class + "', which the host does not know");
                    }
                    continue;
                }
                ownMotion[prop.ItemId] = motion;
            }

            // Pass 2: the rows (own program, rides a moving chain, or animated).
            var included = new List<PlacementV2Prop>();
            var kindOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < doc.Props.Length; i++)
            {
                PlacementV2Prop prop = doc.Props[i];
                if (prop == null || string.IsNullOrEmpty(prop.ItemId)) continue;
                bool own = ownMotion.ContainsKey(prop.ItemId);
                bool rides = !own && RidesMovingChain(prop, propById, ownMotion, 0);
                bool animated = animatedItemIds != null && animatedItemIds.Contains(prop.ItemId);
                if (!own && !rides && !animated) continue;
                included.Add(prop);
                kindOf[prop.ItemId] = own ? 0 : (rides ? 1 : 2);
                if (own) table.OwnProgramRows++;
                else if (rides) table.RiderRows++;
                else table.AnimationOnlyRows++;
            }

            // Parents first: stable sort by chain depth among included rows.
            var depth = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < included.Count; i++)
            {
                depth[included[i].ItemId] = ChainDepth(included[i], propById, kindOf, 0);
            }
            var order = new List<PlacementV2Prop>(included);
            order.Sort(delegate (PlacementV2Prop a, PlacementV2Prop b)
            {
                int c = depth[a.ItemId].CompareTo(depth[b.ItemId]);
                if (c != 0) return c;
                return included.IndexOf(a).CompareTo(included.IndexOf(b));
            });

            var rowOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < order.Count; i++) rowOf[order[i].ItemId] = i;

            int n = order.Count;
            table.ItemIds = new string[n];
            table.ParentRow = new int[n];
            table.Inherit = new int[n];
            table.MotionClass = new int[n];
            table.PhaseSeed = new int[n];
            table.UpdatesPerSecond = new float[n];
            table.HalfHeightM = new float[n];
            table.SpeedMps = new float[n];
            table.SizeGroup = new int[n];
            table.TriggerRadiusM = new float[n];
            table.MotionIdCode = new int[n];
            table.Loop = new bool[n];
            table.SwayTranslation = new bool[n];
            table.PathIndex = new int[n];

            var pathOrder = new List<PlacementV2Path>();
            var pathRow = new Dictionary<string, int>(StringComparer.Ordinal);
            var noted = new HashSet<string>(StringComparer.Ordinal);

            for (int r = 0; r < n; r++)
            {
                PlacementV2Prop prop = order[r];
                table.ItemIds[r] = prop.ItemId;
                table.PathIndex[r] = -1;
                table.MotionIdCode[r] = -1;
                table.SwayTranslation[r] = true;

                int parentRow = -1;
                int inherit = MotionHostMath.InheritFromName(prop.Inherit);
                int row;
                if (prop.HasParent && !string.IsNullOrEmpty(prop.Parent) && inherit != 0 &&
                    rowOf.TryGetValue(prop.Parent, out row) && row < r)
                {
                    parentRow = row;
                }
                table.ParentRow[r] = parentRow;
                table.Inherit[r] = parentRow >= 0 ? inherit : MotionHostMath.InheritNone;

                table.PhaseSeed[r] = prop.HasPhase
                    ? MotionMath.SeedForPhase01(prop.Phase)
                    : PhaseSeed.From(levelId, prop.ItemId);
                table.HalfHeightM[r] = (prop.Aabb != null && prop.Aabb.Length > 1)
                    ? Math.Abs(prop.Aabb[1]) * 0.5f : 0f;
                // v2 carries no original size id: every Cancer motion prop is size 2 = group 0.
                table.SizeGroup[r] = 0;

                PlacementV2Motion program;
                if (!ownMotion.TryGetValue(prop.ItemId, out program))
                {
                    table.MotionClass[r] = MotionHostMath.ClassNone;
                    continue;
                }

                int cls = MotionHostMath.ClassFromName(program.Class);
                table.MotionClass[r] = cls;
                table.ClassCounts[cls]++;
                table.UpdatesPerSecond[r] = program.UpdatesPerSecond > 0f
                    ? program.UpdatesPerSecond : MotionHostMath.DefaultUpdatesPerSecond(cls);
                table.Loop[r] = program.Loop;
                if (cls == MotionHostMath.ClassWaitForPlayer)
                {
                    table.MotionIdCode[r] = program.ProvenanceMotionId;
                    table.TriggerRadiusM[r] = MotionMath.PanicRadiusM(table.SizeGroup[r]);
                    float declared;
                    if (TryParam(program, "triggerRadiusM", out declared) && declared > 0f)
                    {
                        table.TriggerRadiusM[r] = declared;
                    }
                }
                if (cls == MotionHostMath.ClassSway)
                {
                    string def = (prop.Def ?? "").ToLowerInvariant();
                    bool balanced = def.Contains("balancedoll") || def.Contains("balance_doll") ||
                                    def.Contains("roly");
                    table.SwayTranslation[r] = !balanced;
                }

                PlacementV2Path path = null;
                if (!string.IsNullOrEmpty(program.Path))
                {
                    if (!pathById.TryGetValue(program.Path, out path))
                    {
                        table.Errors.Add("[motion_path] motion '" + program.Id + "' names path '" +
                                         program.Path + "', which is not in paths[]; '" + prop.ItemId +
                                         "' stays static");
                        table.MotionClass[r] = MotionHostMath.ClassNone;
                        table.ClassCounts[cls]--;
                        continue;
                    }
                    int index;
                    if (!pathRow.TryGetValue(path.Id, out index))
                    {
                        index = pathOrder.Count;
                        pathOrder.Add(path);
                        pathRow[path.Id] = index;
                    }
                    table.PathIndex[r] = index;
                }
                else if (MotionHostMath.IsPathClass(cls))
                {
                    table.Errors.Add("[motion_path] '" + prop.ItemId + "' is a " + program.Class +
                                     " without a path; it stays static");
                    table.MotionClass[r] = MotionHostMath.ClassNone;
                    table.ClassCounts[cls]--;
                    continue;
                }

                int speedIndex;
                if (!speedIndexByDef.TryGetValue(prop.Def ?? "", out speedIndex)) speedIndex = -1;
                bool hasPathSpeed = path != null && path.HasSpeedCmPerSec;
                table.SpeedMps[r] = MotionHostMath.ResolveSpeedMps(
                    MotionHostMath.SpeedSourceFromName(program.SpeedSource),
                    path == null ? 0f : path.SpeedCmPerSec, hasPathSpeed,
                    program.HasSpeedCmPerSec ? program.SpeedCmPerSec : 0f, speedIndex);
                bool needsSpeed = cls == MotionHostMath.ClassSporadicWalk || cls == MotionHostMath.ClassClouds ||
                                  MotionHostMath.IsPathClass(cls);
                if (needsSpeed && !(table.SpeedMps[r] > 0f))
                {
                    table.Errors.Add("[motion_speed] '" + prop.ItemId + "' (" + program.Class +
                                     ") resolves to 0 m/s: no path speed, explicit speed or def speedIndex");
                }

                for (int k = 0; k < program.ParamCount; k++)
                {
                    string key = program.ParamKeys[k];
                    if (key == "triggerRadiusM" && cls == MotionHostMath.ClassWaitForPlayer) continue;
                    string note = "motion '" + program.Id + "' declares params." + key +
                                  ", which MotionMath does not apply (the class runs on its research constants)";
                    if (noted.Add(note)) table.Notes.Add(note);
                }
            }

            // Path columns.
            int totalPoints = 0;
            for (int p = 0; p < pathOrder.Count; p++) totalPoints += pathOrder[p].Points.Length;
            table.PathPointStart = new int[pathOrder.Count];
            table.PathPointCount = new int[pathOrder.Count];
            table.PathClosed = new bool[pathOrder.Count];
            table.PathPoints = new float[totalPoints * 3];
            int cursor = 0;
            for (int p = 0; p < pathOrder.Count; p++)
            {
                PlacementV2Path path = pathOrder[p];
                table.PathPointStart[p] = cursor;
                table.PathPointCount[p] = path.Points.Length;
                // 6.7.4: the reference level leaves loop out; the class decides (the paths wrap
                // unless the author said "once"; back_and_forth folds by definition).
                table.PathClosed[p] = !string.Equals(path.LoopMode, "once", StringComparison.Ordinal);
                for (int k = 0; k < path.Points.Length; k++)
                {
                    float[] point = path.Points[k];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        table.PathPoints[(cursor + k) * 3 + axis] = point[axis];
                    }
                }
                cursor += path.Points.Length;
            }

            AddProvisionalNotes(table);
            return table;
        }

        private static bool TryParam(PlacementV2Motion motion, string key, out float value)
        {
            value = 0f;
            for (int i = 0; i < motion.ParamCount && i < motion.ParamValues.Length; i++)
            {
                if (string.Equals(motion.ParamKeys[i], key, StringComparison.Ordinal))
                {
                    value = motion.ParamValues[i];
                    return true;
                }
            }
            return false;
        }

        private static bool RidesMovingChain(PlacementV2Prop prop,
            Dictionary<string, PlacementV2Prop> propById,
            Dictionary<string, PlacementV2Motion> ownMotion, int depth)
        {
            if (depth >= MaxChain) return false;
            if (!prop.HasParent || string.IsNullOrEmpty(prop.Parent)) return false;
            if (MotionHostMath.InheritFromName(prop.Inherit) == MotionHostMath.InheritNone) return false;
            PlacementV2Prop parent;
            if (!propById.TryGetValue(prop.Parent, out parent) || ReferenceEquals(parent, prop)) return false;
            if (ownMotion.ContainsKey(parent.ItemId)) return true;
            return RidesMovingChain(parent, propById, ownMotion, depth + 1);
        }

        private static int ChainDepth(PlacementV2Prop prop, Dictionary<string, PlacementV2Prop> propById,
            Dictionary<string, int> includedKinds, int depth)
        {
            if (depth >= MaxChain) return depth;
            if (!prop.HasParent || string.IsNullOrEmpty(prop.Parent)) return 0;
            PlacementV2Prop parent;
            if (!propById.TryGetValue(prop.Parent, out parent) || ReferenceEquals(parent, prop)) return 0;
            if (!includedKinds.ContainsKey(parent.ItemId)) return 0;
            return 1 + ChainDepth(parent, propById, includedKinds, depth + 1);
        }

        // The classes whose constants the research marks PROVISIONAL show up in every import.
        private static void AddProvisionalNotes(MotionRowTable table)
        {
            string[] names = { "", "spin", "flip", "bob", "sway", "whack_a_mole", "hop", "sporadic_walk",
                "clouds", "wait_for_player", "path_collision", "sloping_path", "back_and_forth", "" };
            int[] provisional = { MotionHostMath.ClassSpin, MotionHostMath.ClassFlip,
                MotionHostMath.ClassWhackAMole, MotionHostMath.ClassHop, MotionHostMath.ClassSporadicWalk,
                MotionHostMath.ClassClouds, MotionHostMath.ClassWaitForPlayer,
                MotionHostMath.ClassPathCollision, MotionHostMath.ClassSlopingPath };
            for (int i = 0; i < provisional.Length; i++)
            {
                int count = table.ClassCounts[provisional[i]];
                if (count > 0)
                {
                    table.Notes.Add("PROVISIONAL " + names[provisional[i]] + " x" + count +
                                    ": constants are the research's reference values, not measured (see MotionMath remarks)");
                }
            }
        }
    }
}
