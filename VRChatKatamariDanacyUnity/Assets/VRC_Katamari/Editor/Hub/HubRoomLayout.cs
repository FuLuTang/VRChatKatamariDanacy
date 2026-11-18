using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VRCKatamari.Editor.LevelBuild;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// The hub room's art layout, <c>Tools/hubart/hub_layout.json</c>: the three wall boards,
    /// every text box, level card and settings row, in hub-local metres. The Blender script
    /// <c>Tools/hubart/build_hub_room.py</c> builds the model and one <c>Anchor_*</c> empty per
    /// element from the same file; the hub assembler reads it for sizes, glyph heights and the
    /// anchors' expected positions.
    /// </summary>
    /// <remarks>
    /// Frames: hub-local Unity metres, origin <c>HubCenter</c>. A board's readable side faces
    /// its local -Z after a yaw of <c>yawDeg</c> about +Y (the same side a world-space Canvas
    /// shows); on a board x is the reader's right, y up, depth towards the reader.
    /// </remarks>
    internal sealed class HubRoomLayout
    {
        public const string RepoRelativePath = "Tools/hubart/hub_layout.json";
        public const string Schema = "katamari.hub-layout/1";

        public sealed class Board
        {
            public string Id;
            public Vector3 Center;
            public float YawDeg;
            public Vector2 Size;

            public Quaternion Rotation { get { return Quaternion.Euler(0f, YawDeg, 0f); } }

            /// <summary>Hub-local point of a board coordinate (x right, y up, depth to reader).</summary>
            public Vector3 Point(float x, float y, float depth)
            {
                return Center + Rotation * new Vector3(x, y, -depth);
            }
        }

        /// <summary>One text box or button face on a board, in board metres.</summary>
        public sealed class Box
        {
            public string Board;
            public float X;
            public float Y;
            public float W;
            public float H;
            public float Depth;
            public float Em;
            public TextAnchor Align = TextAnchor.MiddleCenter;
            public bool White;
        }

        public sealed class Card
        {
            public int Index;
            public Box Button;
            public Box Preview;
            public Box Name;
            public Box Goal;
            public Box Record;
            public Box Start;
        }

        public sealed class Row
        {
            public int Id;
            public Box Label;
            public Box Value;
            public Box Less;
            public Box More;
        }

        public string ModelPath;
        public string ModelRootName;
        public readonly Dictionary<string, Board> Boards = new Dictionary<string, Board>();
        public readonly Dictionary<string, Box> Texts = new Dictionary<string, Box>();
        public readonly List<Card> Cards = new List<Card>();
        public readonly List<Row> Rows = new List<Row>();
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
                };
                l.Boards[board.Id] = board;
            }

            LevelJsonNode texts = root.Get("texts");
            for (int i = 0; texts != null && i < texts.Count; i++)
            {
                LevelJsonNode t = texts.At(i);
                Box box = ReadBox(t, t.GetString("board"), 0f, 0f, 0f);
                box.White = t.GetString("color", "") == "Hub_White";
                l.Texts[t.GetString("id")] = box;
            }

            LevelJsonNode cards = root.Get("levelCards");
            for (int i = 0; cards != null && i < cards.Count; i++)
            {
                LevelJsonNode n = cards.At(i);
                string board = n.GetString("board");
                float x = n.GetFloat("x", 0f);
                float y = n.GetFloat("y", 0f);
                var card = new Card { Index = n.GetInt("index", i) };
                card.Button = new Box
                {
                    Board = board, X = x, Y = y, W = n.GetFloat("w", 0f), H = n.GetFloat("h", 0f),
                    Depth = n.GetFloat("depth", 0f) + 0.03f,
                };
                card.Preview = ReadBox(n.Get("preview"), board, x, y, 0f);
                card.Name = ReadBox(n.Get("name"), board, x, y, 0f);
                card.Goal = ReadBox(n.Get("goal"), board, x, y, 0f);
                card.Record = ReadBox(n.Get("record"), board, x, y, 0f);
                card.Start = ReadBox(n.Get("start"), board, x, y, 0f);
                card.Start.White = true;
                card.Preview.White = true;
                l.Cards.Add(card);
            }

            LevelJsonNode rows = root.Get("settingRows");
            for (int i = 0; rows != null && i < rows.Count; i++)
            {
                LevelJsonNode n = rows.At(i);
                string board = n.GetString("board");
                float y = n.GetFloat("y", 0f);
                float h = n.GetFloat("h", 0.14f);
                var row = new Row { Id = n.GetInt("id", i) };
                row.Label = ReadBox(n.Get("label"), board, 0f, y, h);
                row.Value = ReadBox(n.Get("value"), board, 0f, y, h);
                row.Less = ReadBox(n.Get("less"), board, 0f, y, h);
                row.More = ReadBox(n.Get("more"), board, 0f, y, h);
                l.Rows.Add(row);
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
            foreach (Box box in l.AllBoxes())
            {
                if (!l.Boards.ContainsKey(box.Board ?? ""))
                {
                    errors.Add("a hub layout element names the unknown board '" + box.Board + "'");
                    break;
                }
            }
            layout = l;
            return errors.Count == 0;
        }

        private static Box ReadBox(LevelJsonNode n, string board, float x0, float y0, float rowH)
        {
            var box = new Box { Board = board };
            if (n == null) return box;
            box.X = x0 + n.GetFloat("x", 0f);
            box.Y = y0 + n.GetFloat("y", 0f);
            box.W = n.GetFloat("w", 0f);
            box.H = n.GetFloat("h", rowH);
            box.Depth = n.GetFloat("depth", 0f);
            box.Em = n.GetFloat("em", 0f);
            string align = n.GetString("align", "center");
            box.Align = align == "left" ? TextAnchor.MiddleLeft
                : align == "right" ? TextAnchor.MiddleRight : TextAnchor.MiddleCenter;
            return box;
        }

        public IEnumerable<Box> AllBoxes()
        {
            foreach (Box b in Texts.Values) yield return b;
            foreach (Card c in Cards)
            {
                yield return c.Button; yield return c.Preview; yield return c.Name;
                yield return c.Goal; yield return c.Record; yield return c.Start;
            }
            foreach (Row r in Rows)
            {
                yield return r.Label; yield return r.Value; yield return r.Less; yield return r.More;
            }
        }

        /// <summary>Hub-local position and rotation of a box (its face centre, facing the reader).</summary>
        public void Pose(Box box, out Vector3 position, out Quaternion rotation)
        {
            Board b = Boards[box.Board];
            position = b.Point(box.X, box.Y, box.Depth);
            rotation = b.Rotation;
        }

        /// <summary>
        /// The anchor name the Blender script gives an element; the assembler compares the
        /// imported anchor with <see cref="Pose"/> to catch an axis or scale error in the FBX.
        /// </summary>
        public static string AnchorName(string kind, int index, string part)
        {
            if (kind == "Text") return "Anchor_Text_" + part;
            if (kind == "Level") return "Anchor_Level" + index + "_" + part;
            return "Anchor_Setting" + index.ToString("00") + "_" + part;
        }
    }
}
