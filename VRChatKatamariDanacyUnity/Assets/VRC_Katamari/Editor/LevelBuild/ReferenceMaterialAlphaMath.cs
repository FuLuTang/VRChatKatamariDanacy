using System;
using System.Collections.Generic;
using System.Text;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// How a reference prop's material should render, decided from its source glTF material:
    /// <c>alphaMode</c> OPAQUE/MASK/BLEND becomes the built-in Standard shader's Opaque/Cutout/Fade
    /// mode; a MASK material cuts at <see cref="CutoutThreshold"/> and its texture has no
    /// mipmaps. Pure: no asset, file or importer access, so the rules are pinned by EditMode
    /// tests; <see cref="ReferenceModelMaterialPostprocessor"/> applies them during the import
    /// and <c>ReferenceParityAudit</c> checks the scene against the same rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists. The reference FBXes under <c>Assets/_RefUnlicensed/Models/</c> are a
    /// Blender conversion of the extracted GLBs, and that conversion keeps no alpha mode: the FBX
    /// only links the colour texture to <c>TransparencyFactor</c>, which Unity's FBX material
    /// import does not read as transparency. Every MASK leaf (tomato, tulips, shoji...) therefore
    /// imported as Standard Opaque and drew its transparent texels as a solid block. The source
    /// GLB still has the original's answer, so the importer reads it there.
    /// </para>
    /// <para>
    /// The mapping. MASK (the PS2 alpha test, <c>sourceAlphaTestRegister</c> 0x5000F) is
    /// Standard Cutout. BLEND is the PS2 straight-alpha blend (<c>sourceAlphaRegister</c> 0x44,
    /// (Cs-Cd)*As+Cd), which is Standard Fade; Standard "Transparent" is premultiplied and keeps
    /// specular on transparent texels, which the original does not have.
    /// </para>
    /// <para>
    /// Leaves follow Katamari Damacy REROLL (PC, 2019-01-31), not the GLB's <c>alphaCutoff</c>
    /// (1/255, the PS2 "alpha != 0" test). REROLL draws these props with its <c>CustomStandard</c>
    /// shader; the disassembled pixel shader discards when <c>tex.a * _Color.a &lt; _Cutoff</c>
    /// with <c>_Cutoff</c> = 0.5 on the material, and samples the texture with
    /// <c>sample_l ... l(0)</c>, i.e. mip 0 only (the atlas has 11 mips that are never read).
    /// The textures' alpha is only 0 or 255. So a MASK material here cuts at 0.5 and its texture
    /// is imported without mipmaps: with mipmaps a distant leaf's alpha averaged below the
    /// threshold and the leaf vanished, which REROLL cannot do. Source: the REROLL asset survey
    /// (<c>.work/notes/research/reroll_assets_survey.md</c>, "shader and texture alpha"). Texture
    /// filtering is left as imported.
    /// </para>
    /// <para>
    /// The Standard values are the ones Unity's <c>StandardShaderGUI.SetupMaterialWithBlendMode</c>
    /// writes for each mode (render type tag, blend factors, depth write, keyword, queue), so a
    /// material set up here looks the same in the inspector as one set by hand.
    /// </para>
    /// </remarks>
    public static class ReferenceMaterialAlphaMath
    {
        /// <summary>The render mode a source material maps to.</summary>
        public enum RenderMode
        {
            /// <summary>An alpha mode this importer does not know; the material is left alone.</summary>
            Unknown = -1,
            Opaque = 0,
            Cutout = 1,
            Fade = 2,
        }

        /// <summary>
        /// <c>_Cutoff</c> of every Cutout material: REROLL <c>CustomStandard</c>'s material value
        /// (see the remarks). The source GLB's <c>alphaCutoff</c> is not used.
        /// </summary>
        public const float CutoutThreshold = 0.5f;

        /// <summary>Standard shader <c>_Mode</c> values (StandardShaderGUI.BlendMode).</summary>
        public const float StandardModeOpaque = 0f;
        public const float StandardModeCutout = 1f;
        public const float StandardModeFade = 2f;

        /// <summary>UnityEngine.Rendering.BlendMode values the three modes use.</summary>
        public const int BlendZero = 0;
        public const int BlendOne = 1;
        public const int BlendSrcAlpha = 5;
        public const int BlendOneMinusSrcAlpha = 10;

        /// <summary>UnityEngine.Rendering.RenderQueue values; -1 means "the shader's own".</summary>
        public const int QueueFromShader = -1;
        public const int QueueAlphaTest = 2450;
        public const int QueueTransparent = 3000;

        public const string KeywordAlphaTest = "_ALPHATEST_ON";
        public const string KeywordAlphaBlend = "_ALPHABLEND_ON";
        public const string KeywordAlphaPremultiply = "_ALPHAPREMULTIPLY_ON";

        /// <summary>Every keyword the Standard blend modes switch; all but the mode's own are off.</summary>
        public static readonly string[] BlendKeywords =
        {
            KeywordAlphaTest,
            KeywordAlphaBlend,
            KeywordAlphaPremultiply,
        };

        /// <summary>glTF alphaMode to render mode. An absent alphaMode is OPAQUE (glTF default).</summary>
        public static RenderMode ForAlphaMode(string alphaMode)
        {
            if (string.IsNullOrEmpty(alphaMode)) return RenderMode.Opaque;
            switch (alphaMode)
            {
                case "OPAQUE": return RenderMode.Opaque;
                case "MASK": return RenderMode.Cutout;
                case "BLEND": return RenderMode.Fade;
                default: return RenderMode.Unknown;
            }
        }

        /// <summary>
        /// Import settings for the texture of a reference model whose source materials are
        /// <paramref name="materials"/> (each reference GLB has one image, shared by its
        /// textured materials). "Alpha Is Transparency" when any material is Cutout or Fade;
        /// mipmaps off when a textured Cutout material samples it (REROLL reads mip 0 only).
        /// Fade and Opaque textures keep their mipmaps.
        /// </summary>
        public static void TextureSettings(SourceMaterial[] materials, out bool alphaIsTransparency,
            out bool disableMipmaps)
        {
            alphaIsTransparency = false;
            disableMipmaps = false;
            if (materials == null) return;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null) continue;
                RenderMode mode = materials[i].Mode;
                if (mode == RenderMode.Cutout || mode == RenderMode.Fade) alphaIsTransparency = true;
                if (mode == RenderMode.Cutout && materials[i].HasTexture) disableMipmaps = true;
            }
        }

        /// <summary>
        /// True when an imported material does not render the way its source mode requires:
        /// a Cutout or Fade source whose material is not in that mode's queue, or a Cutout whose
        /// <c>_Cutoff</c> is not <see cref="CutoutThreshold"/>. Opaque and Unknown sources are
        /// never a mismatch (the importer leaves them alone).
        /// </summary>
        public static bool MissesSourceAlpha(RenderMode source, int renderQueue, bool hasCutoff, float cutoff)
        {
            if (source != RenderMode.Cutout && source != RenderMode.Fade) return false;
            if (renderQueue != RenderQueue(source)) return true;
            return source == RenderMode.Cutout && (!hasCutoff || Math.Abs(cutoff - CutoutThreshold) > 1e-4f);
        }

        /// <summary>
        /// The glTF material name an imported material stands for. The model's own embedded
        /// material keeps it; a copy made by <c>MaterialInstancing</c> is named
        /// <c>&lt;model stem&gt;_&lt;material&gt;</c>, so the prefix is removed. Null when a
        /// copy's name does not carry the prefix (it cannot be traced to the source).
        /// </summary>
        public static string SourceMaterialName(string modelStem, string materialName, bool embedded)
        {
            if (string.IsNullOrEmpty(materialName)) return null;
            if (embedded) return materialName;
            string prefix = (modelStem ?? "") + "_";
            if (prefix.Length < 2 || materialName.Length <= prefix.Length ||
                !materialName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            return materialName.Substring(prefix.Length);
        }

        /// <summary>Standard's <c>_Mode</c> for a render mode.</summary>
        public static float StandardMode(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Cutout: return StandardModeCutout;
                case RenderMode.Fade: return StandardModeFade;
                default: return StandardModeOpaque;
            }
        }

        /// <summary>The material's <c>RenderType</c> override tag.</summary>
        public static string RenderTypeTag(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Cutout: return "TransparentCutout";
                case RenderMode.Fade: return "Transparent";
                default: return "";
            }
        }

        public static int SourceBlend(RenderMode mode)
        {
            return mode == RenderMode.Fade ? BlendSrcAlpha : BlendOne;
        }

        public static int DestinationBlend(RenderMode mode)
        {
            return mode == RenderMode.Fade ? BlendOneMinusSrcAlpha : BlendZero;
        }

        /// <summary>Depth write: on for Opaque and Cutout, off for Fade.</summary>
        public static int ZWrite(RenderMode mode)
        {
            return mode == RenderMode.Fade ? 0 : 1;
        }

        /// <summary>The one blend keyword the mode turns on, or null (Opaque).</summary>
        public static string Keyword(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Cutout: return KeywordAlphaTest;
                case RenderMode.Fade: return KeywordAlphaBlend;
                default: return null;
            }
        }

        public static int RenderQueue(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Cutout: return QueueAlphaTest;
                case RenderMode.Fade: return QueueTransparent;
                default: return QueueFromShader;
            }
        }

        // ---------------------------------------------------------------- glb ---

        /// <summary>One source material's alpha facts, as the GLB states them.</summary>
        public sealed class SourceMaterial
        {
            public string Name;
            /// <summary>The written alphaMode, or null when the key is absent (OPAQUE).</summary>
            public string AlphaMode;
            /// <summary>True when the material samples a base colour texture.</summary>
            public bool HasTexture;
            public bool DoubleSided;

            public RenderMode Mode
            {
                get { return ForAlphaMode(AlphaMode); }
            }
        }

        private const uint GlbMagic = 0x46546C67; // "glTF"
        private const uint ChunkJson = 0x4E4F534A; // "JSON"

        /// <summary>
        /// The JSON chunk of a binary glTF (version 2). False with a reason for anything that is
        /// not one; the chunk is the first one after the 12-byte header, as the spec requires.
        /// </summary>
        public static bool TryReadGlbJson(byte[] data, out string json, out string error)
        {
            json = null;
            error = null;
            if (data == null || data.Length < 20)
            {
                error = "too short to be a GLB";
                return false;
            }

            if (ReadUInt32(data, 0) != GlbMagic || ReadUInt32(data, 4) != 2)
            {
                error = "not a glTF 2.0 binary";
                return false;
            }

            uint length = ReadUInt32(data, 12);
            if (ReadUInt32(data, 16) != ChunkJson)
            {
                error = "the first chunk is not JSON";
                return false;
            }

            if (length > (uint)(data.Length - 20))
            {
                error = "the JSON chunk runs past the end of the file";
                return false;
            }

            json = Encoding.UTF8.GetString(data, 20, (int)length);
            return true;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) |
                          (data[offset + 3] << 24));
        }

        /// <summary>
        /// The <c>materials[]</c> of a glTF JSON document. A material without a name, or a name
        /// written twice, is an error: the FBX materials are matched to these by name, so either
        /// would make the match ambiguous.
        /// </summary>
        public static bool TryReadMaterials(string json, out SourceMaterial[] materials, out string error)
        {
            materials = null;
            error = null;
            LevelJsonNode root;
            try
            {
                root = LevelJsonParser.ParseText(json);
            }
            catch (FormatException exception)
            {
                error = "glTF JSON does not parse: " + exception.Message;
                return false;
            }

            if (root == null || !root.IsObject)
            {
                error = "glTF JSON root is not an object";
                return false;
            }

            LevelJsonNode list = root.Get("materials");
            if (list == null)
            {
                materials = new SourceMaterial[0];
                return true;
            }

            if (!list.IsArray)
            {
                error = "glTF 'materials' is not an array";
                return false;
            }

            var result = new SourceMaterial[list.Count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++)
            {
                LevelJsonNode item = list.At(i);
                string name = item == null ? null : item.GetString("name");
                if (string.IsNullOrEmpty(name))
                {
                    error = "glTF materials[" + i + "] has no name";
                    return false;
                }

                if (!seen.Add(name))
                {
                    error = "glTF material name '" + name + "' appears twice";
                    return false;
                }

                LevelJsonNode pbr = item.Get("pbrMetallicRoughness");
                LevelJsonNode baseColor = pbr == null || !pbr.IsObject ? null : pbr.Get("baseColorTexture");
                result[i] = new SourceMaterial
                {
                    Name = name,
                    AlphaMode = item.GetString("alphaMode"),
                    HasTexture = baseColor != null && baseColor.IsObject,
                    DoubleSided = item.GetBool("doubleSided", false),
                };
            }

            materials = result;
            return true;
        }

        /// <summary>The material named <paramref name="name"/>, or null.</summary>
        public static SourceMaterial Find(SourceMaterial[] materials, string name)
        {
            if (materials == null || name == null) return null;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null && string.Equals(materials[i].Name, name, StringComparison.Ordinal))
                {
                    return materials[i];
                }
            }

            return null;
        }

        // ----------------------------------------------------------- manifest ---

        /// <summary>The file name part of a path written with either separator.</summary>
        public static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return cut < 0 ? path : path.Substring(cut + 1);
        }

        /// <summary>
        /// Which source GLB a model was converted from, from the manifests beside it.
        /// </summary>
        /// <param name="modelStem">The FBX file name without extension.</param>
        /// <param name="manifestFileNames">File names of the <c>*.manifest.json</c> in the folder.</param>
        /// <param name="manifestSources">Each manifest's <c>source_glb</c> (or <c>source</c>), same order.</param>
        /// <remarks>
        /// The model's own manifest (<c>&lt;stem&gt;.manifest.json</c>) wins: it is written by the
        /// conversion that produced this FBX. Without it every manifest in the folder must name
        /// the same GLB; two that disagree are refused, never guessed. This is not the level's
        /// <c>original_defs.json</c> on purpose: two levels may list different GLBs for one shared
        /// model path (<c>eggplant</c>: 036b in Cancer, 03b3 in MAS1) while only one FBX is on disk.
        /// </remarks>
        public static bool TryResolveSourceGlb(string modelStem, string[] manifestFileNames,
            string[] manifestSources, out string glbFileName, out string error)
        {
            glbFileName = null;
            error = null;
            if (manifestFileNames == null || manifestSources == null ||
                manifestFileNames.Length != manifestSources.Length || manifestFileNames.Length == 0)
            {
                error = "no manifest beside the model";
                return false;
            }

            string own = (modelStem ?? "") + ".manifest.json";
            for (int i = 0; i < manifestFileNames.Length; i++)
            {
                if (string.Equals(manifestFileNames[i], own, StringComparison.Ordinal))
                {
                    glbFileName = FileName(manifestSources[i]);
                    if (glbFileName.Length == 0)
                    {
                        error = own + " names no source GLB";
                        return false;
                    }

                    return true;
                }
            }

            string agreed = null;
            for (int i = 0; i < manifestSources.Length; i++)
            {
                string name = FileName(manifestSources[i]);
                if (name.Length == 0) continue;
                if (agreed == null)
                {
                    agreed = name;
                }
                else if (!string.Equals(agreed, name, StringComparison.Ordinal))
                {
                    error = "the manifests beside the model name different GLBs ('" + agreed + "', '" +
                            name + "') and none is " + own;
                    return false;
                }
            }

            if (agreed == null)
            {
                error = "no manifest beside the model names a source GLB";
                return false;
            }

            glbFileName = agreed;
            return true;
        }
    }
}
