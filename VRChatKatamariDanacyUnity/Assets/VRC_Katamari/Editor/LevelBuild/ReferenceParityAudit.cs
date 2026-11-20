using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRCKatamari.Editor.Pickup;
using VRCKatamari.Pickup;
using VRCKatamari.Story;

namespace VRCKatamari.Editor.LevelBuild
{
    internal static class ReferenceParityAudit
    {
        internal static void Append(string levelId, Transform shell, List<string> lines, List<string> problems)
        {
            if (!LevelPaths.IsReferenceLevelId(levelId)) return;
            string tag = "[ref-parity] " + levelId + " ";
            try
            {
                if (!LevelPaths.TryResolvePlacementPath(levelId, out string path, out _))
                {
                    problems.Add(tag + "对账未执行：找不到 placement.v2.json");
                    return;
                }
                PlacementV2ReadResult read = PlacementV2Reader.ReadFile(path);
                if (!read.Ok)
                {
                    problems.Add(tag + "对账未执行：" + string.Join("；", read.Errors.Take(3)));
                    return;
                }
                PlacementV2Document doc = read.Document;
                string rootPath = PlacementImporter.ManagedRootName + "/" + doc.Meta.Id;
                Transform root = shell == null ? null : shell.Find(rootPath);
                if (root == null)
                {
                    problems.Add(tag + "对账未执行：岛下未找到 " + rootPath);
                    return;
                }
                Audit(doc, root, path, tag, lines, problems);
            }
            catch (Exception exception)
            {
                problems.Add(tag + "对账未完成：" + exception.Message);
            }
        }

        private static void Audit(PlacementV2Document doc, Transform root, string path,
            string tag, List<string> lines, List<string> problems)
        {
            var details = new List<string>();
            string[] expected = doc.Props.Select(prop => prop.ItemId).ToArray();
            var expectedIds = new HashSet<string>(expected, StringComparer.Ordinal);
            var defs = doc.Defs.ToDictionary(def => def.Id, StringComparer.Ordinal);
            var visualIds = new HashSet<string>(doc.Props.Where(prop =>
                defs.TryGetValue(prop.Def, out PlacementV2Def def) && ReferenceParityMath.ExpectsVisible(def))
                .Select(prop => prop.ItemId), StringComparer.Ordinal);
            HashSet<string> animated = ReadAnimated(doc, path, details, tag);
            HashSet<string> skinDefs = ReadSkinDefs(details, tag);
            var scene = new SceneProps();
            Transform props = root.Find(PlacementImporter.PropsFolderName);
            if (props != null) ReadProps(props, expectedIds, scene);
            else problems.Add(tag + "缺少 Props 根");

            gap(true, "instances", expected, scene.Ids);
            if (doc.Meta.Expected != null && doc.Meta.Expected.PropCount >= 0 && doc.Meta.Expected.PropCount != expected.Length)
                problems.Add(tag + "placement 的 meta.expected.propCount 与 props[] 长度不一致");
            gap(true, "visible", visualIds, scene.Visible);
            issue(true, "骨架不完整", scene.BrokenSkins.Count, scene.BrokenSkins);
            if (skinDefs != null)
            {
                var missing = doc.Props.Where(prop => skinDefs.Contains(prop.Def) && !scene.SkinnedItems.Contains(prop.ItemId))
                    .Select(prop => prop.ItemId).ToArray();
                issue(true, "已导出蒙皮的道具缺 SMR", missing.Length, missing);
            }
            gap(false, "textured", visualIds, scene.Textured);
            if (animated != null) gap(false, "animators", animated, scene.Animated);

            string[] boxesExpected = ReferenceParityMath.ExpectedStaticBoxes(doc).ToArray();
            LevelArenaHost arena = root.GetComponent<LevelArenaHost>();
            SerializedProperty boxes = arena == null ? null : new SerializedObject(arena).FindProperty("staticBoxes");
            Transform structures = root.Find(PlacementImporter.StructuresFolderName);
            var boxIds = new List<string>();
            var seenBoxes = new HashSet<BoxCollider>();
            for (int i = 0; boxes != null && i < boxes.arraySize; i++)
            {
                var box = boxes.GetArrayElementAtIndex(i).objectReferenceValue as BoxCollider;
                boxIds.Add(box != null && structures != null && box.transform.parent == structures && seenBoxes.Add(box)
                    ? box.name : "<无效盒槽 " + i + ">");
            }
            if (boxes == null) problems.Add(tag + "未读到本关 LevelArenaHost.staticBoxes");
            gap(true, "staticBoxes", boxesExpected, boxIds);

            string[] offArea = doc.Props.Where(prop => !AreaCollisionMath.CollidesInArea(prop.HitOnAreaNo, doc.Meta.AreaNo) &&
                !(prop.HasScale && !PlacementV2Rules.IsUniformScale(prop.Scale) && !prop.Absorbable &&
                    (!defs.TryGetValue(prop.Def, out PlacementV2Def def) || def.Collider == null)))
                .Select(prop => prop.ItemId).ToArray();
            PickupFieldHost pickup = root.GetComponentInChildren<PickupFieldHost>(true);
            var pickupRows = new HashSet<string>(StringColumn(pickup, "itemIds"), StringComparer.Ordinal);
            string[] offAreaRows = StringColumn(pickup, "areaCollisionOffItemIds");
            gap(true, "areaCollisionOff", offArea, offAreaRows.Where(pickupRows.Contains));
            string[] unresolvedAreaRows = offAreaRows.Where(id => !pickupRows.Contains(id)).ToArray();
            issue(true, "areaCollisionOff 悬空", unresolvedAreaRows.Length, unresolvedAreaRows);

            string[] declaredMotion = doc.Props.Where(ReferenceParityMath.DeclaresMotion).Select(prop => prop.ItemId).ToArray();
            MotionRowTable expectedMotion = PlacementMotionRows.Build(doc, animated);
            var motion = ReadMotion(root, scene.Roots, details, tag);
            gap(false, "motionRows（placement 声明覆盖）", declaredMotion,
                motion.Keys.Where(new HashSet<string>(declaredMotion, StringComparer.Ordinal).Contains));
            if (animated != null) gap(false, "motionRowsAll", expectedMotion.ItemIds, motion.Keys);
            else gap(false, "motionRows（程序/随父链覆盖）", expectedMotion.ItemIds,
                motion.Keys.Where(new HashSet<string>(expectedMotion.ItemIds).Contains));
            foreach (string error in expectedMotion.Errors) details.Add(tag + "warning " + error);
            var classGaps = new List<string>();
            var badLoops = new List<string>();
            int loops = 0, loopExpected = 0;
            for (int i = 0; i < expectedMotion.RowCount; i++)
            {
                string id = expectedMotion.ItemIds[i];
                int expectedClass = expectedMotion.MotionClass[i];
                bool present = motion.TryGetValue(id, out MotionRow row);
                if (present && row.Class != expectedClass) classGaps.Add(id);
                if (expectedClass != MotionHostMath.ClassWhackAMole) continue;
                loopExpected++;
                if (present && row.Class == expectedClass && row.Loop) loops++;
                else badLoops.Add(id);
            }
            issue(false, "motionClass 不符", classGaps.Count, classGaps);
            issue(false, "whackLoop 未循环", badLoops.Count, badLoops);

            AuditAlpha(scene.Materials, out int alpha, out List<string> cutout, out List<string> untraced);
            issue(true, "cutout 与源 GLB 透明模式不符", cutout.Count, cutout);
            issue(false, "cutout 未能对到源 GLB 材质", untraced.Count, untraced);

            lines.Add(tag + $"instances={scene.Ids.Count}/{expected.Length} " +
                $"visible={scene.Visible.Count}/{visualIds.Count} skins={scene.Skins - scene.BrokenSkins.Count}/{scene.Skins} " +
                $"textured={scene.Textured.Count}/{visualIds.Count} animators={scene.Animated.Count}/{(animated == null ? "?" : animated.Count.ToString())} " +
                $"motionRows={motion.Count}/{declaredMotion.Length} motionDeclared={declaredMotion.Count(motion.ContainsKey)}/{declaredMotion.Length} " +
                $"motionRowsAll={motion.Count}/{(animated == null ? "?" : expectedMotion.RowCount.ToString())} whackLoop={loops}/{loopExpected} " +
                $"staticBoxes={boxIds.Count}/{boxesExpected.Length} cutout={cutout.Count}/{alpha}");
            lines.AddRange(details);

            void gap(bool blocking, string metric, IEnumerable<string> wanted, IEnumerable<string> actual)
            {
                ReferenceParityMath.Comparison result = ReferenceParityMath.Compare(wanted, actual);
                issue(blocking, metric + " 缺少", result.Missing, result.MissingIds);
                issue(blocking, metric + " 多出或重复", result.Extra, result.ExtraIds);
            }
            void issue(bool blocking, string metric, int count, IEnumerable<string> names) =>
                AddIssue(blocking ? problems : details, blocking ? tag : tag + "warning ", metric, count, names);
        }

        // The source GLB's alphaMode decides, by the importer's own rules: the denominator is the
        // scene materials whose source is MASK or BLEND, the numerator those not set up as that
        // mode (ReferenceMaterialAlphaMath.MissesSourceAlpha). A texture's own alpha does not
        // count: OPAQUE sources such as the minivan carry stray translucent texels.
        private static void AuditAlpha(Dictionary<Material, string> materials, out int alpha,
            out List<string> cutout, out List<string> untraced)
        {
            alpha = 0;
            cutout = new List<string>();
            untraced = new List<string>();
            var sources = new Dictionary<string, ReferenceMaterialAlphaMath.SourceMaterial[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<Material, string> pair in materials)
            {
                Material material = pair.Key;
                string model = pair.Value;
                if (model == null) continue;
                if (!sources.TryGetValue(model, out ReferenceMaterialAlphaMath.SourceMaterial[] list))
                {
                    ReferenceModelMaterialPostprocessor.TryReadSourceMaterials(model, out list, out _);
                    sources.Add(model, list);
                }
                bool embedded = string.Equals(AssetDatabase.GetAssetPath(material), model, StringComparison.Ordinal);
                string name = ReferenceMaterialAlphaMath.SourceMaterialName(
                    Path.GetFileNameWithoutExtension(model), material.name, embedded);
                ReferenceMaterialAlphaMath.SourceMaterial source = ReferenceMaterialAlphaMath.Find(list, name);
                if (source == null)
                {
                    untraced.Add(material.name);
                    continue;
                }
                ReferenceMaterialAlphaMath.RenderMode mode = source.Mode;
                if (mode != ReferenceMaterialAlphaMath.RenderMode.Cutout &&
                    mode != ReferenceMaterialAlphaMath.RenderMode.Fade) continue;
                alpha++;
                bool hasCutoff = material.HasProperty("_Cutoff");
                if (ReferenceMaterialAlphaMath.MissesSourceAlpha(mode, material.renderQueue, hasCutoff,
                    hasCutoff ? material.GetFloat("_Cutoff") : 0f)) cutout.Add(material.name);
            }
        }

        // The reference model a scene material came from: the model that embeds it, else the
        // model whose mesh the renderer draws (a MaterialInstancing copy). Null when neither is
        // a reference model.
        private static string ReferenceModelOf(Material material, Mesh mesh)
        {
            string path = AssetDatabase.GetAssetPath(material);
            if (ReferenceModelMaterialPostprocessor.IsReferenceModelPath(path)) return path;
            path = mesh == null ? null : AssetDatabase.GetAssetPath(mesh);
            return ReferenceModelMaterialPostprocessor.IsReferenceModelPath(path) ? path : null;
        }

        private static void AddIssue(List<string> destination, string prefix, string metric,
            int count, IEnumerable<string> names)
        {
            if (count > ReferenceParityMath.Tolerance)
                destination.Add(prefix + metric + " " + count + "：" +
                    string.Join(", ", ReferenceParityMath.Sample(names)));
        }

        private static string[] StringColumn(Component host, string field)
        {
            SerializedProperty column = host == null ? null : new SerializedObject(host).FindProperty(field);
            if (column == null) return Array.Empty<string>();
            var values = new string[column.arraySize];
            for (int i = 0; i < values.Length; i++) values[i] = column.GetArrayElementAtIndex(i).stringValue;
            return values;
        }

        private static HashSet<string> ReadAnimated(PlacementV2Document doc, string path,
            List<string> details, string tag)
        {
            string sideTable = Path.Combine(Path.GetDirectoryName(path), "motion_table.json");
            if (!File.Exists(sideTable))
            {
                details.Add(tag + "旁表缺失，未核 Animator 原作期望数与完整动作行期望数");
                return null;
            }
            try { return ReferenceParityMath.ReadAnimatedIds(doc, File.ReadAllText(sideTable)); }
            catch (Exception exception)
            {
                details.Add(tag + "warning 原作旁表不可用，未核：" + exception.Message);
                return null;
            }
        }

        private static HashSet<string> ReadSkinDefs(List<string> details, string tag)
        {
            string path = Path.Combine(LevelPaths.ProjectRoot(), SkeletalAnimationAssembler.AnimationRoot,
                "skinned_defs.json");
            try
            {
                if (File.Exists(path))
                {
                    LevelJsonNode defs = LevelJsonParser.ParseText(File.ReadAllText(path)).Get("defs");
                    if (defs != null && defs.IsObject) return new HashSet<string>(defs.KeyArray(), StringComparer.Ordinal);
                }
                details.Add(tag + "蒙皮清单缺失或无 defs，未核整件蒙皮覆盖率");
            }
            catch (Exception exception) { details.Add(tag + "warning 蒙皮清单不可读：" + exception.Message); }
            return null;
        }

        private sealed class SceneProps
        {
            public readonly List<string> Ids = new List<string>();
            public readonly List<string> Visible = new List<string>();
            public readonly List<string> Textured = new List<string>();
            public readonly List<string> Animated = new List<string>();
            public readonly List<string> BrokenSkins = new List<string>();
            public readonly HashSet<string> SkinnedItems = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Each visible material and the reference model it came from (or null).</summary>
            public readonly Dictionary<Material, string> Materials = new Dictionary<Material, string>();
            public readonly Dictionary<string, Transform> Roots = new Dictionary<string, Transform>(StringComparer.Ordinal);
            public int Skins;
        }

        private static void ReadProps(Transform folder, HashSet<string> expected, SceneProps scene)
        {
            foreach (Transform child in folder)
            {
                PickupFieldAuthoringRules.TryParseDeclaredSize(child.name, out string id, out _, out _);
                if (!expected.Contains(id) && child.GetComponent<Renderer>() == null)
                {
                    ReadProps(child, expected, scene);
                    continue;
                }
                scene.Ids.Add(id);
                if (!scene.Roots.ContainsKey(id)) scene.Roots.Add(id, child);
                if (child.GetComponentInChildren<Animator>(true) != null) scene.Animated.Add(id);
                bool visible = false, textured = true;
                foreach (Renderer renderer in child.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh mesh = null;
                    if (renderer is SkinnedMeshRenderer skin)
                    {
                        scene.Skins++;
                        scene.SkinnedItems.Add(id);
                        mesh = skin.sharedMesh;
                        Transform[] bones = skin.bones;
                        if (mesh == null || skin.rootBone == null || bones == null || bones.Length == 0 ||
                            bones.Any(bone => bone == null || !bone.IsChildOf(child)))
                        {
                            scene.BrokenSkins.Add(id);
                            continue;
                        }
                    }
                    else if (renderer is MeshRenderer)
                    {
                        MeshFilter filter = renderer.GetComponent<MeshFilter>();
                        if (filter != null) mesh = filter.sharedMesh;
                    }
                    if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy ||
                        mesh == null || mesh.vertexCount == 0 || mesh.subMeshCount == 0) continue;
                    visible = true;
                    Material[] materials = renderer.sharedMaterials;
                    if (materials.Length < mesh.subMeshCount) textured = false;
                    foreach (Material material in materials)
                    {
                        if (material == null || material.mainTexture == null) textured = false;
                        if (material != null && !scene.Materials.ContainsKey(material))
                            scene.Materials.Add(material, ReferenceModelOf(material, mesh));
                    }
                }
                if (visible) scene.Visible.Add(id);
                if (visible && textured) scene.Textured.Add(id);
            }
        }

        private struct MotionRow
        {
            public int Class;
            public bool Loop;
        }

        private static Dictionary<string, MotionRow> ReadMotion(Transform root,
            Dictionary<string, Transform> props, List<string> details, string tag)
        {
            var rows = new Dictionary<string, MotionRow>(StringComparer.Ordinal);
            var invalid = new List<string>();
            var duplicates = new List<string>();
            foreach (MotionHost host in root.GetComponentsInChildren<MotionHost>(true))
            {
                var serialized = new SerializedObject(host);
                SerializedProperty ids = serialized.FindProperty("rowItemIds");
                SerializedProperty transforms = serialized.FindProperty("rowTransforms");
                SerializedProperty classes = serialized.FindProperty("rowMotionClass");
                SerializedProperty loops = serialized.FindProperty("rowLoop");
                if (ids == null || transforms == null || classes == null || loops == null ||
                    ids.arraySize != transforms.arraySize || ids.arraySize != classes.arraySize || ids.arraySize != loops.arraySize)
                {
                    details.Add(tag + "warning MotionHost 行列长度不一致：" + host.name);
                    continue;
                }
                for (int i = 0; i < ids.arraySize; i++)
                {
                    string id = ids.GetArrayElementAtIndex(i).stringValue;
                    if (!props.TryGetValue(id, out Transform target) ||
                        transforms.GetArrayElementAtIndex(i).objectReferenceValue != target)
                    {
                        invalid.Add(string.IsNullOrEmpty(id) ? "<空行 " + i + ">" : id);
                        continue;
                    }
                    if (rows.ContainsKey(id)) { duplicates.Add(id); continue; }
                    rows.Add(id, new MotionRow { Class = classes.GetArrayElementAtIndex(i).intValue,
                        Loop = loops.GetArrayElementAtIndex(i).boolValue });
                }
            }
            AddIssue(details, tag + "warning ", "MotionHost 行未指向道具根", invalid.Count, invalid);
            AddIssue(details, tag + "warning ", "MotionHost 行重复", duplicates.Count, duplicates);
            return rows;
        }
    }
}
