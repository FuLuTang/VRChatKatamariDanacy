using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRCKatamari.BallPresentation;
using VRCKatamari.Hub;

namespace VRCKatamari.Editor.BallPresentation
{
    /// <summary>
    /// Optional local reference skins: every original core (core_XX) present under
    /// <see cref="SkinFolder"/> becomes one normalized display child of the ball's core, and
    /// <see cref="BallView"/> gets the table it switches with. Never imports, copies or
    /// generates reference assets.
    /// </summary>
    /// <remarks>
    /// Hierarchy: <c>CoreMesh/OriginalCoreSkin/core_NN</c>. <c>OriginalCoreSkin</c> is a plain
    /// unit-transform container; each <c>core_NN</c> carries a MeshFilter + MeshRenderer scaled
    /// by 1/D_mesh and centred (CORE_SKIN.md). Only the default skin's renderer starts enabled;
    /// the runtime switch only flips <c>enabled</c>. Two skins whose imported meshes are
    /// identical share one Mesh. An older single-skin <c>OriginalCoreSkin</c> (renderer on the
    /// container itself) is migrated into the container form.
    /// </remarks>
    public static class CoreSkinAssembler
    {
        public const string MenuPath = "VRC Katamari/Ball/Assemble Core Skin";
        public const string SkinFolder = "Assets/_RefUnlicensed/Models/KatamariCore";
        /// <summary>The default skin's files; the hub assembly skips skins when these are missing.</summary>
        public const string ModelPath = SkinFolder + "/core_01.obj";
        public const string TexturePath = SkinFolder + "/core_01.png";
        public const string SkinName = "OriginalCoreSkin";

        [MenuItem(MenuPath, false, 415)]
        public static void Assemble() { Debug.Log(AssembleOpenScene()); }

        /// <summary>The asset paths of the skin with original core number <paramref name="number"/>.</summary>
        public static string SkinModelPath(int number) { return SkinFolder + "/" + SkinChildName(number) + ".obj"; }

        public static string SkinTexturePath(int number) { return SkinFolder + "/" + SkinChildName(number) + ".png"; }

        /// <summary>"core_07": the display child's name and the reference files' base name.</summary>
        public static string SkinChildName(int number) { return "core_" + number.ToString("00"); }

        /// <summary>One loaded, validated skin.</summary>
        public sealed class SkinSource
        {
            public int Number;
            public Mesh Mesh;
            public Material Material;
            public float Scale;
            public double Diameter;
        }

        /// <summary>Only the active scene is eligible. The operator saves the scene after review.</summary>
        public static string AssembleOpenScene()
        {
            const string prefix = "Core skin: +0 ~0. Note: ";
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling
                || EditorApplication.isUpdating || PrefabStageUtility.GetCurrentPrefabStage() != null)
                return prefix + "stopped: leave Play/Prefab Mode and wait for import/compile.";
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return prefix + "no loaded active scene.";

            // Validate everything before starting the Undo transaction. No importer changes.
            var skipped = new List<string>();
            List<SkinSource> skins = LoadSkins(skipped);
            if (BallSkinMath.IndexOf(Numbers(skins), BallSkinMath.DefaultNumber) < 0)
                return prefix + "optional core_01 model/texture missing or unusable ("
                    + Join(skipped) + "); existing core left untouched.";

            var views = new List<BallView>();
            var cores = new List<Transform>();
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (BallView view in root.GetComponentsInChildren<BallView>(true))
            {
                var so = new SerializedObject(view);
                Transform core = so.FindProperty("coreMesh").objectReferenceValue as Transform;
                if (core == null || core.gameObject.scene != scene || core.parent == null)
                    return prefix + "BallView.coreMesh must point into this scene with a unit-scale parent.";
                // Runtime owns core.localScale, but all ancestors must preserve metres.
                for (Transform p = core.parent; p != null; p = p.parent)
                    if ((p.localScale - Vector3.one).sqrMagnitude > 1e-10f)
                        return prefix + "core ancestors must have unit scale; left untouched.";
                string custom = ContainerProblem(core);
                if (custom != null) return prefix + custom + "; left untouched.";
                if (core.GetComponent<MeshRenderer>() == null)
                    return prefix + "core needs its existing fallback MeshRenderer; left untouched.";
                if (cores.Contains(core)) return prefix + "two BallViews share one coreMesh; left untouched.";
                views.Add(view);
                cores.Add(core);
            }
            if (cores.Count == 0) return prefix + "no BallView in active scene.";
            if (scene.isDirty) return prefix + "save/review the active scene before assembly.";

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Assemble core skins");
            int created = 0;
            int changed = 0;
            var notes = new List<string>();
            try
            {
                for (int i = 0; i < cores.Count; i++)
                {
                    MeshRenderer[] renderers = ApplyToCore(cores[i], skins, ref created, ref changed);
                    if (WriteViewTable(views[i], Numbers(skins), renderers, notes)) changed++;
                    if (WireSettingsHost(scene, views[i], notes)) changed++;
                }
                Undo.CollapseUndoOperations(group);
                if (created + changed > 0) EditorSceneManager.MarkSceneDirty(scene);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(group);
                return prefix + "rolled back: " + exception.Message;
            }
            SkinSource first = skins[0];
            return "Core skin: +" + created + " ~" + changed + ". " + cores.Count
                + " core(s); mesh equivalent diameter=" + first.Diameter.ToString("G9")
                + ", normalization=" + first.Scale.ToString("G9")
                + " (" + SkinChildName(first.Number) + "). skins=" + skins.Count + " ["
                + string.Join(",", Array.ConvertAll(Numbers(skins), n => n.ToString("00")))
                + "], shared meshes=" + SharedMeshCount(skins)
                + (skipped.Count > 0 ? ", skipped: " + Join(skipped) : "")
                + (notes.Count > 0 ? ", warn: " + Join(notes) : "")
                + ". Note: displayed equivalent diameter = motor start diameter (Cancer 0.222 m);"
                + " bumpy bounds are larger. Reference-only: do not commit or upload the assembled scene.";
        }

        /// <summary>
        /// Every original core number whose OBJ and PNG load and validate, ascending. Missing
        /// files are skipped silently (only the listed ones were copied); a present but unusable
        /// skin is named in <paramref name="skipped"/>.
        /// </summary>
        public static List<SkinSource> LoadSkins(List<string> skipped)
        {
            var skins = new List<SkinSource>();
            for (int number = BallSkinMath.FirstNumber; number <= BallSkinMath.LastNumber; number++)
            {
                if (!BallSkinMath.IsOriginalNumber(number)) continue;
                GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(SkinModelPath(number));
                Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(SkinTexturePath(number));
                if (source == null && texture == null) continue;
                string name = SkinChildName(number);
                if (source == null || texture == null)
                {
                    skipped.Add(name + " (model or texture missing)");
                    continue;
                }
                MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
                if (filters.Length != 1) { skipped.Add(name + " (expected one imported mesh)"); continue; }
                Mesh mesh = filters[0].sharedMesh;
                MeshRenderer sourceRenderer = filters[0].GetComponent<MeshRenderer>();
                Material material = sourceRenderer != null ? sourceRenderer.sharedMaterial : null;
                if (mesh == null || !mesh.isReadable || mesh.subMeshCount != 1
                    || mesh.GetTopology(0) != MeshTopology.Triangles)
                {
                    skipped.Add(name + " (needs one readable triangle submesh: Read/Write on, Keep Quads off)");
                    continue;
                }
                if (material == null || material.mainTexture != texture)
                {
                    skipped.Add(name + " (imported material must reference " + name + ".png)");
                    continue;
                }
                double diameter;
                float scale;
                if (!TryNormalization(mesh.vertices, mesh.triangles, out diameter, out scale))
                {
                    skipped.Add(name + " (invalid or zero-volume mesh)");
                    continue;
                }
                // Identical geometry draws from one Mesh (less memory, same look).
                for (int i = 0; i < skins.Count; i++)
                {
                    if (SameGeometry(skins[i].Mesh, mesh)) { mesh = skins[i].Mesh; break; }
                }
                skins.Add(new SkinSource { Number = number, Mesh = mesh, Material = material,
                    Scale = scale, Diameter = diameter });
            }
            return skins;
        }

        // Keep the runtime-owned CoreMesh as the diameter transform. Only the display children
        // are normalized; imported root scale/axis wrappers are deliberately not instantiated.
        // Returns the display renderer of each skin, same order as skins.
        public static MeshRenderer[] ApplyToCore(Transform core, List<SkinSource> skins,
            ref int created, ref int changed)
        {
            int before = created + changed;
            var touched = new List<UnityEngine.Object>();
            Transform container = core.Find(SkinName);
            if (container == null)
            {
                var go = new GameObject(SkinName);
                Undo.RegisterCreatedObjectUndo(go, "Create core skin container");
                go.layer = core.gameObject.layer;
                go.transform.SetParent(core, false);
                container = go.transform;
                created++;
            }
            // Migrate the single-skin form: the container itself must not draw.
            MeshRenderer oldRenderer = container.GetComponent<MeshRenderer>();
            if (oldRenderer != null) { Undo.DestroyObjectImmediate(oldRenderer); changed++; }
            MeshFilter oldFilter = container.GetComponent<MeshFilter>();
            if (oldFilter != null) { Undo.DestroyObjectImmediate(oldFilter); changed++; }
            if (container.localPosition != Vector3.zero || container.localRotation != Quaternion.identity
                || container.localScale != Vector3.one)
            {
                Undo.RecordObject(container, "Reset core skin container");
                container.localPosition = Vector3.zero;
                container.localRotation = Quaternion.identity;
                container.localScale = Vector3.one;
                changed++;
            }
            if (!container.gameObject.activeSelf)
            {
                Undo.RecordObject(container.gameObject, "Show core skin container");
                container.gameObject.SetActive(true);
                changed++;
            }
            touched.Add(container);
            touched.Add(container.gameObject);

            // A skin whose files are gone no longer has a table entry, so nothing could switch
            // it off again: remove its display child.
            for (int i = container.childCount - 1; i >= 0; i--)
            {
                Transform child = container.GetChild(i);
                int number;
                if (TryParseSkinChild(child.name, out number)
                    && BallSkinMath.IndexOf(Numbers(skins), number) < 0)
                {
                    Undo.DestroyObjectImmediate(child.gameObject);
                    changed++;
                }
            }

            var renderers = new MeshRenderer[skins.Count];
            for (int i = 0; i < skins.Count; i++)
            {
                bool shown = skins[i].Number == BallSkinMath.DefaultNumber;
                renderers[i] = ApplySkin(container, skins[i], shown, touched, ref created, ref changed);
            }

            MeshRenderer fallback = core.GetComponent<MeshRenderer>();
            if (fallback.enabled)
            {
                Undo.RecordObject(fallback, "Hide fallback core");
                fallback.enabled = false;
                changed++;
            }
            touched.Add(fallback);
            if (created + changed != before)
            {
                for (int i = 0; i < touched.Count; i++) RecordPrefabOverride(touched[i]);
            }
            return renderers;
        }

        private static MeshRenderer ApplySkin(Transform container, SkinSource skin, bool shown,
            List<UnityEngine.Object> touched, ref int created, ref int changed)
        {
            string name = SkinChildName(skin.Number);
            Transform child = container.Find(name);
            if (child == null)
            {
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create core skin");
                go.layer = container.gameObject.layer;
                go.transform.SetParent(container, false);
                child = go.transform;
                Undo.AddComponent<MeshFilter>(go);
                Undo.AddComponent<MeshRenderer>(go);
                created++;
            }
            Vector3 position = -skin.Mesh.bounds.center * skin.Scale;
            Vector3 normalizedScale = Vector3.one * skin.Scale;
            if ((child.localPosition - position).sqrMagnitude > 1e-12f
                || child.localRotation != Quaternion.identity || child.localScale != normalizedScale)
            {
                Undo.RecordObject(child, "Normalize core skin");
                child.localPosition = position;
                child.localRotation = Quaternion.identity;
                child.localScale = normalizedScale;
                changed++;
            }
            MeshFilter filter = child.GetComponent<MeshFilter>();
            if (filter.sharedMesh != skin.Mesh)
            {
                Undo.RecordObject(filter, "Assign core mesh");
                filter.sharedMesh = skin.Mesh;
                changed++;
            }
            MeshRenderer renderer = child.GetComponent<MeshRenderer>();
            if (renderer.sharedMaterials.Length != 1 || renderer.sharedMaterial != skin.Material
                || renderer.enabled != shown)
            {
                Undo.RecordObject(renderer, "Assign core material");
                renderer.sharedMaterials = new[] { skin.Material };
                renderer.enabled = shown;
                changed++;
            }
            if (!child.gameObject.activeSelf)
            {
                Undo.RecordObject(child.gameObject, "Show core skin");
                child.gameObject.SetActive(true);
                changed++;
            }
            touched.Add(child);
            touched.Add(child.gameObject);
            touched.Add(filter);
            touched.Add(renderer);
            return renderer;
        }

        // BallView's skin table; true when it changed.
        private static bool WriteViewTable(BallView view, int[] numbers, MeshRenderer[] renderers,
            List<string> notes)
        {
            var so = new SerializedObject(view);
            SerializedProperty numberProp = so.FindProperty("skinNumbers");
            SerializedProperty rendererProp = so.FindProperty("skinRenderers");
            if (numberProp == null || rendererProp == null)
                throw new InvalidOperationException("BallView has no skinNumbers/skinRenderers (renamed?)");
            bool same = numberProp.arraySize == numbers.Length && rendererProp.arraySize == renderers.Length;
            for (int i = 0; same && i < numbers.Length; i++)
            {
                same = numberProp.GetArrayElementAtIndex(i).intValue == numbers[i]
                    && rendererProp.GetArrayElementAtIndex(i).objectReferenceValue == renderers[i];
            }
            if (same) return false;
            numberProp.arraySize = numbers.Length;
            rendererProp.arraySize = renderers.Length;
            for (int i = 0; i < numbers.Length; i++)
            {
                numberProp.GetArrayElementAtIndex(i).intValue = numbers[i];
                rendererProp.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
            }
            so.ApplyModifiedProperties();
            RecordPrefabOverride(view);
            CopyToUdon(view, notes);
            return true;
        }

        /// <summary>
        /// Points the scene's one <see cref="HubSettingsHost"/> at <paramref name="view"/> so the
        /// ball skin setting switches it. No host (or more than one) leaves things as they are.
        /// Public so assemblers that add the host after the skin pass can call it again.
        /// </summary>
        public static bool WireSettingsHost(Scene scene, BallView view, List<string> notes)
        {
            if (view == null || !scene.IsValid() || !scene.isLoaded) return false;
            var hosts = new List<HubSettingsHost>();
            foreach (GameObject root in scene.GetRootGameObjects())
                hosts.AddRange(root.GetComponentsInChildren<HubSettingsHost>(true));
            if (hosts.Count != 1) return false;
            var so = new SerializedObject(hosts[0]);
            SerializedProperty field = so.FindProperty("ballView");
            if (field == null || field.objectReferenceValue == view) return false;
            field.objectReferenceValue = view;
            so.ApplyModifiedProperties();
            RecordPrefabOverride(hosts[0]);
            CopyToUdon(hosts[0], notes);
            return true;
        }

        /// <summary>The scene's single BallView, or null.</summary>
        public static BallView FindBallView(Scene scene)
        {
            BallView found = null;
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (BallView view in root.GetComponentsInChildren<BallView>(true))
            {
                if (found != null) return null;
                found = view;
            }
            return found;
        }

        // The backing UdonBehaviour carries the runtime values; a proxy-only write is lost.
        // A stale program asset (new fields not compiled yet) makes the copy throw: that is a
        // warning to recompile and re-run, not a reason to roll the skins back.
        private static void CopyToUdon(UdonSharp.UdonSharpBehaviour proxy, List<string> notes)
        {
            try
            {
                if (UdonSharpEditor.UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null)
                    UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(proxy);
            }
            catch (Exception exception)
            {
                notes.Add("CopyProxyToUdon(" + proxy.GetType().Name + ") threw: " + exception.Message
                    + "; recompile U# and run again");
            }
        }

        // Null when the core's OriginalCoreSkin is ours (container or the old single-skin form).
        private static string ContainerProblem(Transform core)
        {
            int named = 0;
            for (int i = 0; i < core.childCount; i++)
                if (core.GetChild(i).name == SkinName) named++;
            if (named > 1) return "ambiguous OriginalCoreSkin children";
            Transform container = core.Find(SkinName);
            if (container == null) return null;
            foreach (Component component in container.GetComponents<Component>())
            {
                if (component is Transform || component is MeshFilter || component is MeshRenderer) continue;
                return "custom component on OriginalCoreSkin";
            }
            for (int i = 0; i < container.childCount; i++)
            {
                Transform child = container.GetChild(i);
                int number;
                if (!TryParseSkinChild(child.name, out number) || child.childCount != 0
                    || child.GetComponents<Component>().Length != 3
                    || child.GetComponent<MeshFilter>() == null || child.GetComponent<MeshRenderer>() == null)
                    return "custom child '" + child.name + "' under OriginalCoreSkin";
            }
            return null;
        }

        /// <summary>"core_07" → 7; false for anything that is not an original core name.</summary>
        public static bool TryParseSkinChild(string name, out int number)
        {
            number = 0;
            if (name == null || name.Length != 7 || !name.StartsWith("core_", StringComparison.Ordinal)) return false;
            if (!int.TryParse(name.Substring(5), out number)) return false;
            return BallSkinMath.IsOriginalNumber(number) && SkinChildName(number) == name;
        }

        private static int[] Numbers(List<SkinSource> skins)
        {
            var numbers = new int[skins.Count];
            for (int i = 0; i < skins.Count; i++) numbers[i] = skins[i].Number;
            return numbers;
        }

        private static int SharedMeshCount(List<SkinSource> skins)
        {
            var distinct = new HashSet<Mesh>();
            for (int i = 0; i < skins.Count; i++) distinct.Add(skins[i].Mesh);
            return skins.Count - distinct.Count;
        }

        private static string Join(List<string> items) { return items.Count == 0 ? "none" : string.Join("; ", items.ToArray()); }

        /// <summary>Same vertices, UVs, normals and triangles: safe to draw both skins from one Mesh.</summary>
        public static bool SameGeometry(Mesh a, Mesh b)
        {
            if (a == b) return true;
            if (a == null || b == null || a.vertexCount != b.vertexCount || a.subMeshCount != b.subMeshCount) return false;
            return SameArray(a.vertices, b.vertices) && SameArray(a.uv, b.uv)
                && SameArray(a.normals, b.normals) && SameArray(a.triangles, b.triangles);
        }

        private static bool SameArray<T>(T[] a, T[] b)
        {
            if (a.Length != b.Length) return false;
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < a.Length; i++) if (!comparer.Equals(a[i], b[i])) return false;
            return true;
        }

        private static void RecordPrefabOverride(UnityEngine.Object target)
        {
            if (target != null && PrefabUtility.IsPartOfPrefabInstance(target))
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        /// <summary>
        /// Pure closed, consistently wound triangle-mesh volume: abs(sum(det))/6, then
        /// D = cbrt(6V/pi). Do not sum absolute tetrahedra (wrong on concave surfaces).
        /// Source units cancel in child scale 1/D; this is not the AABB diameter.
        /// </summary>
        public static bool TryNormalization(Vector3[] vertices, int[] triangles,
            out double diameter, out float scale)
        {
            diameter = 0;
            scale = 0;
            if (vertices == null || vertices.Length < 4 || triangles == null
                || triangles.Length < 12 || triangles.Length % 3 != 0) return false;
            foreach (Vector3 v in vertices)
                if (!Finite(v.x) || !Finite(v.y) || !Finite(v.z)) return false;
            Vector3 origin = vertices[0];
            double sixVolume = 0;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
                if (ia < 0 || ib < 0 || ic < 0 || ia >= vertices.Length
                    || ib >= vertices.Length || ic >= vertices.Length) return false;
                Vector3 a = vertices[ia], b = vertices[ib], c = vertices[ic];
                double ax = (double)a.x - origin.x, ay = (double)a.y - origin.y, az = (double)a.z - origin.z;
                double bx = (double)b.x - origin.x, by = (double)b.y - origin.y, bz = (double)b.z - origin.z;
                double cx = (double)c.x - origin.x, cy = (double)c.y - origin.y, cz = (double)c.z - origin.z;
                sixVolume += ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);
            }
            double d = Math.Pow(Math.Abs(sixVolume) / Math.PI, 1.0 / 3.0);
            float inverse = (float)(1.0 / d);
            if (double.IsNaN(d) || double.IsInfinity(d) || d <= 0 || !Finite(inverse) || inverse <= 0)
                return false;
            diameter = d;
            scale = inverse;
            return true;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
