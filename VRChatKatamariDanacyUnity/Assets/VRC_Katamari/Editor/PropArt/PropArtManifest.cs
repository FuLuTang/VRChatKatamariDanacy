using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace VRCKatamari.Editor.PropArt
{
    /// <summary>
    /// The part of <c>&lt;def&gt;.manifest.json</c> the material pipeline needs: the def id,
    /// the material slot names and their linear base colours.
    /// </summary>
    /// <remarks>
    /// The manifest is the Blender-side contract written by
    /// <c>Docs/Art/OriginalModels/props/scripts/prop_lib.py</c>
    /// (<c>Builder.finish()</c>, the <c>materials</c> and <c>material_base_colors</c> keys).
    /// It is copied next to the FBX so the Unity project is self-contained; it is editor
    /// data, not runtime data, and nothing under <c>Runtime/</c> reads it.
    /// </remarks>
    public sealed class PropArtManifest
    {
        /// <summary>The <c>id</c> field; must equal the def folder name.</summary>
        public string Id;

        /// <summary>
        /// Full material slot names in declaration order, e.g. <c>Prop_acorn_nut</c>.
        /// These are exactly the names <c>prop_lib.Builder.material()</c> gives the Blender
        /// materials, so they are also the names the FBX stores (see <see cref="PropArtManifests"/>).
        /// </summary>
        public string[] Slots;

        /// <summary>Linear RGB base colours parallel to <see cref="Slots"/>.</summary>
        public Color[] BaseColors;
    }

    /// <summary>
    /// Reads the material fields out of a prop manifest. Pure: no asset, file or importer
    /// access, which is why the rules are testable without an Editor object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a private JSON reader instead of <c>JsonUtility</c>: the interesting field,
    /// <c>material_base_colors</c>, is a JSON object whose keys are material names, and
    /// <c>JsonUtility</c> cannot deserialize a dictionary. <c>Newtonsoft.Json</c> is only an
    /// indirect package dependency here, so using it would make the editor assembly depend
    /// on a package that isn't declared in <c>Packages/manifest.json</c>. The reader below
    /// handles exactly the JSON subset the manifests use (objects, arrays, strings,
    /// numbers, booleans, null) and refuses anything malformed instead of guessing.
    /// </para>
    /// <para>
    /// Colour space: the manifests store Blender Principled Base Color values, which are
    /// linear. The project is in Linear colour space (<c>m_ActiveColorSpace: 1</c>), and the
    /// existing hand-made materials store those same numbers verbatim in <c>_Color</c>, so
    /// this code copies the values without a gamma conversion. Changing the project to Gamma
    /// colour space would require a conversion here; the tool does not do one silently.
    /// </para>
    /// <para>
    /// Slot names: <c>prop_lib.py</c> builds every material name as
    /// <c>"Prop_%s_%s" % (self.pid, key)</c>, and <c>Builder.finish()</c> writes the same
    /// names into <c>materials</c> and <c>material_base_colors</c>. The FBX binary stores
    /// those strings unchanged (verified across all 48 exports), and Unity's model importer
    /// uses the FBX material name as the imported material name. The expected mapping is
    /// therefore the identity; <see cref="PropArtMaterials.TryResolveSlot"/> only adds
    /// fallbacks for hand-edited files.
    /// </para>
    /// </remarks>
    public static class PropArtManifests
    {
        /// <summary>
        /// Parses <paramref name="json"/> and checks the material contract. When
        /// <paramref name="expectedDef"/> is non-empty the manifest id must match it, so a
        /// manifest copied into the wrong def folder is refused instead of applied.
        /// </summary>
        public static bool TryParse(string json, string expectedDef, out PropArtManifest manifest,
            out string error)
        {
            manifest = null;
            error = null;

            PropArtJsonValue root;
            string parseError;
            if (!PropArtJson.TryParse(json, out root, out parseError))
            {
                error = "manifest is not valid JSON: " + parseError;
                return false;
            }

            if (root.Kind != PropArtJsonKind.Object)
            {
                error = "manifest root is not a JSON object";
                return false;
            }

            PropArtJsonValue idValue;
            if (!root.Members.TryGetValue("id", out idValue) || idValue.Kind != PropArtJsonKind.String ||
                string.IsNullOrEmpty(idValue.Text))
            {
                error = "manifest has no non-empty string 'id'";
                return false;
            }

            if (!string.IsNullOrEmpty(expectedDef) &&
                !string.Equals(idValue.Text, expectedDef, StringComparison.Ordinal))
            {
                error = "manifest id '" + idValue.Text + "' does not match the def folder '" + expectedDef + "'";
                return false;
            }

            PropArtJsonValue materialsValue;
            if (!root.Members.TryGetValue("materials", out materialsValue) ||
                materialsValue.Kind != PropArtJsonKind.Array || materialsValue.Items.Count == 0)
            {
                error = "manifest has no non-empty 'materials' array";
                return false;
            }

            int slotCount = materialsValue.Items.Count;
            string[] slots = new string[slotCount];
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < slotCount; i++)
            {
                PropArtJsonValue item = materialsValue.Items[i];
                if (item.Kind != PropArtJsonKind.String || string.IsNullOrEmpty(item.Text))
                {
                    error = "manifest 'materials[" + i + "]' is not a non-empty string";
                    return false;
                }

                if (!seen.Add(item.Text))
                {
                    error = "manifest lists material '" + item.Text + "' twice";
                    return false;
                }

                slots[i] = item.Text;
            }

            PropArtJsonValue colorsValue;
            if (!root.Members.TryGetValue("material_base_colors", out colorsValue) ||
                colorsValue.Kind != PropArtJsonKind.Object)
            {
                error = "manifest has no 'material_base_colors' object";
                return false;
            }

            Color[] colors = new Color[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                PropArtJsonValue colorValue;
                if (!colorsValue.Members.TryGetValue(slots[i], out colorValue) ||
                    colorValue.Kind != PropArtJsonKind.Array || colorValue.Items.Count != 3)
                {
                    error = "manifest 'material_base_colors' has no 3-number entry for '" + slots[i] + "'";
                    return false;
                }

                float[] rgb = new float[3];
                for (int channel = 0; channel < 3; channel++)
                {
                    PropArtJsonValue number = colorValue.Items[channel];
                    if (number.Kind != PropArtJsonKind.Number || double.IsNaN(number.Number) ||
                        double.IsInfinity(number.Number) || number.Number < 0.0 || number.Number > 1.0)
                    {
                        error = "manifest colour '" + slots[i] + "' has a channel outside [0, 1]";
                        return false;
                    }

                    rgb[channel] = (float)number.Number;
                }

                colors[i] = new Color(rgb[0], rgb[1], rgb[2], 1f);
            }

            foreach (string colorKey in colorsValue.Members.Keys)
            {
                if (!seen.Contains(colorKey))
                {
                    error = "manifest 'material_base_colors' names '" + colorKey +
                            "', which is not in 'materials'";
                    return false;
                }
            }

            manifest = new PropArtManifest
            {
                Id = idValue.Text,
                Slots = slots,
                BaseColors = colors,
            };
            return true;
        }
    }

    internal enum PropArtJsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    internal sealed class PropArtJsonValue
    {
        public PropArtJsonKind Kind;
        public bool Bool;
        public double Number;
        public string Text;
        public List<PropArtJsonValue> Items;
        public Dictionary<string, PropArtJsonValue> Members;
    }

    /// <summary>Minimal, strict JSON reader for the manifests (no third-party dependency).</summary>
    internal static class PropArtJson
    {
        private const int MaxDepth = 64;

        public static bool TryParse(string text, out PropArtJsonValue value, out string error)
        {
            value = null;
            error = null;
            if (text == null)
            {
                error = "empty text";
                return false;
            }

            int index = 0;
            // A UTF-8 BOM is not JSON whitespace; tolerate one because a Windows editor may
            // have saved the manifest with it.
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                index = 1;
            }

            try
            {
                value = ParseValue(text, ref index, 0);
                SkipWhitespace(text, ref index);
                if (index != text.Length)
                {
                    throw Error(text, index, "unexpected content after the JSON value");
                }

                return true;
            }
            catch (FormatException exception)
            {
                value = null;
                error = exception.Message;
                return false;
            }
        }

        private static PropArtJsonValue ParseValue(string text, ref int index, int depth)
        {
            if (depth > MaxDepth)
            {
                throw Error(text, index, "JSON nesting is deeper than " + MaxDepth);
            }

            SkipWhitespace(text, ref index);
            if (index >= text.Length)
            {
                throw Error(text, index, "unexpected end of JSON");
            }

            char c = text[index];
            switch (c)
            {
                case '{':
                    return ParseObject(text, ref index, depth + 1);
                case '[':
                    return ParseArray(text, ref index, depth + 1);
                case '"':
                    return new PropArtJsonValue { Kind = PropArtJsonKind.String, Text = ParseString(text, ref index) };
                case 't':
                    ExpectLiteral(text, ref index, "true");
                    return new PropArtJsonValue { Kind = PropArtJsonKind.Bool, Bool = true };
                case 'f':
                    ExpectLiteral(text, ref index, "false");
                    return new PropArtJsonValue { Kind = PropArtJsonKind.Bool, Bool = false };
                case 'n':
                    ExpectLiteral(text, ref index, "null");
                    return new PropArtJsonValue { Kind = PropArtJsonKind.Null };
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        return ParseNumber(text, ref index);
                    }

                    throw Error(text, index, "unexpected character '" + c + "'");
            }
        }

        private static PropArtJsonValue ParseObject(string text, ref int index, int depth)
        {
            index++; // '{'
            var members = new Dictionary<string, PropArtJsonValue>(StringComparer.Ordinal);
            SkipWhitespace(text, ref index);
            if (Peek(text, index) == '}')
            {
                index++;
                return new PropArtJsonValue { Kind = PropArtJsonKind.Object, Members = members };
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (Peek(text, index) != '"')
                {
                    throw Error(text, index, "expected an object key string");
                }

                string key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                if (Peek(text, index) != ':')
                {
                    throw Error(text, index, "expected ':' after object key '" + key + "'");
                }

                index++;
                PropArtJsonValue value = ParseValue(text, ref index, depth);
                if (members.ContainsKey(key))
                {
                    throw Error(text, index, "duplicate object key '" + key + "'");
                }

                members.Add(key, value);
                SkipWhitespace(text, ref index);
                char separator = Peek(text, index);
                if (separator == ',')
                {
                    index++;
                    continue;
                }

                if (separator == '}')
                {
                    index++;
                    return new PropArtJsonValue { Kind = PropArtJsonKind.Object, Members = members };
                }

                throw Error(text, index, "expected ',' or '}' in object");
            }
        }

        private static PropArtJsonValue ParseArray(string text, ref int index, int depth)
        {
            index++; // '['
            var items = new List<PropArtJsonValue>();
            SkipWhitespace(text, ref index);
            if (Peek(text, index) == ']')
            {
                index++;
                return new PropArtJsonValue { Kind = PropArtJsonKind.Array, Items = items };
            }

            while (true)
            {
                items.Add(ParseValue(text, ref index, depth));
                SkipWhitespace(text, ref index);
                char separator = Peek(text, index);
                if (separator == ',')
                {
                    index++;
                    continue;
                }

                if (separator == ']')
                {
                    index++;
                    return new PropArtJsonValue { Kind = PropArtJsonKind.Array, Items = items };
                }

                throw Error(text, index, "expected ',' or ']' in array");
            }
        }

        private static string ParseString(string text, ref int index)
        {
            index++; // opening quote
            StringBuilder builder = new StringBuilder();
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c == '\\')
                {
                    if (index >= text.Length)
                    {
                        throw Error(text, index, "unterminated string escape");
                    }

                    char escape = text[index++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (index + 4 > text.Length)
                            {
                                throw Error(text, index, "truncated \\u escape");
                            }

                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int digit = HexDigit(text[index + i]);
                                if (digit < 0)
                                {
                                    throw Error(text, index + i, "invalid \\u escape");
                                }

                                code = (code << 4) | digit;
                            }

                            index += 4;
                            builder.Append((char)code);
                            break;
                        default:
                            throw Error(text, index - 1, "invalid string escape '\\" + escape + "'");
                    }

                    continue;
                }

                if (c < 0x20)
                {
                    throw Error(text, index - 1, "control character in string");
                }

                builder.Append(c);
            }

            throw Error(text, index, "unterminated string");
        }

        private static PropArtJsonValue ParseNumber(string text, ref int index)
        {
            int start = index;
            if (Peek(text, index) == '-')
            {
                index++;
            }

            int integerStart = index;
            while (index < text.Length && text[index] >= '0' && text[index] <= '9')
            {
                index++;
            }

            if (index == integerStart)
            {
                throw Error(text, index, "number has no integer part");
            }

            if (Peek(text, index) == '.')
            {
                index++;
                int fractionStart = index;
                while (index < text.Length && text[index] >= '0' && text[index] <= '9')
                {
                    index++;
                }

                if (index == fractionStart)
                {
                    throw Error(text, index, "number has no fraction digits");
                }
            }

            if (Peek(text, index) == 'e' || Peek(text, index) == 'E')
            {
                index++;
                if (Peek(text, index) == '+' || Peek(text, index) == '-')
                {
                    index++;
                }

                int exponentStart = index;
                while (index < text.Length && text[index] >= '0' && text[index] <= '9')
                {
                    index++;
                }

                if (index == exponentStart)
                {
                    throw Error(text, index, "number has no exponent digits");
                }
            }

            string token = text.Substring(start, index - start);
            double number;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                throw Error(text, start, "invalid number '" + token + "'");
            }

            return new PropArtJsonValue { Kind = PropArtJsonKind.Number, Number = number };
        }

        private static void ExpectLiteral(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw Error(text, index, "expected '" + literal + "'");
            }

            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                {
                    return;
                }

                index++;
            }
        }

        private static char Peek(string text, int index)
        {
            return index < text.Length ? text[index] : '\0';
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static FormatException Error(string text, int index, string message)
        {
            int line = 1;
            int column = 1;
            int limit = Mathf.Min(index, text.Length);
            for (int i = 0; i < limit; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return new FormatException(message + " (line " + line + ", column " + column + ")");
        }
    }
}
