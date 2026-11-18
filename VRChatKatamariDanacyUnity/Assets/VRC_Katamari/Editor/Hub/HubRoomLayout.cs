using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VRCKatamari.Editor.LevelBuild;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// The hub room's layout, <c>Tools/hubart/hub_layout.json</c>: palette, lamps and the three
    /// wall boards' places and sizes, in hub-local metres. The Blender script
    /// <c>Tools/hubart/build_hub_room.py</c> builds the room model (with each board's coloured
    /// frame and one <c>Anchor_Board_&lt;id&gt;</c> empty) from the same file; the hub assembler
    /// places each board's panel prefab (HUB_PANEL_PREFAB.md) at <see cref="CanvasCenter"/> and
    /// checks the board anchors.
    /// </summary>
    /// <remarks>
    /// Frames: hub-local Unity metres, origin <c>HubCenter</c>. A board's readable side faces
    /// its local -Z after a yaw of <c>yawDeg</c> about +Y (the same side a world-space Canvas
    /// shows); on a board x is the reader's right, y up. The canvas plane is
    /// <c>canvasDepthM</c> in front of the board plane (towards the reader).
    /// </remarks>
    internal sealed class HubRoomLayout
    {
        public const string RepoRelativePath = "Tools/hubart/hub_layout.json";
        public const string Schema = "katamari.hub-layout/2";

        public sealed class Board
        {
            public string Id;
            public Vector3 Center;
            public float YawDeg;
            public Vector2 Size;
            /// <summary>Palette name of the board frame (the model; the prefab's title band matches).</summary>
            public string Frame;

            public Quaternion Rotation { get { return Quaternion.Euler(0f, YawDeg, 0f); } }

            /// <summary>Hub-local point of a board coordinate (x right, y up, depth to reader).</summary>
            public Vector3 Point(float x, float y, float depth)
            {
                return Center + Rotation * new Vector3(x, y, -depth);
            }
        }

        public string ModelPath;
        /// <summary>Metres per canvas unit (0.001: one unit is a millimetre).</summary>
        public float CanvasMetresPerUnit = 0.001f;
        /// <summary>Canvas plane, metres in front of the board plane.</summary>
        public float CanvasDepthM = 0.06f;
        public string ModelRootName;
        public readonly Dictionary<string, Board> Boards = new Dictionary<string, Board>();
        public readonly Dictionary<string, Color> Palette = new Dictionary<string, Color>();
        public readonly List<Vector3> Lamps = new List<Vector3>();

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

            var l = new HubRoomLayout();
            LevelJsonNode model = root.Get("model");
            l.ModelPath = model != null ? model.GetString("fbx", "") : "";
            l.ModelRootName = model != null ? model.GetString("rootName", "") : "";
            l.CanvasMetresPerUnit = root.GetFloat("canvasMetresPerUnit", 0.001f);
            l.CanvasDepthM = root.GetFloat("canvasDepthM", 0.06f);
            if (!(l.CanvasMetresPerUnit >= 0.001f && l.CanvasMetresPerUnit <= 0.01f))
            {
                errors.Add("hub layout canvasMetresPerUnit " + l.CanvasMetresPerUnit
                    + " is outside 0.001-0.01 (VRChat's world UI scale range)");
            }

            LevelJsonNode palette = root.Get("palette");
            for (int i = 0; palette != null && i < palette.Count; i++)
            {
                float[] rgb;
                LevelJsonNode e = palette.At(i);
                if (LevelJsonValues.TryNumbers(e.Get("rgb"), 3, out rgb))
                {
                    l.Palette[e.GetString("name")] = new Color(rgb[0], rgb[1], rgb[2], 1f);
                }
            }

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
                    Frame = b.GetString("frame", "Hub_Pink"),
                };
                l.Boards[board.Id] = board;
            }

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
            return errors.Count == 0;
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
