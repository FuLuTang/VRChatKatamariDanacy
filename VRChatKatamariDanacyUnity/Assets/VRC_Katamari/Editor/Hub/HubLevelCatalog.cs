using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRCKatamari.Editor.LevelBuild;
using VRCKatamari.Hub;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// The level-select list as data: which levels the hub can offer, what they are
    /// called, which scene hosts them and where the player lands in each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list lives in <c>Docs/Design/StoryMode/hub_levels.json</c>. Adding a level to the
    /// hub is one entry there plus importing the level into its scene; the assembler turns
    /// the entries a scene can play into pads, launcher entries and names. Nothing about a
    /// level is typed into the scene by hand.
    /// </para>
    /// <para>
    /// An entry is offered in a scene when two things hold: the scene's name is in the
    /// entry's <c>scenes</c> (a ball and a round controller belong to one scene, and one
    /// scene's ball cannot play another scene's level), and the scene actually contains the
    /// level - its <c>content</c> object and its <c>entry</c> object both exist. The second
    /// condition is what makes the list honest in a scene that was copied before the level
    /// was imported: it simply does not offer the level.
    /// </para>
    /// <para>
    /// Entry paths are hierarchy paths from a scene root (<c>LevelContent/_ref_cancer/Markers/PlayerSpawn</c>).
    /// A level imported as an arena cell gets its player start from the importer as
    /// <c>Markers/PlayerSpawn</c>; the first level, a whole level, keeps the hand-placed
    /// <c>LevelEntry</c> object.
    /// </para>
    /// </remarks>
    public static class HubLevelCatalog
    {
        public const string SchemaName = "katamari.hub-levels/0";
        public const string RepoRelativePath = "Docs/Design/StoryMode/hub_levels.json";

        [Serializable]
        public sealed class RoomBlock
        {
            public float padSizeM = 2.8f;
            public float padGapM = 0.6f;
            public float padForwardM = 1.2f;
            public float marginM = 0.5f;
        }

        [Serializable]
        public sealed class LevelEntry
        {
            public string id;
            public string name;
            public string[] scenes;
            public string content;
            public string entry;
            public bool roundFromPlacement;
        }

        [Serializable]
        public sealed class CatalogFile
        {
            public string schema;
            public RoomBlock room;
            public LevelEntry[] levels;
        }

        /// <summary>One catalogue entry resolved against the open scene.</summary>
        public sealed class Resolved
        {
            public LevelEntry Entry;
            public int CatalogueIndex;
            public bool SceneListed;
            public Transform EntryTransform;
            public bool ContentPresent;

            public bool Offered
            {
                get { return SceneListed && ContentPresent && EntryTransform != null; }
            }
        }

        /// <summary>Reads and validates the catalogue file; errors name the entry and the field.</summary>
        public static bool TryLoad(out CatalogFile catalog, List<string> errors)
        {
            catalog = null;

            string root = LevelPaths.RepoRoot();
            if (string.IsNullOrEmpty(root))
            {
                errors.Add("the repository root was not found, so " + RepoRelativePath
                    + " cannot be read (a bare Unity copy has no Docs/)");
                return false;
            }

            string path = Path.Combine(root, RepoRelativePath);
            if (!File.Exists(path))
            {
                errors.Add("no level list at " + path);
                return false;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception exception)
            {
                errors.Add("cannot read " + path + ": " + exception.Message);
                return false;
            }

            return TryParse(text, out catalog, errors);
        }

        /// <summary>Parses and validates catalogue text (the file's body).</summary>
        public static bool TryParse(string text, out CatalogFile catalog, List<string> errors)
        {
            catalog = null;
            CatalogFile parsed;
            try
            {
                parsed = JsonUtility.FromJson<CatalogFile>(text);
            }
            catch (Exception exception)
            {
                errors.Add("the level list is not valid JSON: " + exception.Message);
                return false;
            }

            if (parsed == null)
            {
                errors.Add("the level list is empty");
                return false;
            }
            if (!string.Equals(parsed.schema, SchemaName))
            {
                errors.Add("schema is '" + parsed.schema + "', expected '" + SchemaName + "'");
                return false;
            }
            if (parsed.room == null) parsed.room = new RoomBlock();
            if (parsed.levels == null || parsed.levels.Length == 0)
            {
                errors.Add("the level list has no levels");
                return false;
            }

            int before = errors.Count;
            var ids = new string[parsed.levels.Length];
            for (int i = 0; i < parsed.levels.Length; i++)
            {
                LevelEntry level = parsed.levels[i];
                if (level == null)
                {
                    errors.Add("levels[" + i + "] is null");
                    continue;
                }
                ids[i] = level.id;
                if (!HubSelectMath.IsUsableId(level.id))
                {
                    errors.Add("levels[" + i + "].id is empty");
                }
                if (string.IsNullOrEmpty(level.name))
                {
                    errors.Add("levels[" + i + "] ('" + level.id + "') has no name");
                }
                if (level.scenes == null || level.scenes.Length == 0)
                {
                    errors.Add("levels[" + i + "] ('" + level.id + "') lists no scene");
                }
                if (string.IsNullOrEmpty(level.content))
                {
                    errors.Add("levels[" + i + "] ('" + level.id + "') has no content path");
                }
                if (string.IsNullOrEmpty(level.entry))
                {
                    errors.Add("levels[" + i + "] ('" + level.id + "') has no entry path");
                }
            }

            int duplicate = HubSelectMath.FirstDuplicateId(ids);
            if (duplicate >= 0)
            {
                errors.Add("levels[" + duplicate + "] repeats the id '" + ids[duplicate] + "'");
            }

            if (errors.Count > before) return false;

            catalog = parsed;
            return true;
        }

        /// <summary>Resolves every entry against the open scene, in catalogue order.</summary>
        public static Resolved[] ResolveAgainst(CatalogFile catalog, Scene scene)
        {
            var result = new Resolved[catalog.levels.Length];
            for (int i = 0; i < result.Length; i++)
            {
                LevelEntry entry = catalog.levels[i];
                var resolved = new Resolved();
                resolved.Entry = entry;
                resolved.CatalogueIndex = i;
                resolved.SceneListed = ListsScene(entry, scene.name);
                resolved.ContentPresent = FindPath(scene, entry.content) != null;
                resolved.EntryTransform = FindPath(scene, entry.entry);
                result[i] = resolved;
            }
            return result;
        }

        /// <summary>Whether the entry names this scene (ordinal, exact).</summary>
        public static bool ListsScene(LevelEntry entry, string sceneName)
        {
            if (entry == null || entry.scenes == null) return false;
            for (int i = 0; i < entry.scenes.Length; i++)
            {
                if (string.Equals(entry.scenes[i], sceneName)) return true;
            }
            return false;
        }

        /// <summary>The transform at a hierarchy path from a scene root, or null.</summary>
        public static Transform FindPath(Scene scene, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('/');
            GameObject[] roots = scene.GetRootGameObjects();
            Transform current = null;
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, parts[0]))
                {
                    current = roots[i].transform;
                    break;
                }
            }
            for (int i = 1; current != null && i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
            }
            return current;
        }

        /// <summary>
        /// The offered entries in catalogue order, renumbered without holes. Slot k is the
        /// launcher index k.
        /// </summary>
        public static List<Resolved> Offered(Resolved[] resolved)
        {
            var offeredFlags = new bool[resolved.Length];
            for (int i = 0; i < resolved.Length; i++) offeredFlags[i] = resolved[i].Offered;

            var source = new int[resolved.Length];
            int count = HubSelectMath.CompactOffered(offeredFlags, source);

            var list = new List<Resolved>(count);
            for (int k = 0; k < count; k++) list.Add(resolved[source[k]]);
            return list;
        }
    }
}
