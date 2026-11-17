using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>One <c>terrain.visual.materials[]</c> entry: a texture and how it blends.</summary>
    public sealed class TerrainVisualMaterial
    {
        public string Id;
        /// <summary>Path relative to the level directory (the PNG the converter wrote).</summary>
        public string Texture;
        public string Sha256;
        public int Width;
        public int Height;
        /// <summary><c>opaque</c> / <c>cutout</c> / <c>alpha</c> (<see cref="PlacementV2Rules.VisualShaderName"/>).</summary>
        public string Blend;
    }

    /// <summary>A run of one part's indices drawn with one material (<c>firstIndex</c> is part-relative).</summary>
    public sealed class TerrainVisualSubmesh
    {
        public string Material;
        public int FirstIndex;
        public int IndexCount;
    }

    /// <summary>One <c>terrain.visual.parts[]</c> entry: one mesh asset / one renderer.</summary>
    public sealed class TerrainVisualPart
    {
        public string Id;
        /// <summary><c>stage</c> or <c>sky</c>.</summary>
        public string Role;
        public int VertexStart;
        public int VertexCount;
        public int IndexStart;
        public int IndexCount;
        public float[] BoundsMin;
        public float[] BoundsMax;
        public TerrainVisualSubmesh[] Submeshes;
    }

    /// <summary>
    /// <c>terrain.visual</c> (LEVEL_FORMAT_V2 §3.8): the rendered surface of a level, apart from
    /// the contact mesh. Engine-free on purpose (plain arrays, no UnityEngine): the parsing and
    /// every consistency rule can be tested without an Editor, and the Unity-side assembly of
    /// meshes/materials lives in <c>TerrainMeshImporter</c>. The rules mirror the Python
    /// validator's <c>[terrain_visual_*]</c> tags (<c>Tools/levelbuild/placement_v2.py</c>).
    /// </summary>
    public sealed class TerrainVisualDocument
    {
        public const string RoleStage = "stage";
        public const string RoleSky = "sky";

        public string SidecarPath;
        public string SidecarSha256;
        public int VertexCount = -1;
        public int IndexCount = -1;
        public int TriangleCount = -1;
        public int IndexWidth = -1;
        public bool HasNormals;
        public bool HasUvs;
        /// <summary><c>point</c> / <c>bilinear</c>.</summary>
        public string Filter;
        public TerrainVisualPart[] Parts = new TerrainVisualPart[0];
        public TerrainVisualMaterial[] Materials = new TerrainVisualMaterial[0];

        public TerrainVisualMaterial FindMaterial(string id)
        {
            for (int i = 0; i < Materials.Length; i++)
            {
                if (string.Equals(Materials[i].Id, id, StringComparison.Ordinal)) return Materials[i];
            }

            return null;
        }

        /// <summary>
        /// Parse and shape-check the block. <paramref name="error"/> carries the
        /// <c>[terrain_visual*]</c> tag of the first problem; the geometry itself is not read.
        /// </summary>
        public static bool TryParse(LevelJsonNode visual, out TerrainVisualDocument document,
            out string error)
        {
            document = null;
            error = null;
            if (visual == null || visual.Kind != LevelJsonKind.Object)
            {
                error = "[terrain_visual] terrain.visual must be an object";
                return false;
            }

            var result = new TerrainVisualDocument();
            result.Filter = visual.GetString("filter");
            if (result.Filter != "point" && result.Filter != "bilinear")
            {
                error = "[terrain_visual] terrain.visual.filter must be 'point' or 'bilinear' (got '" +
                        result.Filter + "')";
                return false;
            }

            LevelJsonNode meshRef = visual.Get("meshRef");
            if (meshRef == null || meshRef.Kind != LevelJsonKind.Object)
            {
                error = "[terrain_visual_sidecar] terrain.visual.meshRef must be an object";
                return false;
            }

            result.SidecarPath = meshRef.GetString("path");
            result.SidecarSha256 = meshRef.GetString("sha256");
            result.VertexCount = meshRef.GetInt("vertexCount", -1);
            result.IndexCount = meshRef.GetInt("indexCount", -1);
            result.TriangleCount = meshRef.GetInt("triangleCount", -1);
            result.IndexWidth = meshRef.GetInt("indexWidth", -1);
            result.HasNormals = meshRef.GetBool("normals", false);
            result.HasUvs = meshRef.GetBool("uvs", false);

            LevelJsonNode materials = visual.Get("materials");
            if (materials == null || materials.Kind != LevelJsonKind.Array || materials.Count == 0)
            {
                error = "[terrain_visual_material] terrain.visual.materials must be a non-empty array";
                return false;
            }

            var materialList = new List<TerrainVisualMaterial>();
            var materialIds = new HashSet<string>();
            for (int i = 0; i < materials.Count; i++)
            {
                LevelJsonNode node = materials.At(i);
                if (node == null || node.Kind != LevelJsonKind.Object)
                {
                    error = "[terrain_visual_material] terrain.visual.materials[" + i +
                            "] is not an object";
                    return false;
                }

                var material = new TerrainVisualMaterial
                {
                    Id = node.GetString("id"),
                    Texture = node.GetString("texture"),
                    Sha256 = node.GetString("sha256"),
                    Width = node.GetInt("width", -1),
                    Height = node.GetInt("height", -1),
                    Blend = node.GetString("blend")
                };
                if (string.IsNullOrEmpty(material.Id) || !materialIds.Add(material.Id))
                {
                    error = "[terrain_visual_material] terrain.visual.materials[" + i +
                            "].id must be a unique non-empty string (got '" + material.Id + "')";
                    return false;
                }

                if (PlacementV2Rules.VisualShaderName(material.Blend) == null)
                {
                    error = "[terrain_visual_material] terrain.visual.materials[" + i +
                            "].blend must be opaque/cutout/alpha (got '" + material.Blend + "')";
                    return false;
                }

                if (!IsRelativeInside(material.Texture))
                {
                    error = "[terrain_visual_texture] terrain.visual.materials[" + i +
                            "].texture must be a relative path inside the level directory (got '" +
                            material.Texture + "')";
                    return false;
                }

                materialList.Add(material);
            }

            result.Materials = materialList.ToArray();

            LevelJsonNode parts = visual.Get("parts");
            if (parts == null || parts.Kind != LevelJsonKind.Array || parts.Count == 0)
            {
                error = "[terrain_visual_part] terrain.visual.parts must be a non-empty array";
                return false;
            }

            var partList = new List<TerrainVisualPart>();
            var partIds = new HashSet<string>();
            int vertexCursor = 0;
            int indexCursor = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                string where = "terrain.visual.parts[" + i + "]";
                TerrainVisualPart part;
                if (!TryParsePart(parts.At(i), where, materialIds, out part, out error))
                {
                    return false;
                }

                if (!partIds.Add(part.Id))
                {
                    error = "[terrain_visual_part] " + where + ".id '" + part.Id + "' is not unique";
                    return false;
                }

                if (part.VertexStart != vertexCursor || part.IndexStart != indexCursor)
                {
                    error = "[terrain_visual_part] " + where + " must start at vertex " +
                            vertexCursor + " / index " + indexCursor +
                            " (the parts tile the sidecar in order)";
                    return false;
                }

                vertexCursor += part.VertexCount;
                indexCursor += part.IndexCount;
                partList.Add(part);
            }

            if ((result.VertexCount >= 0 && result.VertexCount != vertexCursor) ||
                (result.IndexCount >= 0 && result.IndexCount != indexCursor))
            {
                error = "[terrain_visual_part] terrain.visual.parts cover " + vertexCursor +
                        " vertices / " + indexCursor + " indices but meshRef declares " +
                        result.VertexCount + " / " + result.IndexCount;
                return false;
            }

            result.Parts = partList.ToArray();
            document = result;
            return true;
        }

        private static bool TryParsePart(LevelJsonNode node, string where,
            HashSet<string> materialIds, out TerrainVisualPart part, out string error)
        {
            part = null;
            error = null;
            if (node == null || node.Kind != LevelJsonKind.Object)
            {
                error = "[terrain_visual_part] " + where + " is not an object";
                return false;
            }

            var result = new TerrainVisualPart { Id = node.GetString("id"), Role = node.GetString("role") };
            if (string.IsNullOrEmpty(result.Id))
            {
                error = "[terrain_visual_part] " + where + ".id must be a non-empty string";
                return false;
            }

            if (result.Role != RoleStage && result.Role != RoleSky)
            {
                error = "[terrain_visual_part] " + where + ".role must be stage/sky (got '" +
                        result.Role + "')";
                return false;
            }

            int[] vertexRange;
            int[] indexRange;
            if (!TryPair(node.Get("vertexRange"), out vertexRange) ||
                !TryPair(node.Get("indexRange"), out indexRange))
            {
                error = "[terrain_visual_part] " + where +
                        ".vertexRange/indexRange must be [start,count] whole numbers";
                return false;
            }

            result.VertexStart = vertexRange[0];
            result.VertexCount = vertexRange[1];
            result.IndexStart = indexRange[0];
            result.IndexCount = indexRange[1];
            if (result.VertexCount <= 0 || result.IndexCount <= 0 || result.IndexCount % 3 != 0)
            {
                error = "[terrain_visual_part] " + where +
                        " needs a positive vertex count and an index count that is a positive multiple of 3";
                return false;
            }

            LevelJsonNode bounds = node.Get("bounds");
            if (bounds == null || bounds.Kind != LevelJsonKind.Object ||
                !LevelJsonValues.TryNumbers(bounds.Get("min"), 3, out result.BoundsMin) ||
                !LevelJsonValues.TryNumbers(bounds.Get("max"), 3, out result.BoundsMax))
            {
                error = "[terrain_visual_part] " + where + ".bounds must be {min,max} 3-vectors";
                return false;
            }

            LevelJsonNode submeshes = node.Get("submeshes");
            if (submeshes == null || submeshes.Kind != LevelJsonKind.Array || submeshes.Count == 0)
            {
                error = "[terrain_visual_part] " + where + ".submeshes must be a non-empty array";
                return false;
            }

            var list = new List<TerrainVisualSubmesh>();
            int covered = 0;
            for (int i = 0; i < submeshes.Count; i++)
            {
                LevelJsonNode item = submeshes.At(i);
                if (item == null || item.Kind != LevelJsonKind.Object)
                {
                    error = "[terrain_visual_part] " + where + ".submeshes[" + i + "] is not an object";
                    return false;
                }

                var submesh = new TerrainVisualSubmesh
                {
                    Material = item.GetString("material"),
                    FirstIndex = item.GetInt("firstIndex", -1),
                    IndexCount = item.GetInt("indexCount", -1)
                };
                if (!materialIds.Contains(submesh.Material))
                {
                    error = "[terrain_visual_material] " + where + ".submeshes[" + i +
                            "].material '" + submesh.Material + "' is not in terrain.visual.materials";
                    return false;
                }

                if (submesh.FirstIndex != covered || submesh.IndexCount <= 0 ||
                    submesh.IndexCount % 3 != 0)
                {
                    error = "[terrain_visual_part] " + where + ".submeshes[" + i +
                            "] must start at " + covered +
                            " and have a positive multiple-of-3 indexCount (the submeshes tile the part)";
                    return false;
                }

                covered += submesh.IndexCount;
                list.Add(submesh);
            }

            if (covered != result.IndexCount)
            {
                error = "[terrain_visual_part] " + where + ".submeshes cover " + covered +
                        " indices but indexRange declares " + result.IndexCount;
                return false;
            }

            result.Submeshes = list.ToArray();
            part = result;
            return true;
        }

        private static bool TryPair(LevelJsonNode node, out int[] pair)
        {
            pair = null;
            if (node == null || node.Kind != LevelJsonKind.Array || node.Count != 2) return false;
            int first = node.At(0).AsInt();
            int second = node.At(1).AsInt();
            if (first < 0 || second < 0) return false;
            pair = new[] { first, second };
            return true;
        }

        /// <summary>True for a non-empty relative path with no <c>..</c> segment and no backslash.</summary>
        public static bool IsRelativeInside(string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative[0] == '/' || relative.IndexOf('\\') >= 0 ||
                Path.IsPathRooted(relative))
            {
                return false;
            }

            string[] segments = relative.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == ".." || segments[i].Length == 0) return false;
            }

            return true;
        }

        /// <summary>
        /// Read the <c>.katvisual</c> sidecar next to the document and verify everything the
        /// document pins: sha256, counts, index width, flags, the part table against the data.
        /// </summary>
        public bool TryLoadSidecar(string levelDir, out TerrainVisualSidecar sidecar,
            out string error)
        {
            sidecar = null;
            string full;
            if (!TryResolveFile(levelDir, SidecarPath, "[terrain_visual_sidecar]", out full,
                out error)) return false;
            byte[] bytes = File.ReadAllBytes(full);
            string actual = TerrainVisualSidecar.Sha256Hex(bytes);
            if (!string.Equals(actual, SidecarSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "[terrain_visual_sidecar] sha256 " + actual + " != terrain.visual.meshRef.sha256 " +
                        SidecarSha256;
                return false;
            }

            TerrainVisualSidecar parsed;
            if (!TerrainVisualSidecar.TryRead(bytes, out parsed, out error)) return false;
            if ((VertexCount >= 0 && VertexCount != parsed.VertexCount) ||
                (IndexCount >= 0 && IndexCount != parsed.IndexCount) ||
                (TriangleCount >= 0 && TriangleCount != parsed.IndexCount / 3) ||
                (IndexWidth >= 0 && IndexWidth != parsed.IndexWidth) ||
                HasNormals != (parsed.Normals != null) || HasUvs != (parsed.Uvs != null) ||
                parsed.PartCount != Parts.Length)
            {
                error = "[terrain_visual_sidecar] the sidecar's counts/flags disagree with terrain.visual.meshRef";
                return false;
            }

            for (int i = 0; i < Parts.Length; i++)
            {
                TerrainVisualPart part = Parts[i];
                if (part.VertexStart + part.VertexCount > parsed.VertexCount ||
                    part.IndexStart + part.IndexCount > parsed.IndexCount)
                {
                    error = "[terrain_visual_part] part '" + part.Id + "' reaches past the sidecar";
                    return false;
                }

                for (int k = part.IndexStart; k < part.IndexStart + part.IndexCount; k++)
                {
                    if (parsed.Indices[k] < 0 || parsed.Indices[k] >= part.VertexCount)
                    {
                        error = "[terrain_visual_part] part '" + part.Id + "' has index " +
                                parsed.Indices[k] + " outside its " + part.VertexCount + " vertices";
                        return false;
                    }
                }
            }

            sidecar = parsed;
            return true;
        }

        /// <summary>
        /// The full path of a material's PNG, verified against its sha256 (so an edited or
        /// truncated texture is refused instead of being imported).
        /// </summary>
        public bool TryResolveTexture(string levelDir, TerrainVisualMaterial material,
            out string fullPath, out string error)
        {
            if (!TryResolveFile(levelDir, material.Texture, "[terrain_visual_texture]",
                out fullPath, out error)) return false;
            string actual = TerrainVisualSidecar.Sha256Hex(File.ReadAllBytes(fullPath));
            if (!string.Equals(actual, material.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "[terrain_visual_texture] " + material.Texture + " sha256 " + actual +
                        " != materials[" + material.Id + "].sha256 " + material.Sha256;
                return false;
            }

            return true;
        }

        private static bool TryResolveFile(string levelDir, string relative, string tag,
            out string full, out string error)
        {
            full = null;
            error = null;
            if (!IsRelativeInside(relative))
            {
                error = tag + " '" + relative + "' must be a relative path inside the level directory";
                return false;
            }

            full = Path.GetFullPath(Path.Combine(levelDir, relative));
            string fullDir = Path.GetFullPath(levelDir) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(fullDir, StringComparison.Ordinal))
            {
                error = tag + " '" + relative + "' escapes the level directory";
                return false;
            }

            if (!File.Exists(full))
            {
                error = tag + " missing " + full;
                return false;
            }

            return true;
        }
    }

    /// <summary>The parsed <c>.katvisual</c> payload (§8.4): flat arrays, part-local indices.</summary>
    public sealed class TerrainVisualSidecar
    {
        public int IndexWidth;
        public int VertexCount;
        public int IndexCount;
        public int PartCount;
        /// <summary>3 floats per vertex.</summary>
        public float[] Positions;
        /// <summary>3 floats per vertex, or null.</summary>
        public float[] Normals;
        /// <summary>2 floats per vertex (image top-left origin), or null.</summary>
        public float[] Uvs;
        /// <summary>Indices local to the part that owns them.</summary>
        public int[] Indices;

        public static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        public static bool TryRead(byte[] data, out TerrainVisualSidecar sidecar, out string error)
        {
            sidecar = null;
            error = null;
            try
            {
                if (data == null || data.Length < 20 || data[0] != 'K' || data[1] != 'V' ||
                    data[2] != 'I' || data[3] != 'S')
                {
                    error = "[terrain_visual_sidecar] magic is not KVIS";
                    return false;
                }

                using (var stream = new MemoryStream(data, false))
                using (var reader = new BinaryReader(stream, Encoding.ASCII))
                {
                    reader.ReadBytes(4);
                    int version = reader.ReadUInt16();
                    if (version != 1)
                    {
                        error = "[terrain_visual_sidecar] version " + version + " != 1";
                        return false;
                    }

                    int indexWidth = reader.ReadByte();
                    int flags = reader.ReadByte();
                    int vertexCount = (int)reader.ReadUInt32();
                    int indexCount = (int)reader.ReadUInt32();
                    int partCount = (int)reader.ReadUInt32();
                    if (indexWidth != 2 && indexWidth != 4)
                    {
                        error = "[terrain_visual_sidecar] indexWidth must be 2 or 4";
                        return false;
                    }

                    long needed = 20L + 12L * vertexCount + (long)indexCount * indexWidth;
                    if ((flags & 1) != 0) needed += 12L * vertexCount;
                    if ((flags & 2) != 0) needed += 8L * vertexCount;
                    if (vertexCount <= 0 || indexCount <= 0 || indexCount % 3 != 0 ||
                        needed != data.Length)
                    {
                        error = "[terrain_visual_sidecar] the counts need " + needed +
                                " bytes but the file has " + data.Length;
                        return false;
                    }

                    var result = new TerrainVisualSidecar
                    {
                        IndexWidth = indexWidth,
                        VertexCount = vertexCount,
                        IndexCount = indexCount,
                        PartCount = partCount,
                        Positions = ReadFloats(reader, 3 * vertexCount)
                    };
                    if ((flags & 1) != 0) result.Normals = ReadFloats(reader, 3 * vertexCount);
                    if ((flags & 2) != 0) result.Uvs = ReadFloats(reader, 2 * vertexCount);
                    result.Indices = new int[indexCount];
                    for (int i = 0; i < indexCount; i++)
                    {
                        result.Indices[i] = indexWidth == 2 ? reader.ReadUInt16() : (int)reader.ReadUInt32();
                    }

                    sidecar = result;
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = "[terrain_visual_sidecar] " + exception.Message;
                return false;
            }
        }

        private static float[] ReadFloats(BinaryReader reader, int count)
        {
            var values = new float[count];
            for (int i = 0; i < count; i++) values[i] = reader.ReadSingle();
            return values;
        }
    }
}
