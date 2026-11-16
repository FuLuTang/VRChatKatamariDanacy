using System;
using System.Collections.Generic;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>Which of D-5's layers a prop's authored <c>scale</c> lands in.</summary>
    public enum PlacementScaleLayer
    {
        /// <summary>No scale written, or (1,1,1): nothing to do.</summary>
        Unit = 0,
        /// <summary>Uniform scale: allowed for any behaviour and any parent (D-5 layer 2).</summary>
        Uniform = 1,
        /// <summary>Non-uniform with a box/sphere proxy, no parent, static (D-5 layer 3).</summary>
        NonUniformAllowed = 2,
        /// <summary>
        /// Non-uniform with NO declared collider: allowed, because there is no proxy to
        /// distort. The reference level's one non-uniform instance is exactly this case
        /// (<c>sparrow_001</c>, <c>absorbable:false</c>, def without a collider).
        /// </summary>
        NonUniformNoProxy = 3,
        /// <summary>The scale itself is unusable (non-positive, non-finite, wrong length).</summary>
        Invalid = 4,
        /// <summary>Violates D-5 layer 3 (parented, or not static, or a capsule/convex proxy).</summary>
        Refused = 5
    }

    /// <summary>
    /// The v2 design's decisions as pure functions: no scene, no <c>UnityEngine</c>, no asset
    /// database. Everything the importer decides BEFORE it touches a GameObject lives here, so
    /// each rule has one implementation and an EditMode test that calls it directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four rules this file owns, and where each comes from:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>D-5 scale layers</b> (<c>LEVEL_FORMAT_V2</c> §6.4/§18): runtime
    /// never scales, authoring may scale uniformly anywhere, non-uniform only for an unparented
    /// static box/sphere. The reference data's <c>(0.0741,0.0291,0.0197)</c> has no collider at
    /// all, so "no proxy to distort" is allowed and reported as its own layer instead of being
    /// bent into the box/sphere clause.</description></item>
    /// <item><description><b>Parent graph</b> (§6.5): single parent, must exist, no self-edge,
    /// no cycle. Returns the <c>int[] propParent</c> shape a future <c>PropGroupHost</c> reads,
    /// so the data is already in the runtime's form even though this round only builds the
    /// visual hierarchy (owner ruling A).</description></item>
    /// <item><description><b>Growth key priority</b> (owner ruling B): <c>growthMm3</c> is the
    /// authority; <c>volumeMm3</c> is read only when it is absent, and when both are written
    /// they must satisfy <c>growthMm3 == round(volumeMm3 * penalty)</c> - the exact relation
    /// the Python writer uses (<c>placement_core.item_growth_mm3</c>), with .NET's default
    /// banker's rounding matching Python's <c>round()</c>.</description></item>
    /// <item><description><b>Activity walls</b> (§5.2/§5.3 + owner ruling F): a closed polyline
    /// becomes one infinite vertical half-space per edge, normal pointing INTO the play area.
    /// A plane cannot represent a concave corner, so a non-convex closed polygon is a hard
    /// error instead of a silently wrong arena.</description></item>
    /// </list>
    /// <para>
    /// Nothing here mutates the document: every function takes the model and returns a verdict,
    /// a segment list or an error string. The importer decides what to write.
    /// </para>
    /// </remarks>
    public static class PlacementV2Rules
    {
        /// <summary>Two scale components closer than this are "uniform" (scales are order 1).</summary>
        public const float ScaleUniformEpsilon = 1e-4f;

        /// <summary>A closed polygon thinner than this has no interior and no inside direction.</summary>
        public const float PolygonAreaEpsilon = 1e-9f;

        /// <summary>Shorter edges than this are a duplicated point, not a wall segment.</summary>
        public const float MinSegmentLengthM = 1e-6f;

        /// <summary>§6.4's penalty default when neither the prop nor its def declares one.</summary>
        public const float DefaultVolumePenalty = 0.8f;

        public const string DefaultBehaviour = "static";

        // ------------------------------------------------------------------ scale ---

        public static bool IsUnitScale(float[] scale)
        {
            if (scale == null || scale.Length != 3) return true;
            return Math.Abs(scale[0] - 1f) <= ScaleUniformEpsilon &&
                   Math.Abs(scale[1] - 1f) <= ScaleUniformEpsilon &&
                   Math.Abs(scale[2] - 1f) <= ScaleUniformEpsilon;
        }

        /// <summary>
        /// True when the three components agree to <see cref="ScaleUniformEpsilon"/>.
        /// An absent scale counts as uniform (it means 1,1,1).
        /// </summary>
        public static bool IsUniformScale(float[] scale)
        {
            if (scale == null || scale.Length != 3) return true;
            float largest = Math.Max(Math.Abs(scale[0]), Math.Max(Math.Abs(scale[1]),
                Math.Abs(scale[2])));
            float tolerance = ScaleUniformEpsilon * Math.Max(1f, largest);
            return Math.Abs(scale[0] - scale[1]) <= tolerance &&
                   Math.Abs(scale[1] - scale[2]) <= tolerance &&
                   Math.Abs(scale[0] - scale[2]) <= tolerance;
        }

        /// <summary>Which D-5 layer the prop's scale lands in.</summary>
        public static PlacementScaleLayer ScaleLayer(PlacementV2Prop prop, PlacementV2Def def)
        {
            if (prop == null || !prop.HasScale) return PlacementScaleLayer.Unit;
            float[] scale = prop.Scale;
            if (!IsPositiveFinite(scale)) return PlacementScaleLayer.Invalid;
            if (IsUnitScale(scale)) return PlacementScaleLayer.Unit;
            if (IsUniformScale(scale)) return PlacementScaleLayer.Uniform;

            // Non-uniform from here on: D-5 layer 3's three conditions.
            if (prop.HasParent && !string.IsNullOrEmpty(prop.Parent))
            {
                return PlacementScaleLayer.Refused;
            }

            if (!string.Equals(prop.EffectiveBehaviour, DefaultBehaviour, StringComparison.Ordinal))
            {
                return PlacementScaleLayer.Refused;
            }

            string colliderType = def == null || def.Collider == null ? null : def.Collider.Type;
            if (string.IsNullOrEmpty(colliderType)) return PlacementScaleLayer.NonUniformNoProxy;
            string type = colliderType.Trim().ToLowerInvariant();
            if (string.Equals(type, "box", StringComparison.Ordinal) ||
                string.Equals(type, "sphere", StringComparison.Ordinal))
            {
                return PlacementScaleLayer.NonUniformAllowed;
            }

            return PlacementScaleLayer.Refused;
        }

        /// <summary>
        /// The §9 tag and sentence for a scale that D-5 refuses, or null when the prop is fine.
        /// </summary>
        public static string ScaleError(PlacementV2Prop prop, PlacementV2Def def)
        {
            PlacementScaleLayer layer = ScaleLayer(prop, def);
            if (layer == PlacementScaleLayer.Invalid)
            {
                return "[prop_scale] scale must be three finite, positive numbers (got " +
                       Describe(prop == null ? null : prop.Scale) + ")";
            }

            if (layer != PlacementScaleLayer.Refused) return null;

            if (prop != null && prop.HasParent && !string.IsNullOrEmpty(prop.Parent))
            {
                return "[prop_scale_attached] " + prop.ItemId + " is parented to '" + prop.Parent +
                       "' and its scale " + Describe(prop.Scale) +
                       " is non-uniform; a uniform scale on an attached prop is legal (D-5 layer 2)," +
                       " a non-uniform one is not";
            }

            if (prop != null &&
                !string.Equals(prop.EffectiveBehaviour, DefaultBehaviour, StringComparison.Ordinal))
            {
                return "[prop_scale_nonuniform] " + prop.ItemId + " has a non-uniform scale " +
                       Describe(prop.Scale) + " but its behaviour is '" + prop.EffectiveBehaviour +
                       "'; only an unparented static prop may be scaled non-uniformly";
            }

            string type = def == null || def.Collider == null ? null : def.Collider.Type;
            return "[prop_scale_nonuniform] " + prop.ItemId + " has a non-uniform scale " +
                   Describe(prop.Scale) + " but its def's collider is '" + (type ?? "") +
                   "'; only a box or a sphere stays the same shape when scaled per axis" +
                   " (a capsule or a convex hull does not)";
        }

        // ----------------------------------------------------------------- parent ---

        /// <summary>The resolved <c>parent</c> edges of one document.</summary>
        public sealed class PlacementParentGraph
        {
            /// <summary>For each prop, the parent's index in <c>props</c>, or -1 for a root.</summary>
            public int[] ParentIndex = new int[0];
            /// <summary>For each prop, the authored parent id, or null.</summary>
            public string[] ParentId = new string[0];
            /// <summary>How many props declare a parent.</summary>
            public int EdgeCount;
            /// <summary>How many props have no parent.</summary>
            public int RootCount;
            /// <summary>Longest parent chain (0 = every prop is a root).</summary>
            public int MaxDepth;
            /// <summary>The first <c>[prop_parent]</c> problem, or null when the graph is valid.</summary>
            public string Error;
        }

        /// <summary>
        /// Resolves <c>props[].parent</c> into indices and refuses a missing parent, a self
        /// edge and a cycle (§6.5).
        /// </summary>
        /// <remarks>
        /// The loop is bounded by the prop count, so even a hand-broken document cannot spin
        /// here: a chain longer than the document is a cycle by definition.
        /// </remarks>
        public static PlacementParentGraph BuildParentGraph(PlacementV2Prop[] props)
        {
            var graph = new PlacementParentGraph();
            if (props == null)
            {
                return graph;
            }

            int count = props.Length;
            graph.ParentIndex = new int[count];
            graph.ParentId = new string[count];
            var byId = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                graph.ParentIndex[i] = -1;
                PlacementV2Prop prop = props[i];
                if (prop == null || string.IsNullOrEmpty(prop.ItemId)) continue;
                if (!byId.ContainsKey(prop.ItemId))
                {
                    byId[prop.ItemId] = i;
                }
            }

            for (int i = 0; i < count; i++)
            {
                PlacementV2Prop prop = props[i];
                if (prop == null || !prop.HasParent || string.IsNullOrEmpty(prop.Parent)) continue;
                graph.ParentId[i] = prop.Parent;
                graph.EdgeCount++;
                if (string.Equals(prop.Parent, prop.ItemId, StringComparison.Ordinal))
                {
                    if (graph.Error == null)
                    {
                        graph.Error = "[prop_parent] '" + prop.ItemId + "' is its own parent";
                    }

                    continue;
                }

                int parentIndex;
                if (!byId.TryGetValue(prop.Parent, out parentIndex))
                {
                    if (graph.Error == null)
                    {
                        graph.Error = "[prop_parent] '" + prop.ItemId + "' names the parent '" +
                                      prop.Parent + "', which is not a prop in this document";
                    }

                    continue;
                }

                graph.ParentIndex[i] = parentIndex;
            }

            // Depth and cycles: one walk per prop, capped by the prop count.
            graph.RootCount = 0;
            for (int i = 0; i < count; i++)
            {
                bool cyclic;
                int depthOfChain = DepthOf(i, graph.ParentIndex, count, out cyclic);
                if (cyclic)
                {
                    if (graph.Error == null)
                    {
                        PlacementV2Prop prop = props[i];
                        graph.Error = "[prop_parent] the parent chain of '" +
                                      (prop == null ? "#" + i : prop.ItemId) +
                                      "' is a cycle";
                    }

                    continue;
                }

                if (graph.ParentIndex[i] < 0) graph.RootCount++;
                if (depthOfChain > graph.MaxDepth) graph.MaxDepth = depthOfChain;
            }

            return graph;
        }

        /// <summary>
        /// Chain length above <paramref name="index"/> (0 = the prop is a root).
        /// </summary>
        /// <remarks>
        /// Walked fresh per prop: the worst case is 511 props x 511 links, which is a few
        /// hundred thousand integer steps once per import - cheaper to read than a memo table,
        /// and the counter is what turns a hand-written cycle into the documented error
        /// instead of an infinite loop.
        /// </remarks>
        private static int DepthOf(int index, int[] parentIndex, int count, out bool cyclic)
        {
            cyclic = false;
            int depth = 0;
            int current = index;
            while (parentIndex[current] >= 0)
            {
                current = parentIndex[current];
                depth++;
                if (depth > count)
                {
                    cyclic = true;
                    return 0;
                }
            }

            return depth;
        }

        // ----------------------------------------------------------------- growth ---

        /// <summary>The penalty a prop's growth is priced with: its own, else its def's, else 0.8.</summary>
        public static float EffectiveVolumePenalty(PlacementV2Prop prop, PlacementV2Def def)
        {
            if (prop != null && prop.HasVolumePenalty && prop.VolumePenalty > 0f)
            {
                return prop.VolumePenalty;
            }

            if (def != null && def.VolumePenalty > 0f) return def.VolumePenalty;
            return DefaultVolumePenalty;
        }

        /// <summary>
        /// The v2 growth-key rule (owner ruling B): <c>growthMm3</c> is authoritative,
        /// <c>volumeMm3</c> is an alias read only when it is absent, and both together must
        /// agree through the penalty.
        /// </summary>
        /// <param name="prop">The prop to price.</param>
        /// <param name="effectivePenalty">From <see cref="EffectiveVolumePenalty"/>.</param>
        /// <param name="growthMm3">The value the pickup field column receives.</param>
        /// <param name="fromVolumeAlias">True when the value came from <c>volumeMm3</c>.</param>
        /// <returns>The <c>[prop_volume_stale]</c> sentence, or null.</returns>
        public static string GrowthError(PlacementV2Prop prop, float effectivePenalty,
            out long growthMm3, out bool fromVolumeAlias)
        {
            growthMm3 = 0L;
            fromVolumeAlias = false;
            if (prop == null) return null;

            if (prop.HasGrowthMm3 && prop.HasVolumeMm3)
            {
                long derived = GrowthFromVolume(prop.VolumeMm3, effectivePenalty);
                if (derived != prop.GrowthMm3)
                {
                    return "[prop_volume_stale] " + prop.ItemId + " declares growthMm3=" +
                           prop.GrowthMm3 + " but volumeMm3=" + prop.VolumeMm3 + " x penalty=" +
                           effectivePenalty.ToString("0.####") + " rounds to " + derived;
                }

                growthMm3 = prop.GrowthMm3;
                return null;
            }

            if (prop.HasGrowthMm3)
            {
                growthMm3 = prop.GrowthMm3;
                return null;
            }

            if (prop.HasVolumeMm3)
            {
                growthMm3 = GrowthFromVolume(prop.VolumeMm3, effectivePenalty);
                fromVolumeAlias = true;
                return null;
            }

            return null;
        }

        /// <summary>
        /// The growth the pickup column receives, ignoring any consistency problem:
        /// <c>growthMm3</c> when written, else <c>round(volumeMm3 * penalty)</c>, else 0.
        /// </summary>
        public static long AuthoritativeGrowth(PlacementV2Prop prop, float effectivePenalty)
        {
            if (prop == null) return 0L;
            if (prop.HasGrowthMm3) return prop.GrowthMm3;
            if (prop.HasVolumeMm3) return GrowthFromVolume(prop.VolumeMm3, effectivePenalty);
            return 0L;
        }

        /// <summary>
        /// <c>round(volumeMm3 * penalty)</c> - the Python writer's relation
        /// (<c>placement_core.item_growth_mm3</c> prices <c>penalty * k * a*b*c * 1e9</c>, and
        /// <c>volumeMm3</c> is <c>k * a*b*c * 1e9</c>).
        /// </summary>
        public static long GrowthFromVolume(long volumeMm3, float penalty)
        {
            if (volumeMm3 == 0L) return 0L;
            // double here on purpose: Math.Round's default is banker's rounding, which is what
            // Python's round() does, so both sides round a half to even.
            double value = (double)volumeMm3 * penalty;
            return (long)Math.Round(value);
        }

        public static bool TryFindDef(PlacementV2Def[] defs, string id, out PlacementV2Def def)
        {
            def = null;
            if (defs == null || string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < defs.Length; i++)
            {
                PlacementV2Def candidate = defs[i];
                if (candidate != null && string.Equals(candidate.Id, id, StringComparison.Ordinal))
                {
                    def = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>The def a prop names, or null when the document does not have it.</summary>
        public static PlacementV2Def DefOf(PlacementV2Document document, PlacementV2Prop prop)
        {
            if (document == null || prop == null) return null;
            PlacementV2Def def;
            return TryFindDef(document.Defs, prop.Def, out def) ? def : null;
        }

        // ------------------------------------------------------------------ walls ---

        /// <summary>One edge of an activity wall, as the plane channel consumes it.</summary>
        /// <remarks>
        /// The arena bakes a wall from a Transform: <c>normal = transform.forward</c>,
        /// <c>offset = dot(normal, transform.position)</c>, and free space is
        /// <c>dot(normal, point) &gt;= offset</c>. So the marker's forward must point INTO the
        /// play area, which is exactly <see cref="InwardNormal"/>, and its position must lie on
        /// the wall, which is <see cref="Mid"/> at the level's ground height.
        /// </remarks>
        public sealed class PlacementV2WallSegment
        {
            /// <summary>Stable object name: <c>&lt;wallId&gt;_segNN</c>.</summary>
            public string Id;
            /// <summary>0-based edge index within its wall.</summary>
            public int Index;
            /// <summary>XZ start point.</summary>
            public float[] Start;
            /// <summary>XZ end point.</summary>
            public float[] End;
            /// <summary>XZ midpoint (the marker's position in the plane).</summary>
            public float[] Mid;
            /// <summary>XZ unit normal pointing into the play area.</summary>
            public float[] InwardNormal;
            public float LengthM;
        }

        /// <summary>How many plane segments a wall becomes (closed: one per point).</summary>
        public static int SegmentCount(PlacementV2Wall wall)
        {
            if (wall == null || wall.Points == null) return 0;
            int points = wall.Points.Length;
            if (points < 2) return 0;
            return wall.Closed ? points : points - 1;
        }

        /// <summary>
        /// Everything wrong with one wall that can be known without a scene, or null.
        /// </summary>
        public static string WallError(PlacementV2Wall wall)
        {
            if (wall == null) return null;
            string name = string.IsNullOrEmpty(wall.Id) ? "(unnamed wall)" : wall.Id;
            if (wall.Points == null || wall.Points.Length < 2)
            {
                return "[activity_wall] " + name + " needs at least 2 points (got " +
                       (wall.Points == null ? 0 : wall.Points.Length) + ")";
            }

            for (int i = 0; i < wall.Points.Length; i++)
            {
                float[] point = wall.Points[i];
                if (point == null || point.Length != 2 || !IsFinite(point[0]) ||
                    !IsFinite(point[1]))
                {
                    return "[activity_wall] " + name + " point " + i + " must be [x,z]";
                }
            }

            if (!(wall.HeightM > 0f))
            {
                return "[activity_wall] " + name + " heightM must be > 0 (got " +
                       wall.HeightM.ToString("0.####") + ")";
            }

            if (wall.ThicknessM < 0f)
            {
                return "[activity_wall] " + name + " thicknessM must be >= 0";
            }

            int expected = SegmentCount(wall);
            for (int i = 0; i < expected; i++)
            {
                int next = (i + 1) % wall.Points.Length;
                float dx = wall.Points[next][0] - wall.Points[i][0];
                float dz = wall.Points[next][1] - wall.Points[i][1];
                if (Math.Sqrt(dx * dx + dz * dz) < MinSegmentLengthM)
                {
                    return "[activity_wall] " + name + " segment " + i +
                           " has two identical points, so it is not a wall";
                }
            }

            if (!wall.Closed) return null;

            float signedArea;
            string convexity;
            if (!IsConvexPolygon(wall.Points, out signedArea, out convexity))
            {
                return "[activity_wall] " + name + " is closed but " + convexity +
                       "; the plane channel has no inside direction in a concave corner" +
                       " (build an ActivityAreaHost with OBB slabs before using this polygon)";
            }

            return null;
        }

        /// <summary>
        /// Builds one plane segment per edge, with the normal pointing into the play area.
        /// </summary>
        /// <remarks>
        /// Closed polygon: the normal is the edge's perpendicular flipped toward the polygon's
        /// centroid, which is correct for every CONVEX polygon and is why
        /// <see cref="WallError"/> refuses a concave one. Open polyline: there is no interior,
        /// so the documented convention is "the play area is on the LEFT of the travel
        /// direction" (points ordered counter-clockwise around the field), and the normal is the
        /// left perpendicular. A caller that gets it backwards gets an arena that blocks the
        /// wrong side, which is why the importer notes the convention once per run.
        /// </remarks>
        public static bool TryBuildWallSegments(PlacementV2Wall wall,
            out PlacementV2WallSegment[] segments, out string error)
        {
            segments = new PlacementV2WallSegment[0];
            error = WallError(wall);
            if (error != null) return false;

            int count = SegmentCount(wall);
            var result = new PlacementV2WallSegment[count];
            float[] centroid = wall.Closed ? Centroid(wall.Points) : null;
            string wallName = string.IsNullOrEmpty(wall.Id) ? "wall" : wall.Id;
            for (int i = 0; i < count; i++)
            {
                int next = (i + 1) % wall.Points.Length;
                float[] start = wall.Points[i];
                float[] end = wall.Points[next];
                float dx = end[0] - start[0];
                float dz = end[1] - start[1];
                float length = (float)Math.Sqrt(dx * dx + dz * dz);
                // A fixed perpendicular; the closed case then flips it toward the interior.
                float nx = -dz / length;
                float nz = dx / length;
                if (centroid != null)
                {
                    float mx = (start[0] + end[0]) * 0.5f;
                    float mz = (start[1] + end[1]) * 0.5f;
                    float towardX = centroid[0] - mx;
                    float towardZ = centroid[1] - mz;
                    if (nx * towardX + nz * towardZ < 0f)
                    {
                        nx = -nx;
                        nz = -nz;
                    }
                }

                result[i] = new PlacementV2WallSegment
                {
                    Id = wallName + "_seg" + i.ToString("00"),
                    Index = i,
                    Start = new[] { start[0], start[1] },
                    End = new[] { end[0], end[1] },
                    Mid = new[] { (start[0] + end[0]) * 0.5f, (start[1] + end[1]) * 0.5f },
                    InwardNormal = new[] { nx, nz },
                    LengthM = length
                };
            }

            segments = result;
            return true;
        }

        /// <summary>Every wall of a document, flattened into the plane segments the arena bakes.</summary>
        public static PlacementV2WallSegment[] AllWallSegments(PlacementV2Document document,
            out string error)
        {
            error = null;
            if (document == null || document.Activity == null) return new PlacementV2WallSegment[0];
            var all = new List<PlacementV2WallSegment>();
            PlacementV2Wall[] walls = document.Activity.Walls;
            for (int i = 0; i < walls.Length; i++)
            {
                PlacementV2WallSegment[] segments;
                string wallError;
                if (!TryBuildWallSegments(walls[i], out segments, out wallError))
                {
                    if (error == null) error = wallError;
                    continue;
                }

                for (int s = 0; s < segments.Length; s++) all.Add(segments[s]);
            }

            return all.ToArray();
        }

        /// <summary>
        /// True when the closed polygon is convex (and non-degenerate), i.e. when a set of
        /// inward half-planes with normals from the centroid can describe it.
        /// </summary>
        public static bool IsConvexPolygon(float[][] points, out float signedArea,
            out string reason)
        {
            signedArea = 0f;
            reason = null;
            if (points == null || points.Length < 3)
            {
                reason = "it has fewer than 3 points";
                return false;
            }

            double area = 0.0;
            double largestEdgeSquared = 0.0;
            for (int i = 0; i < points.Length; i++)
            {
                float[] a = points[i];
                float[] b = points[(i + 1) % points.Length];
                area += (double)a[0] * b[1] - (double)b[0] * a[1];
                double dx = b[0] - a[0];
                double dz = b[1] - a[1];
                double squared = dx * dx + dz * dz;
                if (squared > largestEdgeSquared) largestEdgeSquared = squared;
            }

            signedArea = (float)(area * 0.5);
            if (Math.Abs(area * 0.5) < PolygonAreaEpsilon)
            {
                reason = "its area is zero (the points are collinear)";
                return false;
            }

            // Cross products of consecutive edges must all have the same sign. The tolerance
            // scales with the polygon's own edge length so a 20 m field and a 1 m test triangle
            // are judged by the same rule.
            float tolerance = (float)(largestEdgeSquared * 1e-6);
            float sign = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                float[] a = points[i];
                float[] b = points[(i + 1) % points.Length];
                float[] c = points[(i + 2) % points.Length];
                float cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]);
                if (Math.Abs(cross) <= tolerance) continue;   // collinear vertex: harmless
                if (sign == 0f)
                {
                    sign = cross;
                }
                else if (cross * sign < 0f)
                {
                    reason = "it is not convex (vertex " + ((i + 1) % points.Length) +
                             " turns the wrong way)";
                    return false;
                }
            }

            return true;
        }

        /// <summary>The XZ centroid of a polygon's vertices.</summary>
        public static float[] Centroid(float[][] points)
        {
            if (points == null || points.Length == 0) return new[] { 0f, 0f };
            double x = 0.0;
            double z = 0.0;
            for (int i = 0; i < points.Length; i++)
            {
                x += points[i][0];
                z += points[i][1];
            }

            return new[] { (float)(x / points.Length), (float)(z / points.Length) };
        }

        // ---------------------------------------------------------------- summary ---

        /// <summary>
        /// The counts a level's data must produce, for the importer's report and for the
        /// acceptance run against a reference document.
        /// </summary>
        public sealed class PlacementV2Summary
        {
            public int PropCount;
            public int DefCount;
            public int DefWithColliderCount;
            public int SolidCount;
            public int ParentCount;
            public int ParentRootCount;
            public int ParentMaxDepth;
            public int UniformScaleCount;
            public int NonUniformScaleCount;
            public int WallCount;
            public int WallSegmentCount;
            public int MotionCount;
            public int PathCount;
            public long GrowthMm3Total;
            public string ParentError;
        }

        public static PlacementV2Summary Summarize(PlacementV2Document document)
        {
            var summary = new PlacementV2Summary();
            if (document == null) return summary;

            summary.PropCount = document.Props.Length;
            summary.DefCount = document.Defs.Length;
            for (int i = 0; i < document.Defs.Length; i++)
            {
                if (document.Defs[i] != null && document.Defs[i].Collider != null)
                {
                    summary.DefWithColliderCount++;
                }
            }

            summary.SolidCount = document.Solids.Length;
            summary.MotionCount = document.Motions.Length;
            summary.PathCount = document.Paths.Length;

            PlacementParentGraph graph = BuildParentGraph(document.Props);
            summary.ParentCount = graph.EdgeCount;
            summary.ParentRootCount = graph.RootCount;
            summary.ParentMaxDepth = graph.MaxDepth;
            summary.ParentError = graph.Error;

            for (int i = 0; i < document.Props.Length; i++)
            {
                PlacementV2Prop prop = document.Props[i];
                if (prop == null) continue;
                if (prop.HasScale && !IsUnitScale(prop.Scale))
                {
                    if (IsUniformScale(prop.Scale)) summary.UniformScaleCount++;
                    else summary.NonUniformScaleCount++;
                }

                PlacementV2Def def = DefOf(document, prop);
                summary.GrowthMm3Total += AuthoritativeGrowth(prop,
                    EffectiveVolumePenalty(prop, def));
            }

            if (document.Activity != null)
            {
                summary.WallCount = document.Activity.Walls.Length;
                for (int i = 0; i < document.Activity.Walls.Length; i++)
                {
                    summary.WallSegmentCount += SegmentCount(document.Activity.Walls[i]);
                }
            }

            return summary;
        }

        // ------------------------------------------------------------------ helpers ---

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool IsPositiveFinite(float[] values)
        {
            if (values == null) return false;
            for (int i = 0; i < values.Length; i++)
            {
                if (!IsFinite(values[i]) || values[i] <= 0f) return false;
            }

            return true;
        }

        private static string Describe(float[] values)
        {
            if (values == null) return "absent";
            var text = new System.Text.StringBuilder();
            text.Append('[');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(values[i].ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
            }

            text.Append(']');
            return text.ToString();
        }
    }
}
