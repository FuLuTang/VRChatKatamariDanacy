using System;
using System.Collections.Generic;
using System.Globalization;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// The arithmetic of island placement for the all-maps hub: adding an island offset to a
    /// point or a flat list of points, how far apart two islands' bounding boxes are, whether
    /// a box stays inside the coordinate range float precision is good for, and what makes an
    /// offset legal. Pure and static, with no Unity types, so the rules are testable without a
    /// scene.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why float arrays and not <c>Vector3</c>. The editor code that moves objects reads and
    /// writes <c>Vector3</c>, but every rule here is a number rule (is the Y offset zero, is the
    /// gap at least 1000 m), and a <c>float[3]</c> keeps this file free of the engine so the
    /// local EditMode runner can execute its tests.
    /// </para>
    /// <para>
    /// Why the Y offset must be zero. An island is moved by its shell's position, so everything
    /// that follows the transform follows for free. The numbers a component stores about the
    /// ground (<c>LevelArenaHost.groundOffset</c>, <c>killPlaneY</c>, <c>PropFallHost.floorY</c>)
    /// are Y scalars that do not follow, and the scene descriptor's respawn height is global.
    /// With a zero Y offset none of them needs rewriting; a non-zero one would need all of
    /// them, so it is refused instead (<see cref="IsOffsetValid"/>).
    /// </para>
    /// <para>
    /// Why 1000 m and 1300 m. Islands further apart than the default far clip plane (1000 m)
    /// cannot see each other; float spacing at 1300 m is about 1.2e-4 m, far below the
    /// ball's 0.005 m contact skin. Both are the working values of the all-maps design, not
    /// measured limits.
    /// </para>
    /// </remarks>
    public static class IslandMath
    {
        /// <summary>The shell object every taken subtree is parented under.</summary>
        public const string ShellPrefix = "Island_";

        /// <summary>Smallest allowed gap between two islands' bounding boxes, metres.</summary>
        public const float MinIslandGapM = 1000f;

        /// <summary>Largest allowed absolute world coordinate of any island's bounds, metres.</summary>
        public const float MaxAbsCoordinateM = 1300f;

        /// <summary>Two offsets closer than this on every axis count as the same place, metres.</summary>
        public const float SameOffsetToleranceM = 1e-3f;

        /// <summary>The shell name for a level id (<c>Island_&lt;id&gt;</c>).</summary>
        public static string ShellName(string levelId)
        {
            return ShellPrefix + (levelId ?? "");
        }

        /// <summary>
        /// Whether <paramref name="offset"/> is a legal island offset: three finite numbers
        /// with a Y of exactly zero. <paramref name="reason"/> says what is wrong when it is not.
        /// </summary>
        public static bool IsOffsetValid(float[] offset, out string reason)
        {
            reason = null;
            if (offset == null || offset.Length != 3)
            {
                reason = "an island offset is [x, y, z], three numbers";
                return false;
            }
            for (int i = 0; i < 3; i++)
            {
                if (!IsFinite(offset[i]))
                {
                    reason = "an island offset must be finite numbers";
                    return false;
                }
            }
            if (offset[1] != 0f)
            {
                reason = "the island offset's Y must be 0 (ground heights, kill planes and the"
                    + " respawn height are Y scalars that do not move with the island)";
                return false;
            }
            return true;
        }

        /// <summary>Component-wise <c>a + b</c> of two 3-vectors; null when either is not 3 numbers.</summary>
        public static float[] Add(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != 3 || b.Length != 3) return null;
            return new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        }

        /// <summary>
        /// Adds <paramref name="offset"/> to every triple of a flat x,y,z list, in place, and
        /// returns the number of triples moved. Returns -1 and changes nothing when the offset
        /// is not three numbers or the list length is not a multiple of three (a list that
        /// ended mid-triple is damaged data, and half-shifting it would hide that).
        /// </summary>
        public static int TranslateTriples(float[] flatXyz, float[] offset)
        {
            if (flatXyz == null || offset == null || offset.Length != 3) return -1;
            if (flatXyz.Length % 3 != 0) return -1;
            int count = flatXyz.Length / 3;
            for (int i = 0; i < count; i++)
            {
                flatXyz[i * 3] += offset[0];
                flatXyz[i * 3 + 1] += offset[1];
                flatXyz[i * 3 + 2] += offset[2];
            }
            return count;
        }

        /// <summary>
        /// The box <paramref name="min"/>..<paramref name="max"/> moved by <paramref name="offset"/>.
        /// False (outputs null) when any argument is not three numbers.
        /// </summary>
        public static bool TranslateBox(float[] min, float[] max, float[] offset,
            out float[] movedMin, out float[] movedMax)
        {
            movedMin = Add(min, offset);
            movedMax = Add(max, offset);
            if (movedMin == null || movedMax == null)
            {
                movedMin = null;
                movedMax = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// The distance between two axis-aligned boxes: 0 when they touch or overlap, otherwise
        /// the length of the shortest segment between them. Returns -1 for an invalid box (not
        /// three numbers, or a min above its max).
        /// </summary>
        public static float BoxGap(float[] minA, float[] maxA, float[] minB, float[] maxB)
        {
            if (!IsBox(minA, maxA) || !IsBox(minB, maxB)) return -1f;
            double sum = 0.0;
            for (int axis = 0; axis < 3; axis++)
            {
                double gap = 0.0;
                if (minB[axis] > maxA[axis]) gap = (double)minB[axis] - maxA[axis];
                else if (minA[axis] > maxB[axis]) gap = (double)minA[axis] - maxB[axis];
                sum += gap * gap;
            }
            return (float)Math.Sqrt(sum);
        }

        /// <summary>Largest absolute coordinate of a box (what the float-precision limit applies to); -1 when invalid.</summary>
        public static float MaxAbsCoordinate(float[] min, float[] max)
        {
            if (!IsBox(min, max)) return -1f;
            float best = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                best = Math.Max(best, Math.Abs(min[axis]));
                best = Math.Max(best, Math.Abs(max[axis]));
            }
            return best;
        }

        /// <summary>Whether every coordinate of the box is within <paramref name="limitM"/> of the origin.</summary>
        public static bool IsWithinCoordinateRange(float[] min, float[] max, float limitM)
        {
            float largest = MaxAbsCoordinate(min, max);
            return largest >= 0f && largest <= limitM;
        }

        /// <summary>
        /// The first pair of boxes (lowest first index, then lowest second index) whose gap is
        /// below <paramref name="minGapM"/>; false when every pair is far enough apart.
        /// An invalid box counts as "too close to everything" so damaged bounds cannot pass.
        /// </summary>
        public static bool FindTooClosePair(float[][] mins, float[][] maxs, float minGapM,
            out int first, out int second, out float gap)
        {
            first = -1;
            second = -1;
            gap = 0f;
            if (mins == null || maxs == null) return false;
            int count = Math.Min(mins.Length, maxs.Length);
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    float measured = BoxGap(mins[i], maxs[i], mins[j], maxs[j]);
                    if (measured < 0f || measured < minGapM)
                    {
                        first = i;
                        second = j;
                        gap = measured;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// The index of the first offset that repeats an earlier one (every axis within
        /// <see cref="SameOffsetToleranceM"/>), or -1. Two islands at one place would overlap.
        /// </summary>
        public static int FirstDuplicateOffset(float[][] offsets)
        {
            if (offsets == null) return -1;
            for (int i = 1; i < offsets.Length; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    if (SameOffset(offsets[i], offsets[j])) return i;
                }
            }
            return -1;
        }

        /// <summary>Whether two offsets are the same place within <see cref="SameOffsetToleranceM"/>.</summary>
        public static bool SameOffset(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != 3 || b.Length != 3) return false;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(a[axis] - b[axis]) > SameOffsetToleranceM) return false;
            }
            return true;
        }

        /// <summary>
        /// A number as report text: invariant culture, at most three decimals, never "-0".
        /// Reports are compared character for character between runs, so the formatting must
        /// not depend on the machine's locale.
        /// </summary>
        public static string Num(float value)
        {
            if (value == 0f) value = 0f;
            string text = value.ToString("0.###", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        /// <summary>A 3-vector as report text, <c>(x, y, z)</c> (<c>(none)</c> for null).</summary>
        public static string Format(float[] v)
        {
            if (v == null || v.Length != 3) return "(none)";
            return "(" + Num(v[0]) + ", " + Num(v[1]) + ", " + Num(v[2]) + ")";
        }

        private static bool IsBox(float[] min, float[] max)
        {
            if (min == null || max == null || min.Length != 3 || max.Length != 3) return false;
            for (int axis = 0; axis < 3; axis++)
            {
                if (!IsFinite(min[axis]) || !IsFinite(max[axis])) return false;
                if (min[axis] > max[axis]) return false;
            }
            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>
    /// How a value read from an Udon variable table is shown in a report and judged. No
    /// engine types, so the rules are testable without a scene.
    /// </summary>
    public static class UdonValueText
    {
        /// <summary>The value as report text: <c>missing</c> when the symbol is not in the table, otherwise the value (<c>null</c> for null).</summary>
        public static string Describe(bool found, object value)
        {
            if (!found) return "missing";
            if (value == null) return "null";
            return value.ToString();
        }

        /// <summary>Whether the table has the symbol and holds a bool equal to <paramref name="want"/> (a value of another type never matches).</summary>
        public static bool IsBool(bool found, object value, bool want)
        {
            return found && value is bool && (bool)value == want;
        }
    }

    /// <summary>What an island move does to a serialized world-coordinate field.</summary>
    public enum WorldFieldClass
    {
        /// <summary>Holds world positions; the importer adds the island offset to it.</summary>
        ShiftedWithIsland,

        /// <summary>Sizes, local-space values or offsets relative to the object: nothing to shift.</summary>
        SizeOrLocal,

        /// <summary>A direction (a normal, an axis): translation does not change it.</summary>
        Direction,

        /// <summary>
        /// Holds world positions, but its component is never moved into an island (a single
        /// ball motor, the lab's driver and cells); the importer refuses to move one.
        /// </summary>
        NotImported,

        /// <summary>Written by the all-maps assembler with the island offset already added.</summary>
        AuthoredWithOffset,
    }

    /// <summary>
    /// The classification of every serialized world-coordinate-looking field in the project:
    /// <c>Vector3</c>, <c>Vector3[]</c>, <c>Quaternion</c>, <c>Vector2</c> and <c>Vector2[]</c>
    /// fields, and <c>float[]</c> fields whose name says point, centre, position or world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this list exists. Moving an island only shifts what follows a <c>Transform</c>; a
    /// position stored inside a component stays where it was authored and the island then
    /// plays about 1200 m from its own data (a ball that falls through the ground, or is held
    /// up by terrain that is not there). The importer therefore shifts the fields classified
    /// <see cref="WorldFieldClass.ShiftedWithIsland"/>, and a test enumerates every such field
    /// in the project and fails when one is missing here, so adding a component field that
    /// holds a position cannot silently become a bug on an offset island.
    /// </para>
    /// <para>
    /// Keys are <c>TypeName.fieldName</c> (the simple type name). A stale key (a renamed or
    /// removed field) also fails the test, so the list cannot rot.
    /// </para>
    /// </remarks>
    public static class IslandWorldFields
    {
        public const string TerrainVertices = "TerrainFieldHost.vertices";
        public const string MotionPathPoints = "MotionHost.pathPoints";

        private static readonly KeyValuePair<string, WorldFieldClass>[] Table =
        {
            // Shifted by the importer: world metres stored in the component.
            P(TerrainVertices, WorldFieldClass.ShiftedWithIsland),
            P(MotionPathPoints, WorldFieldClass.ShiftedWithIsland),

            // Sizes and local-space data (full edge lengths, shapes in the prop's own space).
            P("PickupFieldHost.propBounds", WorldFieldClass.SizeOrLocal),
            P("PickupFieldHost.propLocalBounds", WorldFieldClass.SizeOrLocal),
            P("PickupFieldHost.propProxyCenters", WorldFieldClass.SizeOrLocal),
            P("PickupFieldHost.propProxySizes", WorldFieldClass.SizeOrLocal),
            P("PickupFieldHost.propProxyHullPoints", WorldFieldClass.SizeOrLocal),
            P("PhysicsTestCell.footprintSize", WorldFieldClass.SizeOrLocal),
            P("PhysicsTestCell.deckHalfSizeM", WorldFieldClass.SizeOrLocal),
            P("LabBallSizeSetter.feederBounds", WorldFieldClass.SizeOrLocal),
            P("PhysicsTestFieldDriver.chaseOffset", WorldFieldClass.SizeOrLocal),

            // Directions.
            P("LevelArenaHost.groundNormal", WorldFieldClass.Direction),
            P("KatamariBallMotor.groundPlaneNormal", WorldFieldClass.Direction),
            P("CpuSkirmishBootstrap.groundNormal", WorldFieldClass.Direction),
            P("PhysicsTestCell.rampProbeNormal", WorldFieldClass.Direction),
            P("PhysicsTestCell.rampProbeRight", WorldFieldClass.Direction),

            // World positions on components an island never takes.
            P("KatamariBallMotor.startCenterWorld", WorldFieldClass.NotImported),
            P("PhysicsTestFieldDriver.sandboxSpawn", WorldFieldClass.NotImported),
            P("PhysicsTestCell.startCenter", WorldFieldClass.NotImported),
            P("PhysicsTestCell.footprintCenter", WorldFieldClass.NotImported),
            P("PhysicsTestCell.deckCenterXZ", WorldFieldClass.NotImported),
            P("PhysicsTestCell.rampProbeCenter", WorldFieldClass.NotImported),

            // Written by the assembler with the offset included.
            P("LevelSwitchHost.startCenters", WorldFieldClass.AuthoredWithOffset),
        };

        /// <summary>
        /// The component types whose world positions the importer does not shift; a taken
        /// subtree must not contain one (<see cref="WorldFieldClass.NotImported"/>).
        /// </summary>
        public static readonly string[] NotImportedTypes =
        {
            "KatamariBallMotor",
            "PhysicsTestFieldDriver",
            "PhysicsTestCell",
        };

        private static KeyValuePair<string, WorldFieldClass> P(string key, WorldFieldClass cls)
        {
            return new KeyValuePair<string, WorldFieldClass>(key, cls);
        }

        /// <summary>The key of a field, <c>TypeName.fieldName</c>.</summary>
        public static string Key(string typeName, string fieldName)
        {
            return typeName + "." + fieldName;
        }

        /// <summary>The class of a field, or false when it is not classified.</summary>
        public static bool TryClassify(string key, out WorldFieldClass cls)
        {
            for (int i = 0; i < Table.Length; i++)
            {
                if (string.Equals(Table[i].Key, key, StringComparison.Ordinal))
                {
                    cls = Table[i].Value;
                    return true;
                }
            }
            cls = WorldFieldClass.SizeOrLocal;
            return false;
        }

        /// <summary>Every classified key, in table order.</summary>
        public static string[] AllKeys()
        {
            var keys = new string[Table.Length];
            for (int i = 0; i < Table.Length; i++) keys[i] = Table[i].Key;
            return keys;
        }

        /// <summary>Whether a component type (simple name) is one an island must not take.</summary>
        public static bool IsNotImportedType(string typeName)
        {
            for (int i = 0; i < NotImportedTypes.Length; i++)
            {
                if (string.Equals(NotImportedTypes[i], typeName, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
