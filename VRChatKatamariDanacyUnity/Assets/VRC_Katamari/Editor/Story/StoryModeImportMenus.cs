using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRCKatamari.Editor.LevelBuild;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.Story
{
    /// <summary>
    /// One-click import of the story-mode scene's two placement documents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why these menu items exist. <c>PlacementImporter</c>'s own menu imports every
    /// document whose <c>level.scene</c> names the open scene, and it deliberately refuses
    /// when the matches mix a whole level with an arena cell. The hub room has to be an
    /// arena cell (<c>level.arena.id = "hub"</c>) because the importer has exactly one
    /// managed root per scene, <c>LevelContent</c>, and a second whole-level document would
    /// delete the first one's props and structures. So after the hub document is added,
    /// the generic menu refuses the StoryMode scene and these two items are the supported
    /// way in: each calls <see cref="PlacementImporter.Import(string)"/> with the id, which
    /// puts the hub under <c>LevelContent/hub</c> and the level under <c>LevelContent</c>.
    /// </para>
    /// <para>
    /// Why the hub item checks for a host first. A cell that declares props must own a
    /// <see cref="PickupFieldHost"/> in its own subtree; otherwise the cell importer reports
    /// an error instead of silently importing geometry that nobody can ever use. The hub's
    /// five props are decoration (the room has no ball and no motor), so the host is inert -
    /// but it is still the contract, and asking for it before the import keeps the first run
    /// green instead of red. This item creates the two plain folders so the component has an
    /// obvious home, then stops and says what to add; the second run imports.
    /// </para>
    /// <para>
    /// Nothing is saved here. The importer does not call <c>SaveScene</c> on purpose; the
    /// operator decides when to save, and can run either item twice to see the idempotency
    /// line (<c>+0 ~0 -0</c>).
    /// </para>
    /// </remarks>
    public static class StoryModeImportMenus
    {
        public const string HubMenuPath = "VRC Katamari/Level/Import StoryMode Hub Room (hub)";
        public const string LevelMenuPath =
            "VRC Katamari/Level/Import StoryMode Level 1 (story_01_bench_size)";

        [MenuItem(HubMenuPath, false, 201)]
        public static void ImportHubRoom()
        {
            Transform hub = EnsureHubRoot();
            if (hub == null) return;

            PickupFieldHost[] hosts = hub.GetComponentsInChildren<PickupFieldHost>(true);
            if (hosts == null || hosts.Length == 0)
            {
                Debug.LogWarning("[hub-room] add a PickupFieldHost component to '" + hub.name
                    + "' (or to a child of it) in the Inspector, then run this menu again."
                    + " The hub's five decor props are synced into that host by the cell"
                    + " importer; a cell that declares props must own one. The host stays"
                    + " inert: the room has no motor and no round.");
                return;
            }

            PlacementImporter.Import("hub");
        }

        [MenuItem(LevelMenuPath, false, 202)]
        public static void ImportStoryLevelOne()
        {
            PlacementImporter.Import("story_01_bench_size");
        }

        /// <summary>
        /// The scene-root <c>LevelContent</c> and its <c>hub</c> child, created when
        /// missing. A scene that already imported the level has both already.
        /// </summary>
        private static Transform EnsureHubRoot()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject content = null;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, PlacementImporter.ManagedRootName))
                {
                    content = roots[i];
                    break;
                }
            }

            if (content == null)
            {
                content = new GameObject(PlacementImporter.ManagedRootName);
                Undo.RegisterCreatedObjectUndo(content, "Create Hub Room Root");
                Debug.Log("[hub-room] created the scene root " + PlacementImporter.ManagedRootName
                    + " (the importer's managed root)");
            }

            Transform hub = content.transform.Find("hub");
            if (hub == null)
            {
                var folder = new GameObject("hub");
                Undo.RegisterCreatedObjectUndo(folder, "Create Hub Room Folder");
                Undo.SetTransformParent(folder.transform, content.transform,
                    "Create Hub Room Folder");
                hub = folder.transform;
                Debug.Log("[hub-room] created " + PlacementImporter.ManagedRootName + "/hub");
            }

            return hub;
        }
    }
}
