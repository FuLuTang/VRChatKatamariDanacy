using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// The guard for the island move. A level moved to an island is shifted by its shell's
    /// transform, which carries everything that reads a <c>Transform</c>; a position stored
    /// inside a component does not move. Every serialized field in the project that could hold
    /// a position must therefore be classified in <c>IslandWorldFields</c> (shifted by the
    /// importer, a size or local value, a direction, a component an island never takes, or
    /// written with the offset already added). A new such field without a classification fails
    /// here, instead of becoming a level that plays 1200 m away from its own data.
    /// </summary>
    /// <remarks>
    /// What counts as "could hold a position": a <c>[SerializeField]</c> field of type
    /// <c>Vector3</c>, <c>Vector3[]</c>, <c>Quaternion</c>, <c>Quaternion[]</c>,
    /// <c>Vector2</c> or <c>Vector2[]</c>, or a <c>float[]</c> whose name contains point,
    /// centre, position or world (case-insensitive) - the flat x,y,z lists such as
    /// <c>MotionHost.pathPoints</c>. Scanned: every type in the <c>VRCKatamari.*</c>
    /// namespaces except the test assembly's own. Reflection into the editor list, like the
    /// other editor-code tests.
    /// </remarks>
    public class WorldCoordinateFieldTests
    {
        private const string FieldsTypeName = "VRCKatamari.Editor.Hub.IslandWorldFields";

        private Type fieldsType;

        [SetUp]
        public void SetUp()
        {
            fieldsType = FindType(FieldsTypeName);
            Assert.IsNotNull(fieldsType, FieldsTypeName + " not loaded");
        }

        [Test]
        public void EverySerializedPositionLikeFieldIsClassified()
        {
            List<string> found = FindPositionLikeFields();
            Assert.Greater(found.Count, 0, "the scan found no fields at all; it is broken, not clean");

            var unclassified = new List<string>();
            for (int i = 0; i < found.Count; i++)
            {
                object[] args = { found[i], null };
                if (!(bool)Invoke("TryClassify", args)) unclassified.Add(found[i]);
            }

            Assert.AreEqual(0, unclassified.Count,
                unclassified.Count + " of " + found.Count + " position-like serialized field(s) are not"
                + " classified in IslandWorldFields (Editor/Hub/IslandMath.cs). Decide for each whether"
                + " an island move must add the offset to it, then add a row: "
                + string.Join(", ", unclassified.ToArray()));
        }

        [Test]
        public void EveryClassifiedKeyStillNamesARealField()
        {
            List<string> found = FindPositionLikeFields();
            var set = new HashSet<string>(found);

            var typeNames = new HashSet<string>();
            foreach (Type type in ProjectTypes()) typeNames.Add(type.Name);

            var stale = new List<string>();
            var keys = (string[])Invoke("AllKeys", new object[0]);
            for (int i = 0; i < keys.Length; i++)
            {
                if (set.Contains(keys[i])) continue;
                // A key whose whole type is not loaded in this run cannot be judged here.
                string typeName = keys[i].Substring(0, keys[i].IndexOf('.'));
                if (!typeNames.Contains(typeName) && !DefaultRuntimeAssemblyLoaded()) continue;
                stale.Add(keys[i]);
            }

            Assert.AreEqual(0, stale.Count,
                "IslandWorldFields classifies field(s) that no longer exist as serialized position-like"
                + " fields (renamed, removed or changed type): " + string.Join(", ", stale.ToArray()));
        }

        [Test]
        public void TheTwoFieldsTheImporterShiftsAreClassifiedAsShifted()
        {
            Assert.AreEqual("ShiftedWithIsland", ClassOf("TerrainFieldHost.vertices"));
            Assert.AreEqual("ShiftedWithIsland", ClassOf("MotionHost.pathPoints"));
        }

        [Test]
        public void OnlyTheTwoShiftedFieldsAreShifted()
        {
            // The importer's ShiftWorldData handles exactly these two; a third "ShiftedWithIsland"
            // row without importer code behind it would be a promise nothing keeps.
            var shifted = new List<string>();
            var keys = (string[])Invoke("AllKeys", new object[0]);
            for (int i = 0; i < keys.Length; i++)
            {
                if (ClassOf(keys[i]) == "ShiftedWithIsland") shifted.Add(keys[i]);
            }
            CollectionAssert.AreEquivalent(
                new[] { "TerrainFieldHost.vertices", "MotionHost.pathPoints" }, shifted);
        }

        [Test]
        public void EveryTypeThatAnIslandMustNotTakeExistsInTheProject()
        {
            var names = (string[])fieldsType.GetField("NotImportedTypes",
                BindingFlags.Public | BindingFlags.Static).GetValue(null);
            Assert.Greater(names.Length, 0);

            var known = new HashSet<string>();
            foreach (Type type in ProjectTypes()) known.Add(type.Name);
            for (int i = 0; i < names.Length; i++)
            {
                if (!known.Contains(names[i]) && !DefaultRuntimeAssemblyLoaded()) continue;
                Assert.IsTrue(known.Contains(names[i]), "NotImportedTypes names '" + names[i]
                    + "', which is not a type of the project any more");
            }
        }

        // ------------------------------------------------------------------ the scan

        private static List<string> FindPositionLikeFields()
        {
            var keys = new List<string>();
            foreach (Type type in ProjectTypes())
            {
                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public
                        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch (Exception exception) when (IsLoadFailure(exception))
                {
                    continue;
                }
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    try
                    {
                        if (field.GetCustomAttributes(typeof(SerializeField), false).Length == 0) continue;
                        if (!IsPositionLike(field)) continue;
                    }
                    catch (Exception exception) when (IsLoadFailure(exception))
                    {
                        continue;
                    }
                    keys.Add(type.Name + "." + field.Name);
                }
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        // A type or field whose dependency assembly is not loaded in this run (the local runner
        // does not load Unity's default runtime assembly, where Labs/ lives) cannot be judged
        // here; in Unity everything loads. Skipping is limited to load failures.
        private static bool IsLoadFailure(Exception exception)
        {
            return exception is System.IO.FileNotFoundException
                || exception is System.IO.FileLoadException
                || exception is TypeLoadException
                || exception is ReflectionTypeLoadException;
        }

        private static bool IsPositionLike(FieldInfo field)
        {
            Type t = field.FieldType;
            if (t == typeof(Vector3) || t == typeof(Vector3[])) return true;
            if (t == typeof(Quaternion) || t == typeof(Quaternion[])) return true;
            if (t == typeof(Vector2) || t == typeof(Vector2[])) return true;
            if (t != typeof(float[])) return false;
            string name = field.Name.ToLowerInvariant();
            return name.Contains("point") || name.Contains("center") || name.Contains("position")
                || name.Contains("world");
        }

        private static bool DefaultRuntimeAssemblyLoaded()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (assemblies[i].GetName().Name == "Assembly-CSharp") return true;
            }
            return false;
        }

        private static IEnumerable<Type> ProjectTypes()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try
                {
                    types = assemblies[a].GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    // An assembly whose dependencies are not all present still yields the
                    // types that did load; skipping it whole would hide project types.
                    types = exception.Types;
                }
                catch (Exception exception) when (IsLoadFailure(exception))
                {
                    continue;
                }
                if (types == null) continue;
                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type == null) continue;
                    bool keep;
                    try
                    {
                        string ns = type.Namespace;
                        keep = type.IsClass && ns != null
                            && ns.StartsWith("VRCKatamari", StringComparison.Ordinal)
                            && !ns.StartsWith("VRCKatamari.Tests", StringComparison.Ordinal);
                    }
                    catch (Exception exception) when (IsLoadFailure(exception))
                    {
                        keep = false;
                    }
                    if (keep) yield return type;
                }
            }
        }

        private string ClassOf(string key)
        {
            object[] args = { key, null };
            Assert.IsTrue((bool)Invoke("TryClassify", args), key + " is not classified");
            return args[1].ToString();
        }

        private object Invoke(string name, object[] args)
        {
            MethodInfo method = fieldsType.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "IslandWorldFields." + name + " not found");
            return method.Invoke(null, args);
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
