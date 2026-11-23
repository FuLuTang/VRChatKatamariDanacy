using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VRCKatamari.Editor.LevelBuild;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// The hub room's layout, <c>Tools/hubart/hub_layout.json</c>: the room's surfaces
    /// (materials), the textures and panel sprites with their import settings, lamps and the
    /// three wall boards' places and sizes, in hub-local metres. The Blender script
    /// <c>Tools/hubart/build_hub_room.py</c> builds the room model (with each board's frame and
    /// one <c>Anchor_Board_&lt;id&gt;</c> empty) from the same file and names every mesh
    /// material after a surface; the hub assembler builds one material per surface, places each
    /// board's panel prefab (HUB_PANEL_PREFAB.md) at <see cref="CanvasCenter"/> and checks the
    /// board anchors.
    /// </summary>
    /// <remarks>
    /// Frames: hub-local Unity metres, origin <c>HubCenter</c>. A board's readable side faces
    /// its local -Z after a yaw of <c>yawDeg</c> about +Y (the same side a world-space Canvas
    /// shows); on a board x is the reader's right, y up. The canvas plane is
    /// <c>canvasDepthM</c> in front of the board plane (towards the reader). The model's UVs are
    /// in metres (one UV unit = 1 m), so a surface's material tiling is
    /// <c>1 / metresPerTile</c>.
    /// </remarks>
    internal sealed class HubRoomLayout
    {
        public const string RepoRelativePath = "Tools/hubart/hub_layout.json";
        public const string Schema = "katamari.hub-layout/3";
        /// <summary>Folder every texture and sprite of the layout must sit in (or below).</summary>
        public const string ArtFolder = "Assets/VRC_Katamari/Art/Hub/";

        public sealed class Board
        {
            public string Id;
            public Vector3 Center;
            public float YawDeg;
            public Vector2 Size;
            /// <summary>Surface name of the board frame in the model.</summary>
            public string Frame;

            public Quaternion Rotation { get { return Quaternion.Euler(0f, YawDeg, 0f); } }

            /// <summary>Hub-local point of a board coordinate (x right, y up, depth to reader).</summary>
            public Vector3 Point(float x, float y, float depth)
            {
                return Center + Rotation * new Vector3(x, y, -depth);
            }
        }

        /// <summary>One room material: colour over an optional tiling texture.</summary>
        public sealed class Surface
        {
            public string Name;
            /// <summary>sRGB colour, multiplied over the texture (white = the texture as drawn).</summary>
            public Color Color;
            public float Smoothness;
            /// <summary>Name of an entry of <see cref="Textures"/>, or empty for a plain colour.</summary>
            public string Texture;
            /// <summary>Metres one copy of the texture covers; 0 without a texture.</summary>
            public float MetresPerTile;

            /// <summary>Material tiling for metre UVs: one texture copy per <see cref="MetresPerTile"/>.</summary>
            public Vector2 Tiling
            {
                get { return MetresPerTile > 0f ? Vector2.one / MetresPerTile : Vector2.one; }
            }
        }

        /// <summary>Import settings of one texture or panel sprite under <see cref="ArtFolder"/>.</summary>
        public sealed class TextureSpec
        {
            public string Name;
            /// <summary>Asset path (<c>Assets/...png</c>).</summary>
            public string File;
            public int MaxSize;
            public int AnisoLevel;
            /// <summary>True for a uGUI sprite (no mipmaps, clamped); false for a repeating room texture.</summary>
            public bool IsSprite;
            /// <summary>Sprite 9-slice border in pixels: left, bottom, right, top.</summary>
            public Vector4 Border;
        }

        public string ModelPath;
        /// <summary>Metres per canvas unit (0.001: one unit is a millimetre).</summary>
        public float CanvasMetresPerUnit = 0.001f;
        /// <summary>Canvas plane, metres in front of the board plane.</summary>
        public float CanvasDepthM = 0.06f;
        public string ModelRootName;
        /// <summary>Shader of every room material (a name for <c>Shader.Find</c>).</summary>
        public string ShaderName = "Standard";
        public readonly Dictionary<string, Board> Boards = new Dictionary<string, Board>();
        public readonly Dictionary<string, Surface> Surfaces = new Dictionary<string, Surface>();
        public readonly Dictionary<string, TextureSpec> Textures = new Dictionary<string, TextureSpec>();
        public readonly List<Vector3> Lamps = new List<Vector3>();
        /// <summary>Placeholder card thumbnail colour per level id (<c>cardPreviews.levels</c>).</summary>
        public readonly Dictionary<string, Color> CardPreviewByLevel = new Dictionary<string, Color>();
        public Color CardPreviewStory = new Color(0.616f, 0.686f, 0.486f, 1f);
        public Color CardPreviewVersus = new Color(0.639f, 0.565f, 0.533f, 1f);

        /// <summary>The level card's placeholder thumbnail colour: its own, else the mode's default.</summary>
        public Color CardPreviewColor(string levelId, bool versus)
        {
            Color c;
            if (!string.IsNullOrEmpty(levelId) && CardPreviewByLevel.TryGetValue(levelId, out c)) return c;
            return versus ? CardPreviewVersus : CardPreviewStory;
        }

        public static bool TryLoad(out HubRoomLayout layout, List<string> errors)
        {
            layout = null;
            string root = LevelPaths.RepoRoot();
            if (string.IsNullOrEmpty(root))
            {
                errors.Add("the repository root was not found, so " + RepoRelativePath
                    + " cannot be read");
                return false;
            }
            string path = Path.Combine(root, RepoRelativePath);
            if (!File.Exists(path))
            {
                errors.Add("no hub layout at " + path);
                return false;
            }
            return TryParse(File.ReadAllText(path), out layout, errors);
        }

        public static bool TryParse(string text, out HubRoomLayout layout, List<string> errors)
        {
            layout = null;
            LevelJsonNode root;
            try
            {
                root = LevelJsonParser.ParseText(text);
            }
            catch (FormatException exception)
            {
                errors.Add("the hub layout is not valid JSON: " + exception.Message);
                return false;
            }
            if (root == null || !root.IsObject || root.GetString("schema") != Schema)
            {
                errors.Add("the hub layout's schema is not '" + Schema + "'");
                return false;
            }

            int before = errors.Count;
            var l = new HubRoomLayout();
            LevelJsonNode model = root.Get("model");
            l.ModelPath = model != null ? model.GetString("fbx", "") : "";
            l.ModelRootName = model != null ? model.GetString("rootName", "") : "";
            l.CanvasMetresPerUnit = root.GetFloat("canvasMetresPerUnit", 0.001f);
            l.CanvasDepthM = root.GetFloat("canvasDepthM", 0.06f);
            l.ShaderName = root.GetString("shader", "Standard");
            if (!(l.CanvasMetresPerUnit >= 0.001f && l.CanvasMetresPerUnit <= 0.01f))
            {
                errors.Add("hub layout canvasMetresPerUnit " + l.CanvasMetresPerUnit
                    + " is outside 0.001-0.01 (VRChat's world UI scale range)");
            }

            ParseTextures(root.Get("textures"), false, l, errors);
            ParseTextures(root.Get("sprites"), true, l, errors);
            ParseSurfaces(root.Get("surfaces"), l, errors);

            LevelJsonNode boards = root.Get("boards");
            for (int i = 0; boards != null && i < boards.Count; i++)
            {
                LevelJsonNode b = boards.At(i);
                float[] c;
                float[] size;
                if (!LevelJsonValues.TryNumbers(b.Get("center"), 3, out c)
                    || !LevelJsonValues.TryNumbers(b.Get("size"), 2, out size))
                {
                    errors.Add("hub layout board " + i + " needs center[3] and size[2]");
                    continue;
                }
                var board = new Board
                {
                    Id = b.GetString("id"),
                    Center = new Vector3(c[0], c[1], c[2]),
                    YawDeg = b.GetFloat("yawDeg", 0f),
                    Size = new Vector2(size[0], size[1]),
                    Frame = b.GetString("frame", ""),
                };
                if (!l.Surfaces.ContainsKey(board.Frame))
                {
                    errors.Add("hub layout board '" + board.Id + "' frame '" + board.Frame
                        + "' is not one of the surfaces");
                }
                l.Boards[board.Id] = board;
            }

            ParseCardPreviews(root.Get("cardPreviews"), l, errors);

            LevelJsonNode lamps = root.Get("lampsLocal");
            for (int i = 0; lamps != null && i < lamps.Count; i++)
            {
                float[] p;
                if (LevelJsonValues.TryNumbers(lamps.At(i), 3, out p)) l.Lamps.Add(new Vector3(p[0], p[1], p[2]));
            }

            if (!l.Boards.ContainsKey("Levels") || !l.Boards.ContainsKey("Help")
                || !l.Boards.ContainsKey("Settings"))
            {
                errors.Add("the hub layout needs the boards Levels, Help and Settings");
            }
            layout = l;
            return errors.Count == before;
        }

        private static void ParseTextures(LevelJsonNode list, bool sprites, HubRoomLayout l,
            List<string> errors)
        {
            string what = sprites ? "sprites" : "textures";
            for (int i = 0; list != null && i < list.Count; i++)
            {
                LevelJsonNode e = list.At(i);
                var spec = new TextureSpec
                {
                    Name = e.GetString("name", ""),
                    File = e.GetString("file", ""),
                    MaxSize = e.GetInt("maxSize", 0),
                    AnisoLevel = e.GetInt("anisoLevel", 1),
                    IsSprite = sprites,
                };
                string where = "hub layout " + what + "[" + i + "] ('" + spec.Name + "')";
                if (string.IsNullOrEmpty(spec.Name)) errors.Add(where + " has no name");
                if (!spec.File.StartsWith(ArtFolder, StringComparison.Ordinal)
                    || !spec.File.EndsWith(".png", StringComparison.Ordinal))
                {
                    errors.Add(where + " file '" + spec.File + "' is not a .png under " + ArtFolder);
                }
                if (!IsPowerOfTwo(spec.MaxSize) || spec.MaxSize < 32 || spec.MaxSize > 2048)
                {
                    errors.Add(where + " maxSize " + spec.MaxSize + " is not a power of two in 32-2048");
                }
                if (sprites)
                {
                    float[] border;
                    if (!LevelJsonValues.TryNumbers(e.Get("border"), 4, out border))
                    {
                        errors.Add(where + " needs border[4] (left, bottom, right, top pixels)");
                    }
                    else
                    {
                        spec.Border = new Vector4(border[0], border[1], border[2], border[3]);
                    }
                }
                if (l.Textures.ContainsKey(spec.Name))
                {
                    errors.Add(where + " repeats the name '" + spec.Name + "'");
                    continue;
                }
                l.Textures[spec.Name] = spec;
            }
        }

        private static void ParseSurfaces(LevelJsonNode list, HubRoomLayout l, List<string> errors)
        {
            if (list == null || list.Count == 0)
            {
                errors.Add("the hub layout has no surfaces");
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                LevelJsonNode e = list.At(i);
                float[] rgb;
                var s = new Surface
                {
                    Name = e.GetString("name", ""),
                    Smoothness = e.GetFloat("smoothness", 0.2f),
                    Texture = e.GetString("texture", ""),
                    MetresPerTile = e.GetFloat("metresPerTile", 0f),
                };
                string where = "hub layout surfaces[" + i + "] ('" + s.Name + "')";
                if (string.IsNullOrEmpty(s.Name)) errors.Add(where + " has no name");
                if (!LevelJsonValues.TryNumbers(e.Get("rgb"), 3, out rgb))
                {
                    errors.Add(where + " needs rgb[3]");
                    rgb = new[] { 1f, 1f, 1f };
                }
                s.Color = new Color(rgb[0], rgb[1], rgb[2], 1f);
                if (!(s.Smoothness >= 0f && s.Smoothness <= 1f))
                {
                    errors.Add(where + " smoothness " + s.Smoothness + " is outside 0-1");
                }
                if (!string.IsNullOrEmpty(s.Texture))
                {
                    TextureSpec spec;
                    if (!l.Textures.TryGetValue(s.Texture, out spec) || spec.IsSprite)
                    {
                        errors.Add(where + " texture '" + s.Texture + "' is not one of the textures");
                    }
                    if (!(s.MetresPerTile > 0f))
                    {
                        errors.Add(where + " has a texture but metresPerTile " + s.MetresPerTile
                            + " is not positive");
                    }
                }
                if (l.Surfaces.ContainsKey(s.Name))
                {
                    errors.Add(where + " repeats the name '" + s.Name + "'");
                    continue;
                }
                l.Surfaces[s.Name] = s;
            }
        }

        private static void ParseCardPreviews(LevelJsonNode node, HubRoomLayout l, List<string> errors)
        {
            if (node == null) return;
            float[] rgb;
            if (LevelJsonValues.TryNumbers(node.Get("story"), 3, out rgb)) l.CardPreviewStory = new Color(rgb[0], rgb[1], rgb[2], 1f);
            if (LevelJsonValues.TryNumbers(node.Get("versus"), 3, out rgb)) l.CardPreviewVersus = new Color(rgb[0], rgb[1], rgb[2], 1f);
            LevelJsonNode levels = node.Get("levels");
            if (levels == null || !levels.IsObject) return;
            foreach (string id in levels.KeyArray())
            {
                if (LevelJsonValues.TryNumbers(levels.Get(id), 3, out rgb))
                    l.CardPreviewByLevel[id] = new Color(rgb[0], rgb[1], rgb[2], 1f);
                else
                    errors.Add("hub layout cardPreviews.levels['" + id + "'] needs rgb[3]");
            }
        }

        private static bool IsPowerOfTwo(int n)
        {
            return n > 0 && (n & (n - 1)) == 0;
        }

        /// <summary>The texture or sprite spec whose file is <paramref name="assetPath"/>, or null.</summary>
        public TextureSpec FindTextureByFile(string assetPath)
        {
            foreach (TextureSpec spec in Textures.Values)
            {
                if (string.Equals(spec.File, assetPath, StringComparison.Ordinal)) return spec;
            }
            return null;
        }

        /// <summary>Hub-local position of a board's canvas centre (the canvas plane, facing the reader).</summary>
        public Vector3 CanvasCenter(Board board)
        {
            return board.Point(0f, 0f, CanvasDepthM);
        }

        /// <summary>
        /// The empty the Blender script puts at a board's canvas centre; the assembler compares
        /// the imported anchor with <see cref="CanvasCenter"/> to catch an axis or scale error in
        /// the FBX (the board's frame would then not sit behind its canvas).
        /// </summary>
        public static string BoardAnchorName(string boardId)
        {
            return "Anchor_Board_" + boardId;
        }
    }
}
