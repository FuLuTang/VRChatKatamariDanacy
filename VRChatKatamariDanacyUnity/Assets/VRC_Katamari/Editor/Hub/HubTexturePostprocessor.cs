using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// Import settings of the hub's room textures and panel sprites, from
    /// <c>Tools/hubart/hub_layout.json</c> (<c>textures</c> and <c>sprites</c>), so a fresh
    /// checkout imports them right without anyone clicking through the Inspector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Room textures (tiling on metre UVs): Default type, sRGB, Repeat, Bilinear, mipmaps on,
    /// the layout's max size and aniso level, Normal Quality compression (DXT1/BC1 on PC; the
    /// Android override is ASTC 6x6, ready for a Quest build).
    /// </para>
    /// <para>
    /// Panel sprites (pre-coloured, drawn 1:1 by uGUI): Sprite (2D and UI), Single, Full Rect,
    /// the layout's 9-slice border, Clamp, Bilinear, no mipmaps, alpha is transparency, the
    /// layout's max size, Normal Quality compression (DXT5/BC3; Android ASTC 4x4, sprites have
    /// soft edges).
    /// </para>
    /// <para>
    /// Only files the layout names are touched; anything else under <c>Art/Hub</c> (the font,
    /// the room model) keeps its importer. The pure decision is <see cref="Settings"/>, tested
    /// in EditMode without an importer.
    /// </para>
    /// </remarks>
    public sealed class HubTexturePostprocessor : AssetPostprocessor
    {
        /// <summary>Plain settings one texture import gets; filled by <see cref="Settings"/>.</summary>
        internal sealed class Plan
        {
            public bool Sprite;
            public int MaxSize;
            public int AnisoLevel;
            public bool Mipmaps;
            public TextureWrapMode Wrap;
            public Vector4 Border;
            public bool AlphaIsTransparency;
            /// <summary>Android override format (Quest).</summary>
            public TextureImporterFormat AndroidFormat;
        }

        /// <summary>The import plan for one layout entry.</summary>
        internal static Plan Settings(HubRoomLayout.TextureSpec spec)
        {
            if (spec == null) return null;
            return new Plan
            {
                Sprite = spec.IsSprite,
                MaxSize = spec.MaxSize,
                AnisoLevel = spec.IsSprite ? 1 : Mathf.Clamp(spec.AnisoLevel, 0, 16),
                Mipmaps = !spec.IsSprite,
                Wrap = spec.IsSprite ? TextureWrapMode.Clamp : TextureWrapMode.Repeat,
                Border = spec.IsSprite ? spec.Border : Vector4.zero,
                AlphaIsTransparency = spec.IsSprite,
                AndroidFormat = spec.IsSprite ? TextureImporterFormat.ASTC_4x4 : TextureImporterFormat.ASTC_6x6,
            };
        }

        private void OnPreprocessTexture()
        {
            if (string.IsNullOrEmpty(assetPath)
                || !assetPath.StartsWith(HubRoomLayout.ArtFolder, StringComparison.Ordinal)
                || !assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var importer = assetImporter as TextureImporter;
            if (importer == null) return;

            HubRoomLayout layout;
            var errors = new List<string>();
            if (!HubRoomLayout.TryLoad(out layout, errors))
            {
                Debug.LogWarning("[hub-art] " + assetPath + " imported with default settings: "
                    + (errors.Count > 0 ? errors[0] : "the hub layout did not load"));
                return;
            }
            Plan plan = Settings(layout.FindTextureByFile(assetPath));
            if (plan == null) return;
            Apply(importer, plan);
        }

        private static void Apply(TextureImporter importer, Plan plan)
        {
            importer.textureType = plan.Sprite ? TextureImporterType.Sprite : TextureImporterType.Default;
            if (plan.Sprite)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spriteBorder = plan.Border;
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
            }
            importer.sRGBTexture = true;
            importer.alphaIsTransparency = plan.AlphaIsTransparency;
            importer.mipmapEnabled = plan.Mipmaps;
            importer.wrapMode = plan.Wrap;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = plan.AnisoLevel;
            importer.maxTextureSize = plan.MaxSize;
            importer.textureCompression = TextureImporterCompression.Compressed;

            TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = plan.MaxSize;
            android.format = plan.AndroidFormat;
            importer.SetPlatformTextureSettings(android);
        }
    }
}
