using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// Where the level data lives, resolved in ONE place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this class exists (two measured defects, 2026-11-16). <c>TerrainMeshImporter</c> used
    /// to build its path from <c>ProjectRoot()</c>, which returns the UNITY PROJECT directory
    /// (<c>.../VRChatKatamariDanacyUnity</c>) and therefore looked for
    /// <c>.../VRChatKatamariDanacyUnity/Docs/Design/Levels/...</c> - a directory that never
    /// exists; and both importers built <c>Levels/&lt;levelId&gt;</c> by string concatenation,
    /// so a level that lives one folder deeper (<c>physics_test_field/C21</c>) could never be
    /// found by <c>Import("C21")</c>. The fix is not a patch at the call sites: it is this one
    /// resolution (repository root by walking UP to the folder that contains
    /// <c>Docs/Design/Levels</c>, then a level directory lookup that accepts a nested id and
    /// falls back to matching the document's OWN declared id), used by both importers.
    /// </para>
    /// <para>
    /// Failure is always a list of the paths actually tried, because "not found" with one path
    /// is what made the original defect take a round trip to diagnose.
    /// </para>
    /// </remarks>
    public static class LevelPaths
    {
        public const string LevelsRepoRelativeFolder = "Docs/Design/Levels";
        public const string PlacementFileName = "placement.json";
        public const string PlacementV2FileName = "placement.v2.json";
        public const string ReferenceSceneRoot = "Assets/_RefUnlicensed/";

        /// <summary>How far up the tree the repository root is searched for.</summary>
        private const int MaxAncestorWalk = 8;

        /// <summary>
        /// The repository root: the nearest ancestor of the Unity project that contains
        /// <c>Docs/Design/Levels</c>. Null when the checkout has neither (a bare Unity copy).
        /// </summary>
        public static string RepoRoot()
        {
            string projectRoot = ProjectRoot();
            string levels;
            if (TryFindLevelsFolder(projectRoot, out levels))
            {
                return Path.GetFullPath(Path.Combine(levels, "..", ".."));
            }

            return null;
        }

        /// <summary>The Unity project directory (the one that contains <c>Assets/</c>).</summary>
        public static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        /// <summary>The <c>Docs/Design/Levels</c> folder, walking up from the project root.</summary>
        public static string LevelsFolder()
        {
            string levels;
            return TryFindLevelsFolder(ProjectRoot(), out levels) ? levels : null;
        }

        private static bool TryFindLevelsFolder(string startDirectory, out string levels)
        {
            levels = null;
            string directory = startDirectory;
            for (int i = 0; i < MaxAncestorWalk && !string.IsNullOrEmpty(directory); i++)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, LevelsRepoRelativeFolder));
                if (Directory.Exists(candidate))
                {
                    levels = candidate;
                    return true;
                }

                // A checkout where the Unity project sits beside Docs/ rather than inside it.
                candidate = Path.GetFullPath(Path.Combine(directory, "..", LevelsRepoRelativeFolder));
                if (Directory.Exists(candidate))
                {
                    levels = candidate;
                    return true;
                }

                directory = Path.GetDirectoryName(directory);
            }

            return false;
        }

        /// <summary>
        /// Resolves a level id to its directory, accepting a nested id
        /// (<c>physics_test_field/C21</c>) and, failing that, matching the id a document
        /// DECLARES (<c>Import("C21")</c> finds <c>physics_test_field/C21</c>).
        /// </summary>
        public static bool TryResolveLevelDirectory(string levelId, out string directory,
            out List<string> tried)
        {
            directory = null;
            tried = new List<string>();
            if (string.IsNullOrEmpty(levelId)) return false;

            string levels = LevelsFolder();
            if (levels == null)
            {
                tried.Add("(no " + LevelsRepoRelativeFolder + " folder above " + ProjectRoot() + ")");
                return false;
            }

            string trimmed = levelId.Trim().Trim('/', '\\');
            string direct = Path.GetFullPath(Path.Combine(levels, trimmed));
            if (IsInside(levels, direct))
            {
                tried.Add(direct);
                if (Directory.Exists(direct))
                {
                    directory = direct;
                    return true;
                }
            }

            // The last path segment, so "C21" can match "physics_test_field/C21" and
            // "physics_test_field/C21" still matches itself when the whole id was given.
            string leaf = LeafOf(trimmed);
            if (!string.Equals(leaf, trimmed, StringComparison.Ordinal))
            {
                string leafPath = Path.GetFullPath(Path.Combine(levels, leaf));
                if (IsInside(levels, leafPath))
                {
                    tried.Add(leafPath);
                    if (Directory.Exists(leafPath))
                    {
                        directory = leafPath;
                        return true;
                    }
                }
            }

            // Last resort: scan two levels of folders and match the DECLARED id, which is the
            // authority (the folder name is a convention, meta.id is the contract).
            string found = ScanDeclaredId(levels, trimmed, leaf, tried);
            if (found != null)
            {
                directory = found;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves the placement document of a level, preferring a v2 document when the
        /// directory has one.
        /// </summary>
        /// <remarks>
        /// Order, and why: a <c>placement.json</c> whose own <c>schema</c> says
        /// <c>katamari.placement/2</c> wins over <c>placement.v2.json</c>, because that file
        /// name is the canonical one (§1.1) and a level that upgraded in place must not be read
        /// from a stale side file. Then <c>placement.v2.json</c> (the reference levels' name),
        /// then plain <c>placement.json</c> (v1).
        /// </remarks>
        public static bool TryResolvePlacementPath(string levelId, out string path,
            out List<string> tried)
        {
            path = null;
            string directory;
            if (!TryResolveLevelDirectory(levelId, out directory, out tried))
            {
                return false;
            }

            return TryResolvePlacementIn(directory, out path, out tried);
        }

        /// <summary>The same resolution for a directory the caller already resolved.</summary>
        public static bool TryResolvePlacementIn(string directory, out string path,
            out List<string> tried)
        {
            path = null;
            if (tried == null) tried = new List<string>();
            string canonical = Path.Combine(directory, PlacementFileName);
            string v2Side = Path.Combine(directory, PlacementV2FileName);
            tried.Add(canonical);
            if (File.Exists(canonical))
            {
                string text = TryReadText(canonical);
                if (text != null &&
                    text.IndexOf(PlacementV2Reader.SchemaName, StringComparison.Ordinal) >= 0)
                {
                    path = canonical;
                    return true;
                }
            }

            tried.Add(v2Side);
            if (File.Exists(v2Side))
            {
                // A v1-shaped file under the v2 name is still returned: the caller sniffs the
                // schema and reports the mismatch, which is more useful than "not found".
                path = v2Side;
                return true;
            }

            if (File.Exists(canonical))
            {
                path = canonical;
                return true;
            }

            return false;
        }

        /// <summary>Every direct child directory of a level whose document is a level document.</summary>
        public static bool IsLevelDirectory(string directory)
        {
            return File.Exists(Path.Combine(directory, PlacementFileName)) ||
                   File.Exists(Path.Combine(directory, PlacementV2FileName));
        }

        /// <summary>True when the id names an unlicensed reference level (<c>_ref_*</c>).</summary>
        public static bool IsReferenceLevelId(string levelId)
        {
            return !string.IsNullOrEmpty(levelId) &&
                   LeafOf(levelId).StartsWith("_ref_", StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the open scene lives under <c>Assets/_RefUnlicensed/</c>, the only place a
        /// reference level's objects may be written (they never enter git or an upload).
        /// </summary>
        public static bool IsReferenceScene(out string scenePath)
        {
            Scene scene = SceneManager.GetActiveScene();
            scenePath = scene.path;
            return !string.IsNullOrEmpty(scenePath) &&
                   scenePath.StartsWith(ReferenceSceneRoot, StringComparison.Ordinal);
        }

        /// <summary>The last segment of a slash-separated level id.</summary>
        public static string LeafOf(string levelId)
        {
            if (string.IsNullOrEmpty(levelId)) return "";
            int slash = levelId.LastIndexOfAny(new[] { '/', '\\' });
            return slash < 0 ? levelId : levelId.Substring(slash + 1);
        }

        // ------------------------------------------------------------------ helpers ---

        private static string ScanDeclaredId(string levels, string fullId, string leaf,
            List<string> tried)
        {
            string[] firstLevel;
            try
            {
                firstLevel = Directory.GetDirectories(levels);
            }
            catch (Exception)
            {
                return null;
            }

            Array.Sort(firstLevel, StringComparer.Ordinal);
            for (int i = 0; i < firstLevel.Length; i++)
            {
                string found = MatchDirectory(firstLevel[i], fullId, leaf, tried, 0);
                if (found != null) return found;
            }

            return null;
        }

        private static string MatchDirectory(string directory, string fullId, string leaf,
            List<string> tried, int depth)
        {
            if (IsLevelDirectory(directory))
            {
                string declared = DeclaredIdOf(directory);
                if (declared != null &&
                    (string.Equals(declared, fullId, StringComparison.Ordinal) ||
                     string.Equals(declared, leaf, StringComparison.Ordinal) ||
                     string.Equals(declared, RelativeIdOf(directory), StringComparison.Ordinal)))
                {
                    return directory;
                }

                return null;
            }

            if (depth >= 1) return null;
            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception)
            {
                return null;
            }

            Array.Sort(children, StringComparer.Ordinal);
            for (int i = 0; i < children.Length; i++)
            {
                tried.Add(children[i]);
                string found = MatchDirectory(children[i], fullId, leaf, tried, depth + 1);
                if (found != null) return found;
            }

            return null;
        }

        /// <summary>
        /// The id a level document declares: <c>meta.id</c> for v2, <c>level.id</c> for v1.
        /// </summary>
        /// <remarks>
        /// Parsed with the same reader the importers use, so an id that only looks right in a
        /// regex cannot resolve. A broken document simply declares nothing here; the import
        /// itself reports the real problem.
        /// </remarks>
        public static string DeclaredIdOf(string levelDirectory)
        {
            LevelJsonNode block = DeclaredBlockOf(levelDirectory);
            return block != null ? block.GetString("id") : null;
        }

        /// <summary>The scene a level document declares: <c>meta.scene</c> (v2) or <c>level.scene</c>.</summary>
        public static string DeclaredSceneOf(string levelDirectory)
        {
            LevelJsonNode block = DeclaredBlockOf(levelDirectory);
            return block != null ? block.GetString("scene") : null;
        }

        /// <summary>
        /// The block that holds the level's identity: <c>meta</c> for v2, <c>level</c> for v1.
        /// </summary>
        /// <remarks>
        /// Parsed with the same reader the importers use, so an id that only looks right in a
        /// regex cannot resolve. A broken document declares nothing here; the import itself
        /// reports the real problem.
        /// </remarks>
        private static LevelJsonNode DeclaredBlockOf(string levelDirectory)
        {
            string text = null;
            string canonical = Path.Combine(levelDirectory, PlacementFileName);
            if (File.Exists(canonical)) text = TryReadText(canonical);
            if (text == null || text.IndexOf(PlacementV2Reader.SchemaName, StringComparison.Ordinal) < 0)
            {
                string side = Path.Combine(levelDirectory, PlacementV2FileName);
                if (File.Exists(side)) text = TryReadText(side);
            }

            if (text == null) return null;
            LevelJsonNode root;
            try
            {
                root = LevelJsonParser.ParseText(text);
            }
            catch (Exception)
            {
                return null;
            }

            if (root == null || root.Kind != LevelJsonKind.Object) return null;
            LevelJsonNode meta = root.Get("meta");
            if (meta != null && meta.Kind == LevelJsonKind.Object) return meta;
            LevelJsonNode level = root.Get("level");
            return level != null && level.Kind == LevelJsonKind.Object ? level : null;
        }

        private static string RelativeIdOf(string directory)
        {
            string levels = LevelsFolder();
            if (levels == null) return null;
            string full = Path.GetFullPath(directory);
            string prefix = Path.GetFullPath(levels) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.Ordinal)) return Path.GetFileName(full);
            return full.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string TryReadText(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsInside(string parent, string candidate)
        {
            string prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.Ordinal);
        }
    }
}
