using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace VRCKatamari.Editor.MapGreybox
{
    /// <summary>
    /// Builds the PicnicLawn greybox lab scene from the generated authoring payload
    /// <c>Docs/Design/Maps/PicnicLawn/unity_greybox.json</c> (produced by
    /// <c>Tools/mapbuild/build_greybox.py</c>; never edited by hand).
    ///
    /// Menu: VRC Katamari/Map/Build PicnicLawn Greybox.
    ///
    /// Rebuilds are deterministic and idempotent: the scene is created from scratch,
    /// every generated object gets a fixed name and a fixed parent, prop placement uses
    /// a seeded <see cref="System.Random"/> (never <c>UnityEngine.Random</c>), and the
    /// only assets written are the scene, its report JSON, its top-down preview PNG and
    /// a shared material folder that is reused across rebuilds.
    ///
    /// The scene is built in its own scene (additively when another scene is open) and
    /// closed again afterwards, so an unrelated open lab scene is never touched, never
    /// marked dirty and never saved.
    /// </summary>
    public static class PicnicLawnGreyboxBuilder
    {
        public const string SceneFolder = "Assets/VRC_Katamari/Labs/PicnicLawnGreybox";
        public const string ScenePath = SceneFolder + "/PicnicLawnGreybox.unity";
        public const string ReportPath = SceneFolder + "/GreyboxReport.json";
        public const string PreviewPath = SceneFolder + "/PicnicLawnGreybox_top.png";
        public const string MaterialsFolder = SceneFolder + "/Materials";
        public const string PayloadRepoRelativePath = "Docs/Design/Maps/PicnicLawn/unity_greybox.json";

        private const string RootName = "PicnicLawnGreybox";
        private const string MenuPath = "VRC Katamari/Map/Build PicnicLawn Greybox";

        // Expected record counts: the design contract of this specific map
        // (Docs/Design/Maps/PicnicLawn/README.md §3.1/§3.2, layout.json). A payload that
        // no longer matches these numbers is a different map and must not be built silently.
        private const int ExpectedSchemaVersion = 1;
        private const int ExpectedZones = 21;
        private const int ExpectedStructures = 60;
        private const int ExpectedSpawns = 4;
        private const int ExpectedBankAnchors = 4;
        private const int ExpectedSizeGates = 8;
        private const int ExpectedScatterRegions = 20;
        private const int ExpectedRoutes = 21;
        private const int ExpectedChokepoints = 16;
        private const int ExpectedProps = 54;

        // Cross-checks from the design tables; mismatches are warnings, not hard failures.
        private const int DesignAbsorbableInstances = 452;
        private const int DesignFixedInstances = 60;

        private const float StructurePositionMargin = 10f;
        private const float RoutePositionMargin = 2f;
        private const float ZoneSlabThickness = 0.02f;
        private const float RouteRibbonY = 0.10f;
        private const float RouteNodeY = 0.13f;
        private const int PlacementAttempts = 256;
        private const int PreviewResolution = 2048;
        private const float PreviewOrthoSize = 21.5f;

        // Objects are built on a scratch layer so the preview camera can render this
        // scene alone while another lab scene is still loaded. The layer is reset to
        // Default before the scene is saved.
        private const int PreviewLayer = 30;

        private static readonly Dictionary<string, Color> Palette = new Dictionary<string, Color>
        {
            { "Ground", new Color(0.17f, 0.18f, 0.17f) },
            { "Zone_Turf", new Color(0.30f, 0.52f, 0.29f) },
            { "Zone_TurfWorn", new Color(0.44f, 0.53f, 0.31f) },
            { "Zone_TurfLong", new Color(0.23f, 0.43f, 0.23f) },
            { "Zone_TurfPatchy", new Color(0.39f, 0.50f, 0.29f) },
            { "Zone_Blanket", new Color(0.72f, 0.30f, 0.32f) },
            { "Zone_FlowerBed", new Color(0.45f, 0.32f, 0.48f) },
            { "Zone_TreeRoot", new Color(0.42f, 0.33f, 0.22f) },
            { "Structure", new Color(0.62f, 0.62f, 0.62f) },
            { "Hedge", new Color(0.22f, 0.40f, 0.24f) },
            { "Tree", new Color(0.30f, 0.24f, 0.16f) },
            { "Prop_S", new Color(0.55f, 0.85f, 0.95f) },
            { "Prop_M", new Color(0.98f, 0.62f, 0.20f) },
            { "Prop_L", new Color(0.90f, 0.25f, 0.55f) },
            { "Spawn", new Color(0.15f, 0.75f, 0.85f) },
            { "Bank", new Color(0.95f, 0.80f, 0.20f) },
            { "GateAbsorb", new Color(0.90f, 0.30f, 0.20f) },
            { "GateClimb", new Color(0.65f, 0.40f, 0.95f) },
            { "Chokepoint", new Color(0.95f, 0.85f, 0.25f) },
            { "ChokepointNarrow", new Color(0.95f, 0.55f, 0.15f) },
            { "Route", new Color(0.86f, 0.89f, 0.93f) },
            { "RouteLoop", new Color(0.35f, 0.60f, 0.95f) },
            { "RouteShortcut", new Color(0.95f, 0.45f, 0.45f) },
        };

        [MenuItem(MenuPath, false, 100)]
        public static void Build()
        {
            BuildInternal();
        }

        // ------------------------------------------------------------------ build ---

        private static void BuildInternal()
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var notes = new List<string>();

            string payloadPath = ResolvePayloadPath(out List<string> triedPaths);
            if (payloadPath == null)
            {
                Debug.LogError(
                    "[PicnicLawnGreybox] payload not found. Expected " + PayloadRepoRelativePath +
                    " at the repo root next to VRChatKatamariDanacyUnity. Tried:\n  " +
                    string.Join("\n  ", triedPaths.ToArray()));
                return;
            }

            GreyboxPayload payload;
            try
            {
                payload = JsonUtility.FromJson<GreyboxPayload>(File.ReadAllText(payloadPath));
            }
            catch (Exception exception)
            {
                Debug.LogError("[PicnicLawnGreybox] failed to read " + payloadPath + ": " + exception.Message);
                return;
            }

            if (!ValidatePayload(payload, errors, warnings))
            {
                Debug.LogError("[PicnicLawnGreybox] payload validation failed (" + errors.Count +
                               " error(s)):\n  " + string.Join("\n  ", errors.ToArray()));
                return;
            }

            Scene previousScene = SceneManager.GetActiveScene();
            bool previousUnsaved = previousScene.IsValid() && previousScene.isDirty &&
                                   !string.Equals(previousScene.path, ScenePath, StringComparison.OrdinalIgnoreCase);
            if (previousUnsaved)
            {
                Debug.LogError("[PicnicLawnGreybox] active scene '" + previousScene.path +
                               "' has unsaved changes. Save or discard them first; refusing to replace it.");
                return;
            }

            // An aborted run (or another tool) can leave an empty untitled scene behind, and
            // Unity refuses additive scene creation while one is open. Close such strays, but
            // never a stray that might carry unsaved work.
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                Scene stray = SceneManager.GetSceneAt(i);
                if (!stray.IsValid() || !stray.isLoaded || !string.IsNullOrEmpty(stray.path))
                {
                    continue;
                }

                if (stray.isDirty || stray.rootCount > 0)
                {
                    Debug.LogError("[PicnicLawnGreybox] an untitled scene with content is open; " +
                                   "save or close it first (the build never touches unsaved scenes).");
                    return;
                }

                EditorSceneManager.CloseScene(stray, true);
            }

            previousScene = SceneManager.GetActiveScene();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var counters = new BuildCounters();

            EnsureAssetFolder(MaterialsFolder);
            Dictionary<string, Material> materials = BuildPalette();

            bool rebuildInPlace = string.Equals(previousScene.path, ScenePath, StringComparison.OrdinalIgnoreCase);
            Scene existing = SceneManager.GetSceneByPath(ScenePath);
            if (existing.IsValid() && existing.isLoaded && !rebuildInPlace)
            {
                EditorSceneManager.CloseScene(existing, true);
            }

            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                rebuildInPlace ? NewSceneMode.Single : NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);

            var root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.position = Vector3.zero;
            root.layer = PreviewLayer;
            counters.Created.Add(root);

            var rootTransform = root.transform;
            var sortedZones = new List<GreyboxZone>(payload.zones);
            sortedZones.Sort((a, b) => string.CompareOrdinal(a.id, b.id));

            Transform groundGroup = CreateGroup("00_Ground", rootTransform, counters);
            Transform zoneGroup = CreateGroup("10_Zones", rootTransform, counters);
            Transform structureGroup = CreateGroup("20_Structures", rootTransform, counters);
            Transform propGroup = CreateGroup("30_Props", rootTransform, counters);
            Transform spawnGroup = CreateGroup("40_Spawns", rootTransform, counters);
            Transform bankGroup = CreateGroup("50_BankAnchors", rootTransform, counters);
            Transform gateGroup = CreateGroup("60_SizeGates", rootTransform, counters);
            Transform chokeGroup = CreateGroup("70_Chokepoints", rootTransform, counters);
            Transform routeGroup = CreateGroup("80_Routes", rootTransform, counters);
            Transform labelGroup = CreateGroup("90_Labels", rootTransform, counters);
            Transform lightingGroup = CreateGroup("95_Lighting", rootTransform, counters);

            Font font = ResolveGreyboxFont(warnings);

            BuildGround(payload, groundGroup, materials, counters);
            BuildZones(sortedZones, zoneGroup, materials, counters);
            BuildStructures(payload, structureGroup, materials, counters, notes);
            BuildProps(payload, propGroup, materials, counters, warnings);
            BuildSpawns(payload, spawnGroup, labelGroup, materials, counters, font, warnings);
            BuildBankAnchors(payload, bankGroup, labelGroup, materials, counters, font, warnings);
            BuildSizeGates(payload, gateGroup, labelGroup, materials, counters, font, warnings);
            BuildChokepoints(payload, chokeGroup, materials, counters);
            BuildRoutes(payload, routeGroup, materials, counters);
            BuildLighting(lightingGroup, counters);

            bool previewWritten = RenderTopDownPreview(rootTransform, counters, warnings);
            ResetPreviewLayer(counters);

            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (!saved)
            {
                Debug.LogError("[PicnicLawnGreybox] failed to save " + ScenePath +
                               "; the scene stays open and unsaved.");
            }

            stopwatch.Stop();
            double buildSeconds = stopwatch.Elapsed.TotalMilliseconds / 1000.0;

            WriteReport(payload, payloadPath, counters, buildSeconds, previewWritten, warnings, notes, errors);

            if (!rebuildInPlace)
            {
                if (previousScene.IsValid() && previousScene.isLoaded)
                {
                    SceneManager.SetActiveScene(previousScene);
                }

                EditorSceneManager.CloseScene(scene, true);
            }

            if (!saved)
            {
                return;
            }

            var summary = new StringBuilder();
            summary.AppendLine("[PicnicLawnGreybox] " + ScenePath + " built in " +
                               buildSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s");
            summary.AppendLine("  zones " + counters.Zones + ", structures " + counters.StructureObjects +
                               " (+" + counters.StructuresSkipped + " footprint-only, " +
                               counters.StructureColliders + " colliders)");
            summary.AppendLine("  props " + counters.PropsPlaced + "/" + counters.PropsRequested + " (" +
                               counters.PropsGlobalSpacing + " with global spacing, " +
                               counters.PropsPerDefinitionSpacingOnly + " per-definition spacing only)");
            summary.AppendLine("  spawns " + counters.Spawns + ", bank anchors " + counters.BankAnchors +
                               ", size gates " + counters.SizeGates + ", chokepoints " + counters.Chokepoints);
            summary.AppendLine("  route waypoints " + counters.RouteWaypoints + ", segments " +
                               counters.RouteSegments + ", labels " + counters.Labels);
            summary.AppendLine("  renderers " + counters.Renderers.Count + ", materials " +
                               materials.Count + ", preview " + (previewWritten ? PreviewPath : "skipped"));
            if (notes.Count > 0)
            {
                summary.AppendLine();
                for (int i = 0; i < notes.Count; i++)
                {
                    summary.AppendLine("  note: " + notes[i]);
                }
            }

            summary.Append("  report " + ReportPath);
            Debug.Log(summary.ToString());

            if (warnings.Count > 0)
            {
                Debug.LogWarning("[PicnicLawnGreybox] " + warnings.Count + " warning(s):\n  " +
                                 string.Join("\n  ", warnings.ToArray()));
            }

            if (previousScene.IsValid() && previousScene.isLoaded && previousScene.isDirty)
            {
                Debug.LogWarning("[PicnicLawnGreybox] previous scene '" + previousScene.path +
                                 "' became dirty during the build (expected: untouched).");
            }
        }

        private static Transform CreateGroup(string name, Transform parent, BuildCounters counters)
        {
            var group = new GameObject(name);
            group.transform.SetParent(parent, false);
            group.layer = PreviewLayer;
            counters.Created.Add(group);
            return group.transform;
        }

        // ---------------------------------------------------------------- palette ---

        private static Shader ResolveGreyboxShader()
        {
            string[] candidates = { "Standard", "Universal Render Pipeline/Lit", "Unlit/Color" };
            for (int i = 0; i < candidates.Length; i++)
            {
                Shader shader = Shader.Find(candidates[i]);
                if (shader != null)
                {
                    return shader;
                }
            }

            return null;
        }

        private static void ApplyColor(Material material, Color color)
        {
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", 0.12f);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0.12f);
            }

            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", 0f);
            }
        }

        private static Dictionary<string, Material> BuildPalette()
        {
            Shader shader = ResolveGreyboxShader();
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "[PicnicLawnGreybox] no usable shader found (tried Standard, URP Lit, Unlit/Color).");
            }

            var materials = new Dictionary<string, Material>();
            foreach (KeyValuePair<string, Color> entry in Palette)
            {
                string path = MaterialsFolder + "/Greybox_" + entry.Key + ".mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    material = new Material(shader);
                    AssetDatabase.CreateAsset(material, path);
                }
                else if (material.shader != shader)
                {
                    material.shader = shader;
                }

                ApplyColor(material, entry.Value);
                EditorUtility.SetDirty(material);
                materials[entry.Key] = material;
            }

            // The material folder is generated: drop anything the palette no longer names.
            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { MaterialsFolder });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                string fileName = Path.GetFileNameWithoutExtension(path);
                const string prefix = "Greybox_";
                if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
                    !Palette.ContainsKey(fileName.Substring(prefix.Length)))
                {
                    AssetDatabase.DeleteAsset(path);
                }
            }

            AssetDatabase.SaveAssets();
            return materials;
        }

        private static Material Mat(Dictionary<string, Material> materials, string key)
        {
            if (!materials.TryGetValue(key, out Material material))
            {
                throw new InvalidOperationException("[PicnicLawnGreybox] palette is missing material '" + key + "'.");
            }

            return material;
        }

        // -------------------------------------------------------------- primitives ---

        private static GameObject CreateBox(string name, Transform parent, Vector3 center, Quaternion rotation,
            Vector3 size, Material material, BuildCounters counters)
        {
            return CreatePrimitive(name, parent, PrimitiveType.Cube, center, rotation, size, material, counters);
        }

        private static GameObject CreateSphere(string name, Transform parent, Vector3 center, Vector3 diameter,
            Material material, BuildCounters counters)
        {
            return CreatePrimitive(name, parent, PrimitiveType.Sphere, center, Quaternion.identity, diameter, material,
                counters);
        }

        private static GameObject CreateCylinder(string name, Transform parent, Vector3 center, Quaternion rotation,
            float diameter, float height, Material material, BuildCounters counters)
        {
            // Unity's cylinder primitive is 1 unit across and 2 units tall.
            var scale = new Vector3(Mathf.Max(0.001f, diameter), Mathf.Max(0.0005f, height * 0.5f),
                Mathf.Max(0.001f, diameter));
            return CreatePrimitive(name, parent, PrimitiveType.Cylinder, center, rotation, scale, material, counters);
        }

        private static GameObject CreatePrimitive(string name, Transform parent, PrimitiveType type, Vector3 center,
            Quaternion rotation, Vector3 scale, Material material, BuildCounters counters)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.layer = PreviewLayer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localRotation = rotation;
            go.transform.localScale = scale;

            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                counters.Renderers.Add(renderer);
            }

            counters.Created.Add(go);
            return go;
        }

        private static Font ResolveGreyboxFont(List<string> warnings)
        {
            Font font = null;
            try
            {
                font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch (Exception)
            {
                font = null;
            }

            if (font == null)
            {
                warnings.Add("built-in font 'LegacyRuntime.ttf' unavailable; labels are skipped.");
            }

            return font;
        }

        private static void CreateLabel(string name, Transform parent, string text, Vector3 position, float worldHeight,
            Color color, Font font, BuildCounters counters)
        {
            if (font == null)
            {
                return;
            }

            var go = new GameObject(name);
            go.layer = PreviewLayer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            // Lay the text flat so it reads correctly in a north-up top-down view: the
            // quad's up becomes +Z (north) and its front normal points down, which the
            // built-in font material (Cull Off) still renders from above.
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            var mesh = go.AddComponent<TextMesh>();
            mesh.font = font;
            mesh.fontSize = 64;
            mesh.characterSize = 1f;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.text = text;
            mesh.color = color;

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                if (font.material != null)
                {
                    renderer.sharedMaterial = font.material;
                }

                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                counters.Renderers.Add(renderer);

                // TextMesh geometry may not be built yet; fontSize 64 at characterSize 1
                // renders about 4 world units tall, so that is the fallback.
                float currentHeight = renderer.bounds.size.y;
                float scale = currentHeight > 0.0001f ? worldHeight / currentHeight : worldHeight / 4f;
                go.transform.localScale = Vector3.one * scale;
            }

            counters.Created.Add(go);
            counters.Labels++;
        }

        // ------------------------------------------------------------------ ground ---

        private static void BuildGround(GreyboxPayload payload, Transform parent, Dictionary<string, Material> materials,
            BuildCounters counters)
        {
            float width = payload.bounds.max_x - payload.bounds.min_x + 0.4f;
            float depth = payload.bounds.max_z - payload.bounds.min_z + 0.4f;
            float centerX = (payload.bounds.min_x + payload.bounds.max_x) * 0.5f;
            float centerZ = (payload.bounds.min_z + payload.bounds.max_z) * 0.5f;
            const float slab = 0.05f;

            CreateBox("ground_plane", parent, new Vector3(centerX, -slab * 0.5f, centerZ), Quaternion.identity,
                new Vector3(width, slab, depth), Mat(materials, "Ground"), counters);
        }

        // ------------------------------------------------------------------- zones ---

        private static void BuildZones(List<GreyboxZone> zones, Transform parent, Dictionary<string, Material> materials,
            BuildCounters counters)
        {
            for (int i = 0; i < zones.Count; i++)
            {
                GreyboxZone zone = zones[i];
                float width = zone.max_x - zone.min_x;
                float depth = zone.max_z - zone.min_z;
                float bottom = Mathf.Min(0f, zone.ground_y_m);
                float top = zone.ground_y_m + ZoneSlabThickness;
                float height = Mathf.Max(ZoneSlabThickness, top - bottom);
                var center = new Vector3((zone.min_x + zone.max_x) * 0.5f, bottom + height * 0.5f,
                    (zone.min_z + zone.max_z) * 0.5f);

                // Raised zones (blankets at 0.06 m, flower beds at 0.26 m) read as a step
                // because the slab spans from y=0 up to their ground height.
                CreateBox(zone.id, parent, center, Quaternion.identity, new Vector3(width, height, depth),
                    Mat(materials, ZoneMaterialKey(zone.surface)), counters);
                counters.Zones++;
            }
        }

        private static string ZoneMaterialKey(string surface)
        {
            switch (surface)
            {
                case "wool_blanket": return "Zone_Blanket";
                case "raised_soil": return "Zone_FlowerBed";
                case "root_mound": return "Zone_TreeRoot";
                case "turf_worn": return "Zone_TurfWorn";
                case "turf_long": return "Zone_TurfLong";
                case "turf_patchy": return "Zone_TurfPatchy";
                default: return "Zone_Turf";
            }
        }

        // -------------------------------------------------------------- structures ---

        private static void BuildStructures(GreyboxPayload payload, Transform parent,
            Dictionary<string, Material> materials, BuildCounters counters, List<string> notes)
        {
            GreyboxStructure[] structures = payload.structures;
            for (int i = 0; i < structures.Length; i++)
            {
                GreyboxStructure structure = structures[i];
                var position = new Vector3(structure.position_x, structure.position_y, structure.position_z);
                var size = new Vector3(Mathf.Abs(structure.size_x), Mathf.Abs(structure.size_y),
                    Mathf.Abs(structure.size_z));
                Quaternion rotation = Quaternion.Euler(0f, structure.yaw_deg, 0f);
                string name = structure.definition_id + "#" + structure.index.ToString("00",
                    CultureInfo.InvariantCulture);
                Material material = Mat(materials, StructureMaterialKey(structure.definition_id));
                float centerY = CenterY(position.y, size.y);

                switch (structure.kind)
                {
                    case "merged_plane":
                        // Full-map footprint meshes (ground terrain, path mesh) would hide the
                        // zone layout from above. The ground slab and the route ribbons stand
                        // in for them; only the real ground structure is worth a note.
                        counters.StructuresSkipped++;
                        notes.Add("structure " + name + " (" + structure.kind +
                                  ") rendered as footprint only: the ground slab and route ribbons stand in for it");
                        continue;

                    case "merged_ring":
                        BuildRing(name, parent, position, size, material, counters);
                        counters.StructureObjects++;
                        continue;

                    case "box_collider":
                        BuildInvisibleWall(name, parent, new Vector3(position.x, centerY, position.z), rotation, size,
                            counters);
                        counters.StructureColliders++;
                        continue;

                    case "disc":
                    case "decal_ring":
                        CreateCylinder(name, parent, new Vector3(position.x, centerY, position.z), rotation,
                            Mathf.Max(size.x, 0.05f), Mathf.Max(size.y, 0.01f), material, counters);
                        counters.StructureObjects++;
                        continue;

                    case "pole_flag":
                        BuildPoleFlag(name, parent, position, size, material, materials, counters);
                        counters.StructureObjects++;
                        continue;

                    case "tree":
                        BuildTree(name, parent, position, size, material, counters);
                        counters.StructureObjects++;
                        continue;

                    case "mound":
                        CreateSphere(name, parent, new Vector3(position.x, centerY, position.z), size, material,
                            counters);
                        counters.StructureObjects++;
                        continue;

                    default:
                        CreateBox(name, parent, new Vector3(position.x, centerY, position.z), rotation, size, material,
                            counters);
                        counters.StructureObjects++;
                        continue;
                }
            }
        }

        private static float CenterY(float positionY, float sizeY)
        {
            // The generator mixes centred records (pads, hedges, blankets: y = height/2)
            // with base-anchored ones (trees, posts, poles: y = 0). A record whose y is
            // below half its height is base-anchored.
            if (sizeY > 0.001f && positionY < sizeY * 0.5f - 0.0001f)
            {
                return positionY + sizeY * 0.5f;
            }

            return positionY;
        }

        private static string StructureMaterialKey(string definitionId)
        {
            switch (definitionId)
            {
                case "boundary_hedge":
                case "core_hedge":
                    return "Hedge";
                case "picnic_blanket":
                    return "Zone_Blanket";
                case "flower_bed_frame":
                    return "Zone_FlowerBed";
                case "tree_root_mound":
                    return "Zone_TreeRoot";
                case "invisible_wall":
                    // Collision-only record: it has no renderer, so no material is used.
                    return "Structure";
                case "corner_tree":
                case "giant_oak":
                case "distant_tree_line":
                    return "Tree";
                case "bank_pad":
                case "bank_beacon":
                    return "Bank";
                case "spawn_marker":
                    return "Spawn";
                case "ground_terrain":
                case "path_mesh":
                    return "Ground";
                default:
                    return "Structure";
            }
        }

        private static void BuildRing(string name, Transform parent, Vector3 position, Vector3 size, Material material,
            BuildCounters counters)
        {
            var ring = new GameObject(name);
            ring.layer = PreviewLayer;
            ring.transform.SetParent(parent, false);
            ring.transform.localPosition = Vector3.zero;
            counters.Created.Add(ring);

            // A ring record stores the outer extent in size_x/size_z and the fence depth
            // in the smaller of the two (boundary hedge: 40 x 0.8). A square record
            // (distant tree line: 50 x 50) is a thin frame around the map instead.
            bool squareFrame = Mathf.Abs(size.x - size.z) < 0.001f;
            float thickness = squareFrame ? 1f : Mathf.Max(0.05f, Mathf.Min(size.x, size.z));
            float half = Mathf.Max(0.1f, size.x * 0.5f - thickness * 0.5f);
            float y = position.y;
            float spanZ = size.x;

            CreateBox("north", ring.transform, new Vector3(position.x, y, position.z + half), Quaternion.identity,
                new Vector3(size.x, size.y, thickness), material, counters);
            CreateBox("south", ring.transform, new Vector3(position.x, y, position.z - half), Quaternion.identity,
                new Vector3(size.x, size.y, thickness), material, counters);
            CreateBox("east", ring.transform, new Vector3(position.x + half, y, position.z), Quaternion.identity,
                new Vector3(thickness, size.y, spanZ), material, counters);
            CreateBox("west", ring.transform, new Vector3(position.x - half, y, position.z), Quaternion.identity,
                new Vector3(thickness, size.y, spanZ), material, counters);
        }

        private static void BuildInvisibleWall(string name, Transform parent, Vector3 center, Quaternion rotation,
            Vector3 size, BuildCounters counters)
        {
            // Collision-only data: keep it visible? No. The boundary hedge ring already
            // marks the play area, so the wall stays an unrendered BoxCollider.
            var go = new GameObject(name);
            go.layer = PreviewLayer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localRotation = rotation;
            var box = go.AddComponent<BoxCollider>();
            box.size = size;
            counters.Created.Add(go);
        }

        private static void BuildPoleFlag(string name, Transform parent, Vector3 position, Vector3 size,
            Material poleMaterial, Dictionary<string, Material> materials, BuildCounters counters)
        {
            var pole = new GameObject(name);
            pole.layer = PreviewLayer;
            pole.transform.SetParent(parent, false);
            pole.transform.localPosition = Vector3.zero;
            counters.Created.Add(pole);

            float height = Mathf.Max(0.2f, size.y);
            CreateCylinder("pole", pole.transform,
                new Vector3(position.x, position.y + height * 0.5f, position.z), Quaternion.identity,
                Mathf.Max(0.04f, size.x), height, poleMaterial, counters);
            CreateBox("flag", pole.transform,
                new Vector3(position.x + 0.45f, position.y + height - 0.35f, position.z),
                Quaternion.Euler(0f, 0f, 0f), new Vector3(0.9f, 0.45f, 0.05f), Mat(materials, "Bank"), counters);
        }

        private static void BuildTree(string name, Transform parent, Vector3 position, Vector3 size, Material material,
            BuildCounters counters)
        {
            var tree = new GameObject(name);
            tree.layer = PreviewLayer;
            tree.transform.SetParent(parent, false);
            tree.transform.localPosition = Vector3.zero;
            counters.Created.Add(tree);

            float height = Mathf.Max(0.5f, size.y);
            float trunk = Mathf.Max(0.15f, size.x);
            CreateCylinder("trunk", tree.transform,
                new Vector3(position.x, position.y + height * 0.5f, position.z), Quaternion.identity, trunk, height,
                material, counters);

            float canopy = Mathf.Max(1f, trunk * 4f);
            CreateSphere("canopy", tree.transform,
                new Vector3(position.x, position.y + height * 0.72f, position.z),
                new Vector3(canopy, canopy * 0.8f, canopy), material, counters);
        }

        // ------------------------------------------------------------------- props ---

        private static void BuildProps(GreyboxPayload payload, Transform parent, Dictionary<string, Material> materials,
            BuildCounters counters, List<string> warnings)
        {
            var zones = new Dictionary<string, GreyboxZone>();
            for (int i = 0; i < payload.zones.Length; i++)
            {
                zones[payload.zones[i].id] = payload.zones[i];
            }

            var props = new Dictionary<string, GreyboxProp>();
            for (int i = 0; i < payload.props.Length; i++)
            {
                props[payload.props[i].id] = payload.props[i];
            }

            var propRegions = new List<GreyboxScatterRegion>(payload.scatter_regions);
            propRegions.Sort((a, b) => string.CompareOrdinal(a.zone_id, b.zone_id));

            for (int r = 0; r < propRegions.Count; r++)
            {
                GreyboxScatterRegion region = propRegions[r];
                GreyboxZone zone = zones[region.zone_id];
                Transform group = CreateGroup(region.zone_id, parent, counters);
                counters.PropZones++;

                // Placement is checked against every prop already in the zone first. The
                // authored counts are denser than a global min-spacing packing can hold, so
                // the remainder falls back to per-definition spacing and is counted.
                var placed = new List<Placement>();

                for (int d = 0; d < region.prop_ids.Length; d++)
                {
                    string propId = region.prop_ids[d];
                    GreyboxProp prop = props[propId];
                    int countMin = region.prop_counts_min[d];
                    int countMax = region.prop_counts_max[d];
                    var random = new System.Random(StableSeed(region.zone_id, propId));
                    int count = countMax <= countMin ? countMin : random.Next(countMin, countMax + 1);
                    counters.PropsRequested += count;

                    float inset = Mathf.Max(0.05f, prop.game_longest_dim_m * 0.5f + 0.05f);
                    for (int n = 0; n < count; n++)
                    {
                        if (!TryFindSpot(random, zone, inset, region.min_spacing_m, placed, propId, true,
                                out Vector2 spot))
                        {
                            if (!TryFindSpot(random, zone, inset, region.min_spacing_m, placed, propId, false,
                                    out spot))
                            {
                                counters.PropsDropped++;
                                warnings.Add("prop " + propId + " in " + region.zone_id +
                                             " dropped: no position respects min_spacing_m " +
                                             region.min_spacing_m.ToString("0.##", CultureInfo.InvariantCulture));
                                continue;
                            }

                            counters.PropsPerDefinitionSpacingOnly++;
                        }
                        else
                        {
                            counters.PropsGlobalSpacing++;
                        }

                        placed.Add(new Placement { X = spot.x, Z = spot.y, PropId = propId });
                        Material material = Mat(materials, PropMaterialKey(prop.size_tier));
                        CreateProp(zone, prop, spot, n, random, group, material, counters);
                        counters.PropsPlaced++;
                    }
                }
            }
        }

        private static string PropMaterialKey(string tier)
        {
            switch (tier)
            {
                case "s": return "Prop_S";
                case "l": return "Prop_L";
                default: return "Prop_M";
            }
        }

        private static void CreateProp(GreyboxZone zone, GreyboxProp prop, Vector2 spot, int index,
            System.Random random, Transform parent, Material material, BuildCounters counters)
        {
            float dimension = Mathf.Max(0.04f, prop.game_longest_dim_m);
            float ground = zone.ground_y_m + ZoneSlabThickness;
            var center = new Vector3(spot.x, ground + dimension * 0.5f, spot.y);
            float yaw = (float)random.NextDouble() * 360f;
            string name = prop.id + "#" + index.ToString("00", CultureInfo.InvariantCulture);

            switch (prop.size_tier)
            {
                case "s":
                    CreateSphere(name, parent, center, new Vector3(dimension, dimension, dimension), material, counters);
                    break;
                case "l":
                    CreateCylinder(name, parent, center, Quaternion.Euler(0f, yaw, 0f), dimension * 0.8f, dimension,
                        material, counters);
                    break;
                default:
                    CreateBox(name, parent, center, Quaternion.Euler(0f, yaw, 0f),
                        new Vector3(dimension, dimension, dimension), material, counters);
                    break;
            }
        }

        private static bool TryFindSpot(System.Random random, GreyboxZone zone, float inset, float spacing,
            List<Placement> placed, string propId, bool allProps, out Vector2 spot)
        {
            float x0 = zone.min_x + inset;
            float x1 = zone.max_x - inset;
            float z0 = zone.min_z + inset;
            float z1 = zone.max_z - inset;
            if (x1 < x0)
            {
                float center = (zone.min_x + zone.max_x) * 0.5f;
                x0 = center;
                x1 = center;
            }

            if (z1 < z0)
            {
                float center = (zone.min_z + zone.max_z) * 0.5f;
                z0 = center;
                z1 = center;
            }

            float spacingSquared = spacing * spacing;
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                float x = x0 + (float)random.NextDouble() * (x1 - x0);
                float z = z0 + (float)random.NextDouble() * (z1 - z0);
                bool free = true;
                for (int i = 0; i < placed.Count; i++)
                {
                    Placement other = placed[i];
                    if (!allProps && other.PropId != propId)
                    {
                        continue;
                    }

                    float dx = other.X - x;
                    float dz = other.Z - z;
                    if (dx * dx + dz * dz < spacingSquared)
                    {
                        free = false;
                        break;
                    }
                }

                if (free)
                {
                    spot = new Vector2(x, z);
                    return true;
                }
            }

            spot = Vector2.zero;
            return false;
        }

        private static int StableSeed(string zoneId, string propId)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = Fnv(hash, zoneId);
                hash = Fnv(hash, "|");
                hash = Fnv(hash, propId);
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        private static uint Fnv(uint hash, string text)
        {
            unchecked
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    hash ^= (byte)(c & 0xFF);
                    hash *= 16777619u;
                    hash ^= (byte)(c >> 8);
                    hash *= 16777619u;
                }

                return hash;
            }
        }

        // ------------------------------------------------------------ spawn points ---

        private static void BuildSpawns(GreyboxPayload payload, Transform parent, Transform labelParent,
            Dictionary<string, Material> materials, BuildCounters counters, Font font, List<string> warnings)
        {
            for (int i = 0; i < payload.spawns.Length; i++)
            {
                GreyboxSpawn spawn = payload.spawns[i];
                var position = new Vector3(spawn.position_x, spawn.position_y, spawn.position_z);
                Quaternion facing = Quaternion.Euler(0f, spawn.yaw_deg, 0f);
                var group = new GameObject(spawn.id);
                group.layer = PreviewLayer;
                group.transform.SetParent(parent, false);
                group.transform.localPosition = position;
                group.transform.localRotation = facing;
                counters.Created.Add(group);

                // Pad + facing arrow in the spawn's local frame (+Z is the facing).
                CreateCylinder("pad", group.transform, new Vector3(0f, 0.13f, 0f), Quaternion.identity, 1.8f, 0.02f,
                    Mat(materials, "Spawn"), counters);
                CreateBox("arrow_shaft", group.transform, new Vector3(0f, 0.145f, 0.95f), Quaternion.identity,
                    new Vector3(0.09f, 0.02f, 1.25f), Mat(materials, "Spawn"), counters);
                CreateBox("arrow_left", group.transform, new Vector3(0.14f, 0.145f, 1.42f),
                    Quaternion.Euler(0f, 30f, 0f), new Vector3(0.09f, 0.02f, 0.55f), Mat(materials, "Spawn"),
                    counters);
                CreateBox("arrow_right", group.transform, new Vector3(-0.14f, 0.145f, 1.42f),
                    Quaternion.Euler(0f, -30f, 0f), new Vector3(0.09f, 0.02f, 0.55f), Mat(materials, "Spawn"),
                    counters);

                CreateLabel(spawn.id + "_label", labelParent, spawn.id.ToUpperInvariant(),
                    position + new Vector3(0f, 1.1f, 0f), 0.7f, Mat(materials, "Spawn").color, font, counters);
                counters.Spawns++;
            }
        }

        // ------------------------------------------------------------- bank anchors ---

        private static void BuildBankAnchors(GreyboxPayload payload, Transform parent, Transform labelParent,
            Dictionary<string, Material> materials, BuildCounters counters, Font font, List<string> warnings)
        {
            const int segments = 24;
            for (int i = 0; i < payload.bank_anchors.Length; i++)
            {
                GreyboxBankAnchor anchor = payload.bank_anchors[i];
                var position = new Vector3(anchor.position_x, anchor.position_y, anchor.position_z);
                float radius = Mathf.Max(0.3f, anchor.radius_m);
                var group = new GameObject(anchor.id);
                group.layer = PreviewLayer;
                group.transform.SetParent(parent, false);
                group.transform.localPosition = position;
                counters.Created.Add(group);

                float segmentLength = 2f * Mathf.PI * radius / segments * 1.08f;
                for (int s = 0; s < segments; s++)
                {
                    float angle = s * 360f / segments;
                    float radians = angle * Mathf.Deg2Rad;
                    var local = new Vector3(Mathf.Sin(radians) * radius, 0.14f, Mathf.Cos(radians) * radius);
                    CreateBox(anchor.id + "_ring_" + s.ToString("00", CultureInfo.InvariantCulture), group.transform,
                        local, Quaternion.Euler(0f, angle + 90f, 0f),
                        new Vector3(0.09f, 0.04f, segmentLength), Mat(materials, "Bank"), counters);
                }

                CreateLabel(anchor.id + "_label", labelParent,
                    anchor.id + " #" + anchor.rotation_order_index.ToString(CultureInfo.InvariantCulture),
                    position + new Vector3(0f, 2.3f, 0f), 0.85f, Mat(materials, "Bank").color, font, counters);
                counters.BankAnchors++;
            }
        }

        // --------------------------------------------------------------- size gates ---

        private static void BuildSizeGates(GreyboxPayload payload, Transform parent, Transform labelParent,
            Dictionary<string, Material> materials, BuildCounters counters, Font font, List<string> warnings)
        {
            for (int i = 0; i < payload.size_gates.Length; i++)
            {
                GreyboxSizeGate gate = payload.size_gates[i];
                var position = new Vector3(gate.position_x, gate.position_y, gate.position_z);
                float width = Mathf.Max(0.4f, gate.clear_width_m);
                // The gates sit on the diagonals of the central lawn: face the approach
                // direction (map centre) rather than the payload's unused yaw_deg.
                float yaw = Mathf.Atan2(position.x, position.z) * Mathf.Rad2Deg;
                Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
                bool absorb = string.Equals(gate.type, "absorb", StringComparison.Ordinal);
                string materialKey = absorb ? "GateAbsorb" : "GateClimb";
                Material material = Mat(materials, materialKey);

                var group = new GameObject(gate.id);
                group.layer = PreviewLayer;
                group.transform.SetParent(parent, false);
                group.transform.localPosition = position;
                group.transform.localRotation = rotation;
                counters.Created.Add(group);

                float postHeight = 2.6f;
                CreateBox("post_left", group.transform, new Vector3(-width * 0.5f, postHeight * 0.5f, 0f),
                    Quaternion.identity, new Vector3(0.12f, postHeight, 0.12f), material, counters);
                CreateBox("post_right", group.transform, new Vector3(width * 0.5f, postHeight * 0.5f, 0f),
                    Quaternion.identity, new Vector3(0.12f, postHeight, 0.12f), material, counters);
                CreateBox("lintel", group.transform, new Vector3(0f, postHeight + 0.06f, 0f), Quaternion.identity,
                    new Vector3(width + 0.24f, 0.12f, 0.12f), material, counters);

                if (absorb)
                {
                    // Collapsed bench across the door: the blocker the gate describes.
                    CreateBox("bench_block", group.transform, new Vector3(0f, 0.45f, 0f), Quaternion.identity,
                        new Vector3(width * 0.9f, 0.28f, 0.42f), material, counters);
                }
                else
                {
                    CreateBox("step_edge", group.transform, new Vector3(0f, 0.02f, 0f), Quaternion.identity,
                        new Vector3(width, 0.04f, 0.3f), material, counters);
                }

                string text = gate.id + "\n" + (absorb ? "absorb >=" : "climb >=") + " " +
                              gate.absorbable_at_ball_diameter_m.ToString("0.##", CultureInfo.InvariantCulture) +
                              " m";
                CreateLabel(gate.id + "_label", labelParent, text, position + new Vector3(0f, postHeight + 0.9f, 0f),
                    0.62f, material.color, font, counters);
                counters.SizeGates++;
            }
        }

        // -------------------------------------------------------------- chokepoints ---

        private static void BuildChokepoints(GreyboxPayload payload, Transform parent,
            Dictionary<string, Material> materials, BuildCounters counters)
        {
            for (int i = 0; i < payload.chokepoints.Length; i++)
            {
                GreyboxChokepoint choke = payload.chokepoints[i];
                var position = new Vector3(choke.position_x, choke.position_y, choke.position_z);
                float yaw = Mathf.Atan2(position.x, position.z) * Mathf.Rad2Deg;
                string materialKey = choke.narrow_small_ball_lane != 0 ? "ChokepointNarrow" : "Chokepoint";
                Material material = Mat(materials, materialKey);

                var group = new GameObject(choke.id);
                group.layer = PreviewLayer;
                group.transform.SetParent(parent, false);
                group.transform.localPosition = position;
                group.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                counters.Created.Add(group);

                CreateCylinder("post", group.transform, new Vector3(0f, 0.6f, 0f), Quaternion.identity, 0.11f, 1.2f,
                    material, counters);
                CreateBox("clear_width_bar", group.transform, new Vector3(0f, 0.12f, 0f), Quaternion.identity,
                    new Vector3(Mathf.Max(0.2f, choke.clear_width_m), 0.03f, 0.08f), material, counters);
                counters.Chokepoints++;
            }
        }

        // ------------------------------------------------------------------ routes ---

        private static void BuildRoutes(GreyboxPayload payload, Transform parent, Dictionary<string, Material> materials,
            BuildCounters counters)
        {
            for (int i = 0; i < payload.routes.Length; i++)
            {
                GreyboxRoute route = payload.routes[i];
                var group = new GameObject(route.id);
                group.layer = PreviewLayer;
                group.transform.SetParent(parent, false);
                counters.Created.Add(group);

                string materialKey = string.Equals(route.kind, "safe_loop", StringComparison.Ordinal)
                    ? "RouteLoop"
                    : string.Equals(route.kind, "gated_shortcut", StringComparison.Ordinal)
                        ? "RouteShortcut"
                        : "Route";
                Material material = Mat(materials, materialKey);
                int count = route.waypoints_x.Length;
                bool closed = route.is_closed != 0;

                for (int w = 0; w < count; w++)
                {
                    var point = new Vector3(route.waypoints_x[w], RouteNodeY, route.waypoints_z[w]);
                    CreateSphere(route.id + "_wp_" + w.ToString("00", CultureInfo.InvariantCulture), group.transform,
                        point, new Vector3(0.16f, 0.16f, 0.16f), material, counters);
                    counters.RouteWaypoints++;

                    bool hasNext = w + 1 < count || closed;
                    if (!hasNext)
                    {
                        continue;
                    }

                    int next = (w + 1) % count;
                    var a = new Vector3(route.waypoints_x[w], RouteRibbonY, route.waypoints_z[w]);
                    var b = new Vector3(route.waypoints_x[next], RouteRibbonY, route.waypoints_z[next]);
                    Vector3 direction = b - a;
                    float length = direction.magnitude;
                    if (length < 0.05f)
                    {
                        continue;
                    }

                    CreateBox(route.id + "_seg_" + w.ToString("00", CultureInfo.InvariantCulture) + "_" +
                              next.ToString("00", CultureInfo.InvariantCulture), group.transform, (a + b) * 0.5f,
                        Quaternion.LookRotation(direction, Vector3.up), new Vector3(0.06f, 0.02f, length), material,
                        counters);
                    counters.RouteSegments++;
                }
            }
        }

        // ---------------------------------------------------------------- lighting ---

        private static void BuildLighting(Transform parent, BuildCounters counters)
        {
            var sun = new GameObject("GreyboxSun");
            sun.layer = PreviewLayer;
            sun.transform.SetParent(parent, false);
            sun.transform.localRotation = Quaternion.Euler(50f, -35f, 0f);
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.97f, 0.92f);
            light.intensity = 1.05f;
            light.shadows = LightShadows.None;
            counters.Created.Add(sun);
        }

        // ----------------------------------------------------------------- preview ---

        private static bool RenderTopDownPreview(Transform root, BuildCounters counters, List<string> warnings)
        {
            Camera camera = null;
            RenderTexture target = null;
            GameObject cameraObject = null;
            try
            {
                cameraObject = new GameObject("__GreyboxPreviewCamera");
                cameraObject.layer = PreviewLayer;
                cameraObject.transform.SetParent(root, false);
                cameraObject.transform.localPosition = new Vector3(0f, 80f, 0f);
                cameraObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

                camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = PreviewOrthoSize;
                camera.nearClipPlane = 0.5f;
                camera.farClipPlane = 250f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.07f, 0.08f, 0.10f, 1f);
                camera.allowHDR = false;
                camera.allowMSAA = true;
                camera.cullingMask = 1 << PreviewLayer;

                target = new RenderTexture(PreviewResolution, PreviewResolution, 24, RenderTextureFormat.ARGB32);
                target.antiAliasing = 1;
                target.Create();
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                var texture = new Texture2D(PreviewResolution, PreviewResolution, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0f, 0f, PreviewResolution, PreviewResolution), 0, 0);
                texture.Apply();
                RenderTexture.active = null;
                camera.targetTexture = null;

                byte[] png = texture.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(texture);

                string absolutePath = ToAbsolutePath(PreviewPath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? SceneFolder);
                File.WriteAllBytes(absolutePath, png);
                AssetDatabase.ImportAsset(PreviewPath, ImportAssetOptions.ForceUpdate);
                return true;
            }
            catch (Exception exception)
            {
                warnings.Add("top-down preview skipped: " + exception.Message);
                return false;
            }
            finally
            {
                RenderTexture.active = null;
                if (camera != null)
                {
                    camera.targetTexture = null;
                }

                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }

                if (cameraObject != null)
                {
                    counters.Created.Remove(cameraObject);
                    UnityEngine.Object.DestroyImmediate(cameraObject);
                }
            }
        }

        private static void ResetPreviewLayer(BuildCounters counters)
        {
            for (int i = 0; i < counters.Created.Count; i++)
            {
                GameObject go = counters.Created[i];
                if (go != null && go.layer == PreviewLayer)
                {
                    go.layer = 0;
                }
            }
        }

        // ---------------------------------------------------------------- reporting ---

        private static void WriteReport(GreyboxPayload payload, string payloadPath, BuildCounters counters,
            double buildSeconds, bool previewWritten, List<string> warnings, List<string> notes, List<string> errors)
        {
            Bounds totalBounds = default(Bounds);
            bool hasBounds = false;
            for (int i = 0; i < counters.Renderers.Count; i++)
            {
                Renderer renderer = counters.Renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    totalBounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    totalBounds.Encapsulate(renderer.bounds);
                }
            }

            var report = new GreyboxReportData
            {
                map_id = payload.map_id,
                payload_schema_version = payload.schema_version,
                payload_path = payloadPath,
                payload_repo_relative_path = PayloadRepoRelativePath,
                scene_path = ScenePath,
                preview_png_path = previewWritten ? PreviewPath : string.Empty,
                preview_written = previewWritten,
                validation_ok = errors.Count == 0,
                build_seconds = (float)buildSeconds,
                material_count = Palette.Count,
                renderer_total = counters.Renderers.Count,
                gameobject_count = counters.Created.Count,
                counts = new GreyboxReportCounts
                {
                    zones = counters.Zones,
                    structures_total = payload.structures.Length,
                    structure_objects = counters.StructureObjects,
                    structures_skipped = counters.StructuresSkipped,
                    structure_colliders = counters.StructureColliders,
                    prop_zones = counters.PropZones,
                    props_requested = counters.PropsRequested,
                    props_placed = counters.PropsPlaced,
                    props_dropped = counters.PropsDropped,
                    props_global_spacing = counters.PropsGlobalSpacing,
                    props_per_definition_spacing_only = counters.PropsPerDefinitionSpacingOnly,
                    spawns = counters.Spawns,
                    bank_anchors = counters.BankAnchors,
                    size_gates = counters.SizeGates,
                    chokepoints = counters.Chokepoints,
                    route_waypoints = counters.RouteWaypoints,
                    route_segments = counters.RouteSegments,
                    labels = counters.Labels,
                },
                bounds = new GreyboxReportBounds
                {
                    min_x = hasBounds ? totalBounds.min.x : 0f,
                    max_x = hasBounds ? totalBounds.max.x : 0f,
                    min_y = hasBounds ? totalBounds.min.y : 0f,
                    max_y = hasBounds ? totalBounds.max.y : 0f,
                    min_z = hasBounds ? totalBounds.min.z : 0f,
                    max_z = hasBounds ? totalBounds.max.z : 0f,
                },
                warnings = warnings.ToArray(),
                notes = notes.ToArray(),
                errors = errors.ToArray(),
            };

            string absolutePath = ToAbsolutePath(ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? SceneFolder);
            File.WriteAllText(absolutePath, JsonUtility.ToJson(report, true) + Environment.NewLine,
                new UTF8Encoding(false));
            AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceUpdate);
        }

        // --------------------------------------------------------------- validation ---

        private static bool ValidatePayload(GreyboxPayload payload, List<string> errors, List<string> warnings)
        {
            if (payload == null)
            {
                errors.Add("payload could not be parsed (JsonUtility returned null)");
                return false;
            }

            if (payload.schema_version != ExpectedSchemaVersion)
            {
                errors.Add("schema_version is " + payload.schema_version + ", expected " + ExpectedSchemaVersion);
            }

            if (string.IsNullOrEmpty(payload.map_id))
            {
                errors.Add("map_id is empty");
            }

            if (payload.bounds == null)
            {
                errors.Add("bounds is missing");
                return false;
            }

            GreyboxBounds bounds = payload.bounds;
            if (bounds.min_x >= bounds.max_x || bounds.min_z >= bounds.max_z)
            {
                errors.Add("bounds are degenerate: x[" + bounds.min_x + ", " + bounds.max_x + "] z[" + bounds.min_z +
                           ", " + bounds.max_z + "]");
            }

            RequireArray(payload.zones, "zones", ExpectedZones, errors);
            RequireArray(payload.structures, "structures", ExpectedStructures, errors);
            RequireArray(payload.spawns, "spawns", ExpectedSpawns, errors);
            RequireArray(payload.bank_anchors, "bank_anchors", ExpectedBankAnchors, errors);
            RequireArray(payload.size_gates, "size_gates", ExpectedSizeGates, errors);
            RequireArray(payload.scatter_regions, "scatter_regions", ExpectedScatterRegions, errors);
            RequireArray(payload.routes, "routes", ExpectedRoutes, errors);
            RequireArray(payload.chokepoints, "chokepoints", ExpectedChokepoints, errors);
            RequireArray(payload.props, "props", ExpectedProps, errors);

            if (payload.bank_rotation == null)
            {
                errors.Add("bank_rotation is missing");
            }

            if (errors.Count > 0)
            {
                return false;
            }

            var zoneIds = new HashSet<string>();
            for (int i = 0; i < payload.zones.Length; i++)
            {
                GreyboxZone zone = payload.zones[i];
                if (string.IsNullOrEmpty(zone.id))
                {
                    errors.Add("zones[" + i + "] has an empty id");
                    continue;
                }

                if (!zoneIds.Add(zone.id))
                {
                    errors.Add("zone id '" + zone.id + "' is duplicated");
                }

                if (zone.min_x >= zone.max_x || zone.min_z >= zone.max_z)
                {
                    errors.Add("zone '" + zone.id + "' has a degenerate rectangle");
                }

                if (!WithinBounds(zone.min_x, zone.min_z, bounds, 0.5f) ||
                    !WithinBounds(zone.max_x, zone.max_z, bounds, 0.5f))
                {
                    errors.Add("zone '" + zone.id + "' extends outside the map bounds");
                }

                if (!IsFinite(zone.ground_y_m) || zone.ground_y_m < -0.5f || zone.ground_y_m > 3f)
                {
                    errors.Add("zone '" + zone.id + "' has an implausible ground_y_m " + zone.ground_y_m);
                }

                if (zone.area_m2 <= 0f)
                {
                    errors.Add("zone '" + zone.id + "' has area_m2 <= 0");
                }

                if (string.IsNullOrEmpty(zone.surface))
                {
                    errors.Add("zone '" + zone.id + "' has no surface");
                }
            }

            var propIds = new HashSet<string>();
            for (int i = 0; i < payload.props.Length; i++)
            {
                GreyboxProp prop = payload.props[i];
                if (string.IsNullOrEmpty(prop.id))
                {
                    errors.Add("props[" + i + "] has an empty id");
                    continue;
                }

                if (!propIds.Add(prop.id))
                {
                    errors.Add("prop id '" + prop.id + "' is duplicated");
                }

                if (prop.size_tier != "s" && prop.size_tier != "m" && prop.size_tier != "l")
                {
                    errors.Add("prop '" + prop.id + "' has unknown size_tier '" + prop.size_tier + "'");
                }

                if (!IsFinite(prop.game_longest_dim_m) || prop.game_longest_dim_m <= 0f)
                {
                    errors.Add("prop '" + prop.id + "' has a non-positive game_longest_dim_m");
                }
            }

            for (int i = 0; i < payload.structures.Length; i++)
            {
                GreyboxStructure structure = payload.structures[i];
                string label = "structure " + structure.definition_id + "#" + structure.index;
                if (string.IsNullOrEmpty(structure.definition_id))
                {
                    errors.Add("structures[" + i + "] has an empty definition_id");
                }

                if (!IsFinite(structure.position_x) || !IsFinite(structure.position_y) ||
                    !IsFinite(structure.position_z))
                {
                    errors.Add(label + " has a non-finite position");
                }

                if (!WithinBounds(structure.position_x, structure.position_z, bounds, StructurePositionMargin))
                {
                    errors.Add(label + " sits outside the map bounds (+" + StructurePositionMargin + " m margin)");
                }

                if (Mathf.Abs(structure.size_x) < 0.001f || Mathf.Abs(structure.size_y) < 0.001f ||
                    Mathf.Abs(structure.size_z) < 0.001f)
                {
                    errors.Add(label + " has a zero-sized extent");
                }

                if (!string.IsNullOrEmpty(structure.zone_id) && !zoneIds.Contains(structure.zone_id))
                {
                    errors.Add(label + " references unknown zone '" + structure.zone_id + "'");
                }

            }

            for (int i = 0; i < payload.spawns.Length; i++)
            {
                GreyboxSpawn spawn = payload.spawns[i];
                if (string.IsNullOrEmpty(spawn.id))
                {
                    errors.Add("spawns[" + i + "] has an empty id");
                }

                if (!WithinBounds(spawn.position_x, spawn.position_z, bounds, 0.5f))
                {
                    errors.Add("spawn '" + spawn.id + "' sits outside the map bounds");
                }

                if (!zoneIds.Contains(spawn.zone_id))
                {
                    errors.Add("spawn '" + spawn.id + "' references unknown zone '" + spawn.zone_id + "'");
                }
            }

            for (int i = 0; i < payload.bank_anchors.Length; i++)
            {
                GreyboxBankAnchor anchor = payload.bank_anchors[i];
                if (!WithinBounds(anchor.position_x, anchor.position_z, bounds, 0.5f))
                {
                    errors.Add("bank anchor '" + anchor.id + "' sits outside the map bounds");
                }

                if (anchor.radius_m <= 0f)
                {
                    errors.Add("bank anchor '" + anchor.id + "' has radius_m <= 0");
                }
            }

            if (payload.bank_rotation.order != null)
            {
                var anchorIds = new HashSet<string>();
                for (int i = 0; i < payload.bank_anchors.Length; i++)
                {
                    anchorIds.Add(payload.bank_anchors[i].id);
                }

                for (int i = 0; i < payload.bank_rotation.order.Length; i++)
                {
                    if (!anchorIds.Contains(payload.bank_rotation.order[i]))
                    {
                        errors.Add("bank_rotation.order references unknown anchor '" + payload.bank_rotation.order[i] +
                                   "'");
                    }
                }
            }

            for (int i = 0; i < payload.size_gates.Length; i++)
            {
                GreyboxSizeGate gate = payload.size_gates[i];
                if (gate.type != "absorb" && gate.type != "climb")
                {
                    errors.Add("size gate '" + gate.id + "' has unknown type '" + gate.type + "'");
                }

                if (!WithinBounds(gate.position_x, gate.position_z, bounds, RoutePositionMargin))
                {
                    errors.Add("size gate '" + gate.id + "' sits outside the map bounds");
                }

                if (gate.clear_width_m <= 0f)
                {
                    errors.Add("size gate '" + gate.id + "' has clear_width_m <= 0");
                }

                if (gate.absorbable_at_ball_diameter_m < gate.blocks_ball_diameter_below_m)
                {
                    errors.Add("size gate '" + gate.id + "' opens below its blocking diameter");
                }
            }

            for (int i = 0; i < payload.chokepoints.Length; i++)
            {
                GreyboxChokepoint choke = payload.chokepoints[i];
                if (!WithinBounds(choke.position_x, choke.position_z, bounds, RoutePositionMargin))
                {
                    errors.Add("chokepoint '" + choke.id + "' sits outside the map bounds");
                }

                if (choke.clear_width_m <= 0f)
                {
                    errors.Add("chokepoint '" + choke.id + "' has clear_width_m <= 0");
                }
            }

            int requestedProps = 0;
            for (int i = 0; i < payload.scatter_regions.Length; i++)
            {
                GreyboxScatterRegion region = payload.scatter_regions[i];
                string label = "scatter region '" + region.zone_id + "'";
                if (!zoneIds.Contains(region.zone_id))
                {
                    errors.Add(label + " references an unknown zone");
                }

                if (string.IsNullOrEmpty(region.tier_band))
                {
                    errors.Add(label + " has no tier_band");
                }

                if (region.min_spacing_m <= 0f)
                {
                    errors.Add(label + " has min_spacing_m <= 0");
                }

                if (region.prop_ids == null || region.prop_counts_min == null || region.prop_counts_max == null)
                {
                    errors.Add(label + " is missing its prop arrays");
                    continue;
                }

                int definitions = region.prop_ids.Length;
                if (definitions != region.prop_counts_min.Length || definitions != region.prop_counts_max.Length ||
                    definitions != region.prop_definition_count)
                {
                    errors.Add(label + " has mismatched prop array lengths (ids " + definitions + ", counts_min " +
                               region.prop_counts_min.Length + ", counts_max " + region.prop_counts_max.Length +
                               ", prop_definition_count " + region.prop_definition_count + ")");
                }

                var seen = new HashSet<string>();
                int sumMin = 0;
                int sumMax = 0;
                for (int d = 0; d < definitions && d < region.prop_counts_min.Length && d < region.prop_counts_max.Length;
                     d++)
                {
                    string propId = region.prop_ids[d];
                    if (!propIds.Contains(propId))
                    {
                        errors.Add(label + " references unknown prop id '" + propId + "'");
                        continue;
                    }

                    if (!seen.Add(propId))
                    {
                        errors.Add(label + " lists prop '" + propId + "' twice");
                    }

                    if (region.prop_counts_min[d] < 0 || region.prop_counts_max[d] < region.prop_counts_min[d])
                    {
                        errors.Add(label + " has an invalid count range for '" + propId + "'");
                    }

                    sumMin += region.prop_counts_min[d];
                    sumMax += region.prop_counts_max[d];

                }

                if (sumMin != region.count_min_total || sumMax != region.count_max_total)
                {
                    errors.Add(label + " totals disagree: array sum " + sumMin + "/" + sumMax + " vs declared " +
                               region.count_min_total + "/" + region.count_max_total);
                }

                requestedProps += sumMin;
            }

            for (int i = 0; i < payload.routes.Length; i++)
            {
                GreyboxRoute route = payload.routes[i];
                string label = "route '" + route.id + "'";
                if (route.waypoints_x == null || route.waypoints_z == null)
                {
                    errors.Add(label + " is missing its waypoint arrays");
                    continue;
                }

                if (route.waypoints_x.Length != route.waypoints_z.Length ||
                    route.waypoints_x.Length != route.waypoint_count)
                {
                    errors.Add(label + " has mismatched waypoint counts");
                }

                if (route.waypoint_count < 2)
                {
                    errors.Add(label + " has fewer than 2 waypoints");
                }

                for (int w = 0; w < route.waypoints_x.Length && w < route.waypoints_z.Length; w++)
                {
                    if (!WithinBounds(route.waypoints_x[w], route.waypoints_z[w], bounds, RoutePositionMargin))
                    {
                        errors.Add(label + " waypoint " + w + " sits outside the map bounds");
                        break;
                    }
                }

                if (route.width_m <= 0f)
                {
                    errors.Add(label + " has width_m <= 0");
                }
            }

            // Design cross-checks: scatter instances + absorb gates should add up to the
            // catalog's absorbable total, and the structure list should match the fixed
            // instance count. Mismatches are reported but do not block a greybox build.
            int absorbableGates = 0;
            for (int i = 0; i < payload.size_gates.Length; i++)
            {
                if (payload.size_gates[i].type == "absorb")
                {
                    absorbableGates++;
                }
            }

            if (requestedProps + absorbableGates != DesignAbsorbableInstances)
            {
                warnings.Add("scatter instances (" + requestedProps + ") + absorb gates (" + absorbableGates +
                             ") = " + (requestedProps + absorbableGates) + ", design says " + DesignAbsorbableInstances);
            }

            if (payload.structures.Length != DesignFixedInstances)
            {
                warnings.Add("structures " + payload.structures.Length + ", design says " + DesignFixedInstances);
            }

            return errors.Count == 0;
        }

        private static void RequireArray<T>(T[] array, string label, int expected, List<string> errors)
        {
            if (array == null)
            {
                errors.Add(label + " is missing");
                return;
            }

            if (array.Length != expected)
            {
                errors.Add(label + " has " + array.Length + " records, expected " + expected);
            }
        }

        private static bool WithinBounds(float x, float z, GreyboxBounds bounds, float margin)
        {
            return x >= bounds.min_x - margin && x <= bounds.max_x + margin && z >= bounds.min_z - margin &&
                   z <= bounds.max_z + margin;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // ------------------------------------------------------------------- paths ---

        private static string ResolvePayloadPath(out List<string> tried)
        {
            tried = new List<string>();
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            var candidates = new List<string>
            {
                Path.GetFullPath(Path.Combine(projectRoot, "..", PayloadRepoRelativePath)),
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", PayloadRepoRelativePath)),
                Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), PayloadRepoRelativePath)),
            };

            string directory = projectRoot;
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(directory); i++)
            {
                candidates.Add(Path.GetFullPath(Path.Combine(directory, PayloadRepoRelativePath)));
                candidates.Add(Path.GetFullPath(Path.Combine(directory, "..", PayloadRepoRelativePath)));
                directory = Path.GetDirectoryName(directory);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];
                if (!tried.Contains(candidate))
                {
                    tried.Add(candidate);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder == "Assets" || AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            string parent = Path.GetDirectoryName(folder);
            if (string.IsNullOrEmpty(parent))
            {
                return;
            }

            parent = parent.Replace('\\', '/');
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        // --------------------------------------------------------------- bookkeeping ---

        private sealed class Placement
        {
            public float X;
            public float Z;
            public string PropId;
        }

        private sealed class BuildCounters
        {
            public int Zones;
            public int StructureObjects;
            public int StructuresSkipped;
            public int StructureColliders;
            public int PropZones;
            public int PropsRequested;
            public int PropsPlaced;
            public int PropsDropped;
            public int PropsGlobalSpacing;
            public int PropsPerDefinitionSpacingOnly;
            public int Spawns;
            public int BankAnchors;
            public int SizeGates;
            public int Chokepoints;
            public int RouteWaypoints;
            public int RouteSegments;
            public int Labels;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<GameObject> Created = new List<GameObject>();
        }

        [Serializable]
        private class GreyboxReportData
        {
            public string map_id;
            public int payload_schema_version;
            public string payload_repo_relative_path;
            public string payload_path;
            public string scene_path;
            public string preview_png_path;
            public bool preview_written;
            public bool validation_ok;
            public float build_seconds;
            public int material_count;
            public int renderer_total;
            public int gameobject_count;
            public GreyboxReportCounts counts;
            public GreyboxReportBounds bounds;
            public string[] warnings;
            public string[] notes;
            public string[] errors;
        }

        [Serializable]
        private class GreyboxReportCounts
        {
            public int zones;
            public int structures_total;
            public int structure_objects;
            public int structures_skipped;
            public int structure_colliders;
            public int prop_zones;
            public int props_requested;
            public int props_placed;
            public int props_dropped;
            public int props_global_spacing;
            public int props_per_definition_spacing_only;
            public int spawns;
            public int bank_anchors;
            public int size_gates;
            public int chokepoints;
            public int route_waypoints;
            public int route_segments;
            public int labels;
        }

        [Serializable]
        private class GreyboxReportBounds
        {
            public float min_x;
            public float max_x;
            public float min_y;
            public float max_y;
            public float min_z;
            public float max_z;
        }
    }
}
