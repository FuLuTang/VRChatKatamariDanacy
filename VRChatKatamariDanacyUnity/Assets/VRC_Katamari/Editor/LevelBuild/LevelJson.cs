using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>The JSON value kinds this reader distinguishes.</summary>
    public enum LevelJsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// One node of a parsed JSON document: the tree shape <c>LEVEL_FORMAT_V2</c> needs and
    /// <c>JsonUtility</c> cannot express.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists at all. <c>JsonUtility</c> has no jagged arrays, so
    /// <c>meta.bounds</c> (<c>[[minX,minZ],[maxX,maxZ]]</c>) and
    /// <c>activityArea.walls[].points</c> (<c>[[x,z],...]</c>) cannot be deserialized into a
    /// <c>[Serializable]</c> class. Worse, it fills every nested <c>[Serializable]</c> field
    /// with a default instance even when the key is absent, so <c>field != null</c> always
    /// says "present" (the trap <see cref="PickupFieldAuthoringRules.IsColliderDeclared"/>
    /// documents for <c>defs[].collider</c>). A tree has neither problem: an absent key is
    /// <c>Get(name) == null</c>, an explicit <c>null</c> is a Null node, and an empty object
    /// is an Object node with no fields - three different, testable states.
    /// </para>
    /// <para>
    /// No <c>UnityEngine</c> dependency on purpose: this type and
    /// <see cref="PlacementV2Reader"/> are the host-agnostic layer (numbers, strings, tree),
    /// so they can be compiled and exercised by any C# compiler outside Unity as well as by
    /// the EditMode tests inside it. Geometry stays in <c>float[]</c> for the same reason and
    /// because the v1 DTOs (<c>props[].position</c>, <c>aabb</c>) already do.
    /// </para>
    /// <para>
    /// Determinism. Numbers go through <c>double</c> with the invariant culture; a duplicate
    /// object key keeps the last value (JSON's own "last wins"); an empty input and trailing
    /// garbage are errors, not silent nulls. Those are the same three choices the private
    /// reader in <c>TerrainMeshImporter</c> already made, so replacing it with this class is
    /// behaviour-preserving.
    /// </para>
    /// </remarks>
    public sealed class LevelJsonNode
    {
        /// <summary>Which kind this node is. Only the matching value field is meaningful.</summary>
        public LevelJsonKind Kind;

        public bool BoolValue;
        public double NumberValue;
        public string StringValue;

        /// <summary>Array items; null for every other kind.</summary>
        public List<LevelJsonNode> Items;

        /// <summary>Object fields; null for every other kind.</summary>
        public Dictionary<string, LevelJsonNode> Fields;

        public int Count
        {
            get { return Items == null ? 0 : Items.Count; }
        }

        public bool IsObject
        {
            get { return Kind == LevelJsonKind.Object; }
        }

        public bool IsArray
        {
            get { return Kind == LevelJsonKind.Array; }
        }

        public bool IsNull
        {
            get { return Kind == LevelJsonKind.Null; }
        }

        public LevelJsonNode At(int index)
        {
            return Items != null && index >= 0 && index < Items.Count ? Items[index] : null;
        }

        public LevelJsonNode Get(string name)
        {
            if (name == null || Fields == null) return null;
            LevelJsonNode node;
            return Fields.TryGetValue(name, out node) ? node : null;
        }

        /// <summary>
        /// True when the key EXISTS, whatever its value - including an explicit <c>null</c>.
        /// </summary>
        /// <remarks>
        /// This is the one method that answers "was the key written?", and it is why the v2
        /// reader never needs the <c>field != null</c> presence test: <c>Has("parent")</c> is
        /// false for <c>{"parent": null}</c> only if the author wrote no key at all, while
        /// <c>Get("parent")</c> returns a Null node for a written null. Callers that only care
        /// about a usable value use <see cref="GetString"/> and friends.
        /// </remarks>
        public bool Has(string name)
        {
            return Fields != null && name != null && Fields.ContainsKey(name);
        }

        /// <summary>The object's keys, in the order the file wrote them (empty for non-objects).</summary>
        public string[] KeyArray()
        {
            if (Fields == null || Fields.Count == 0) return new string[0];
            var keys = new string[Fields.Count];
            int index = 0;
            foreach (KeyValuePair<string, LevelJsonNode> pair in Fields)
            {
                keys[index] = pair.Key;
                index++;
            }

            Array.Sort(keys, StringComparer.Ordinal);
            return keys;
        }

        public string GetString(string name)
        {
            LevelJsonNode node = Get(name);
            return node == null ? null : node.AsString();
        }

        public string GetString(string name, string fallback)
        {
            LevelJsonNode node = Get(name);
            if (node == null || node.Kind == LevelJsonKind.Null) return fallback;
            string value = node.AsString();
            return value == null ? fallback : value;
        }

        public bool GetBool(string name, bool fallback)
        {
            LevelJsonNode node = Get(name);
            if (node == null) return fallback;
            if (node.Kind == LevelJsonKind.Bool) return node.BoolValue;
            if (node.Kind == LevelJsonKind.Number) return node.NumberValue != 0.0;
            if (node.Kind == LevelJsonKind.String)
            {
                string text = node.StringValue;
                return text != null && string.Equals(text.Trim(), "true",
                    StringComparison.OrdinalIgnoreCase);
            }

            return fallback;
        }

        public double GetDouble(string name, double fallback)
        {
            LevelJsonNode node = Get(name);
            return node == null ? fallback : node.AsDouble();
        }

        public float GetFloat(string name, float fallback)
        {
            LevelJsonNode node = Get(name);
            return node == null ? fallback : node.AsFloat();
        }

        public int GetInt(string name, int fallback)
        {
            LevelJsonNode node = Get(name);
            return node == null ? fallback : node.AsInt();
        }

        public long GetLong(string name, long fallback)
        {
            LevelJsonNode node = Get(name);
            return node == null ? fallback : node.AsLong();
        }

        public string AsString()
        {
            return Kind == LevelJsonKind.String ? StringValue : null;
        }

        public double AsDouble()
        {
            return Kind == LevelJsonKind.Number ? NumberValue : 0.0;
        }

        public float AsFloat()
        {
            return Kind == LevelJsonKind.Number ? (float)NumberValue : 0f;
        }

        public int AsInt()
        {
            if (Kind != LevelJsonKind.Number) return 0;
            double rounded = Math.Round(NumberValue);
            if (rounded > int.MaxValue) return int.MaxValue;
            if (rounded < int.MinValue) return int.MinValue;
            return (int)rounded;
        }

        public long AsLong()
        {
            if (Kind != LevelJsonKind.Number) return 0L;
            double rounded = Math.Round(NumberValue);
            if (rounded > long.MaxValue) return long.MaxValue;
            if (rounded < long.MinValue) return long.MinValue;
            return (long)rounded;
        }
    }

    /// <summary>
    /// Reads JSON text into a <see cref="LevelJsonNode"/> tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deliberately small recursive-descent reader: objects, arrays, strings with the seven
    /// escapes and <c>\uXXXX</c>, numbers through <c>double</c> with the invariant culture,
    /// <c>true</c>/<c>false</c>/<c>null</c>. No comments, no trailing commas - the files are
    /// written by our own canonical writer, so accepting more would only hide damage.
    /// </para>
    /// <para>
    /// Errors are <see cref="FormatException"/> with the byte offset, because the one caller
    /// that must not throw (<see cref="PlacementV2Reader.Read"/>) wraps the call and turns the
    /// message into a report error. <see cref="ParseText"/> strips a UTF-8 BOM first: the
    /// writer emits plain UTF-8, but Windows editors add the BOM and a v2 document that
    /// refuses to open because of it would be a pointless papercut.
    /// </para>
    /// </remarks>
    public sealed class LevelJsonParser
    {
        /// <summary>Guards the recursion; no real level document nests anywhere near this.</summary>
        private const int MaxDepth = 64;

        private readonly string text;
        private int index;
        private int depth;

        public LevelJsonParser(string text)
        {
            this.text = text ?? "";
        }

        /// <summary>Strips a leading BOM, then parses. Never returns null; throws on bad input.</summary>
        public static LevelJsonNode ParseText(string text)
        {
            if (!string.IsNullOrEmpty(text) && text[0] == '\uFEFF') text = text.Substring(1);
            return new LevelJsonParser(text).Parse();
        }

        public LevelJsonNode Parse()
        {
            LevelJsonNode node = ParseValue();
            SkipWhitespace();
            if (index != text.Length)
            {
                throw new FormatException("trailing JSON at " + index);
            }

            return node;
        }

        private LevelJsonNode ParseValue()
        {
            SkipWhitespace();
            if (index >= text.Length) throw new FormatException("unexpected end of JSON");
            char c = text[index];
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return new LevelJsonNode { Kind = LevelJsonKind.String, StringValue = ParseString() };
            if (c == 't' || c == 'f')
            {
                bool value = c == 't';
                Expect(value ? "true" : "false");
                return new LevelJsonNode { Kind = LevelJsonKind.Bool, BoolValue = value };
            }

            if (c == 'n')
            {
                Expect("null");
                return new LevelJsonNode { Kind = LevelJsonKind.Null };
            }

            return new LevelJsonNode { Kind = LevelJsonKind.Number, NumberValue = ParseNumber() };
        }

        private LevelJsonNode ParseObject()
        {
            if (++depth > MaxDepth) throw new FormatException("JSON nests deeper than " + MaxDepth);
            index++; // {
            var fields = new Dictionary<string, LevelJsonNode>(StringComparer.Ordinal);
            SkipWhitespace();
            if (index < text.Length && text[index] == '}')
            {
                index++;
                depth--;
                return new LevelJsonNode { Kind = LevelJsonKind.Object, Fields = fields };
            }

            while (true)
            {
                SkipWhitespace();
                string key = ParseString();
                SkipWhitespace();
                if (index >= text.Length || text[index] != ':')
                {
                    throw new FormatException("expected ':' at " + index);
                }

                index++;
                // Duplicate keys: last wins, which is what a JSON consumer is allowed to do
                // and what the previous reader did.
                fields[key] = ParseValue();
                SkipWhitespace();
                if (index >= text.Length) throw new FormatException("unterminated object");
                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == '}')
                {
                    index++;
                    break;
                }

                throw new FormatException("expected ',' or '}' at " + index);
            }

            depth--;
            return new LevelJsonNode { Kind = LevelJsonKind.Object, Fields = fields };
        }

        private LevelJsonNode ParseArray()
        {
            if (++depth > MaxDepth) throw new FormatException("JSON nests deeper than " + MaxDepth);
            index++; // [
            var items = new List<LevelJsonNode>();
            SkipWhitespace();
            if (index < text.Length && text[index] == ']')
            {
                index++;
                depth--;
                return new LevelJsonNode { Kind = LevelJsonKind.Array, Items = items };
            }

            while (true)
            {
                items.Add(ParseValue());
                SkipWhitespace();
                if (index >= text.Length) throw new FormatException("unterminated array");
                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == ']')
                {
                    index++;
                    break;
                }

                throw new FormatException("expected ',' or ']' at " + index);
            }

            depth--;
            return new LevelJsonNode { Kind = LevelJsonKind.Array, Items = items };
        }

        private string ParseString()
        {
            if (index >= text.Length || text[index] != '"')
            {
                throw new FormatException("expected string at " + index);
            }

            index++;
            var value = new StringBuilder();
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') return value.ToString();
                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }

                if (index >= text.Length) break;
                char escape = text[index++];
                switch (escape)
                {
                    case '"':
                        value.Append('"');
                        break;
                    case '\\':
                        value.Append('\\');
                        break;
                    case '/':
                        value.Append('/');
                        break;
                    case 'b':
                        value.Append('\b');
                        break;
                    case 'f':
                        value.Append('\f');
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 'r':
                        value.Append('\r');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    case 'u':
                        if (index + 4 > text.Length) throw new FormatException("bad \\u escape");
                        value.Append((char)int.Parse(text.Substring(index, 4),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        throw new FormatException("bad escape \\" + escape);
                }
            }

            throw new FormatException("unterminated string");
        }

        private double ParseNumber()
        {
            int start = index;
            while (index < text.Length && "-+.eE0123456789".IndexOf(text[index]) >= 0) index++;
            if (start == index) throw new FormatException("expected number at " + index);
            return double.Parse(text.Substring(start, index - start), NumberStyles.Float,
                CultureInfo.InvariantCulture);
        }

        private void Expect(string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("expected " + literal + " at " + index);
            }

            index += literal.Length;
        }

        private void SkipWhitespace()
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }
    }

    /// <summary>Converts tree nodes into the flat value shapes the level model uses.</summary>
    public static class LevelJsonValues
    {
        /// <summary>
        /// Reads <c>count</c> numbers. Returns false when the node is not an array of exactly
        /// that many finite numbers; <paramref name="values"/> is then null.
        /// </summary>
        public static bool TryNumbers(LevelJsonNode node, int count, out float[] values)
        {
            values = null;
            if (node == null || node.Kind != LevelJsonKind.Array || node.Count != count) return false;
            var result = new float[count];
            for (int i = 0; i < count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Number || !IsFinite(item.NumberValue))
                    return false;
                result[i] = (float)item.NumberValue;
            }

            values = result;
            return true;
        }

        /// <summary>Reads any number of finite numbers (used for a flat collider hull).</summary>
        public static bool TryNumbersFlat(LevelJsonNode node, out float[] values)
        {
            values = null;
            if (node == null || node.Kind != LevelJsonKind.Array) return false;
            var result = new float[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                if (item == null || item.Kind != LevelJsonKind.Number || !IsFinite(item.NumberValue))
                    return false;
                result[i] = (float)item.NumberValue;
            }

            values = result;
            return true;
        }

        /// <summary>
        /// Reads an array of equal-length number arrays - the shape of <c>meta.bounds</c>
        /// (<c>[[minX,minZ],[maxX,maxZ]]</c>) and of a wall's <c>points</c>
        /// (<c>[[x,z],...]</c>). An empty array yields an empty result, not null.
        /// </summary>
        public static bool TryNumberGroups(LevelJsonNode node, int groupLength, out float[][] groups)
        {
            groups = null;
            if (node == null || node.Kind != LevelJsonKind.Array) return false;
            var result = new float[node.Count][];
            for (int i = 0; i < node.Count; i++)
            {
                float[] group;
                if (!TryNumbers(node.At(i), groupLength, out group)) return false;
                result[i] = group;
            }

            groups = result;
            return true;
        }

        /// <summary>Reads an array of strings; a non-array yields null.</summary>
        public static string[] Strings(LevelJsonNode node)
        {
            if (node == null || node.Kind != LevelJsonKind.Array) return null;
            var result = new string[node.Count];
            for (int i = 0; i < node.Count; i++)
            {
                LevelJsonNode item = node.At(i);
                result[i] = item != null && item.Kind == LevelJsonKind.String ? item.StringValue : null;
            }

            return result;
        }

        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
