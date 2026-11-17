using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// The one way editor code here reads an UdonSharp behaviour's instance variable table
    /// (the backing <c>UdonBehaviour</c>'s <c>publicVariables</c>), and one-line descriptions
    /// of a field's proxy value, serialized value and table value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists. <c>CopyProxyToUdon</c> reports nothing when it does not move a value,
    /// and a field missing from the table is skipped silently, so the assembler reads the table
    /// back (<c>Labs/README.md</c>, "two tables").
    /// </para>
    /// <para>
    /// <c>publicVariables</c> is a public member of <c>UdonBehaviour</c> in SDK 3.10.5 and is
    /// used as one. An earlier version fetched it with a non-public reflection lookup, which
    /// returns null for a public member: every read then answered "missing" and the read-back
    /// checks that skipped on a null table passed without checking anything.
    /// </para>
    /// </remarks>
    internal static class UdonVariableAccess
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic;

        /// <summary>The proxy's backing table, or null when it has no backing behaviour.</summary>
        public static IUdonVariableTable TableOf(UdonSharpBehaviour proxy)
        {
            if (proxy == null) return null;
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
            return backing == null ? null : backing.publicVariables;
        }

        /// <summary>Reads a variable from the proxy's table; false when there is no table or no such symbol.</summary>
        public static bool TryRead(UdonSharpBehaviour proxy, string name, out object value)
        {
            value = null;
            IUdonVariableTable table = TableOf(proxy);
            return table != null && table.TryGetVariableValue(name, out value);
        }

        /// <summary>
        /// The names among <paramref name="names"/> that the proxy's table does not hold.
        /// Null when the proxy has no table at all (no backing behaviour yet).
        /// </summary>
        public static List<string> MissingNames(UdonSharpBehaviour proxy, string[] names)
        {
            IUdonVariableTable table = TableOf(proxy);
            if (table == null) return null;
            var missing = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                object value;
                if (!table.TryGetVariableValue(names[i], out value)) missing.Add(names[i]);
            }
            return missing;
        }

        /// <summary>
        /// One line: the proxy's in-memory field, its serialized property, and the table value.
        /// No object ids, so it can sit in a report that must repeat exactly.
        /// </summary>
        public static string Describe(UdonSharpBehaviour proxy, string field)
        {
            string memory = "n/a";
            FieldInfo info = proxy.GetType().GetField(field, AnyInstance);
            if (info != null) memory = UdonValueText.Describe(true, info.GetValue(proxy));

            string serialized = "n/a";
            SerializedProperty property = new SerializedObject(proxy).FindProperty(field);
            if (property != null && property.propertyType == SerializedPropertyType.Boolean)
            {
                serialized = property.boolValue.ToString();
            }

            object tableValue;
            bool inTable = TryRead(proxy, field, out tableValue);
            return "proxyField=" + memory + " serialized=" + serialized
                + " table=" + UdonValueText.Describe(inTable, tableValue);
        }

        /// <summary>
        /// Checks that the table holds <paramref name="want"/> for a bool field and appends one
        /// diagnostic line (the three values and the verdict) to <paramref name="lines"/>.
        /// </summary>
        public static bool CheckBool(UdonSharpBehaviour proxy, string field, bool want, string where,
            List<string> lines)
        {
            object value;
            bool found = TryRead(proxy, field, out value);
            bool ok = UdonValueText.IsBool(found, value, want);
            lines.Add("diag " + field + " " + where + ": " + Describe(proxy, field)
                + (ok ? " ok" : " MISMATCH"));
            return ok;
        }
    }
}
