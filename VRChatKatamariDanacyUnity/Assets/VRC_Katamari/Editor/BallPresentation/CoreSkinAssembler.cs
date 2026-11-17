using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRCKatamari.BallPresentation;

namespace VRCKatamari.Editor.BallPresentation
{
    /// <summary>Optional local reference skin. Never imports, copies or generates reference assets.</summary>
    public static class CoreSkinAssembler
    {
        public const string MenuPath = "VRC Katamari/Ball/Assemble Core Skin";
        public const string ModelPath = "Assets/_RefUnlicensed/Models/KatamariCore/core_01.obj";
        public const string TexturePath = "Assets/_RefUnlicensed/Models/KatamariCore/core_01.png";
        public const string SkinName = "OriginalCoreSkin";

        [MenuItem(MenuPath, false, 415)]
        public static void Assemble() { Debug.Log(AssembleOpenScene()); }

        /// <summary>Only the active scene is eligible. The operator saves the scene after review.</summary>
        public static string AssembleOpenScene()
        {
            const string prefix = "Core skin: +0 ~0. Note: ";
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling
                || EditorApplication.isUpdating || PrefabStageUtility.GetCurrentPrefabStage() != null)
                return prefix + "stopped: leave Play/Prefab Mode and wait for import/compile.";
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return prefix + "no loaded active scene.";

            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(TexturePath);
            if (source == null || texture == null)
                return prefix + "optional core_01 model/texture missing; existing core left untouched.";
            // Validate everything before starting the Undo transaction. No importer changes.
            MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length != 1) return prefix + "expected one imported core mesh; left untouched.";
            Mesh mesh = filters[0].sharedMesh;
            MeshRenderer sourceRenderer = filters[0].GetComponent<MeshRenderer>();
            Material material = sourceRenderer != null ? sourceRenderer.sharedMaterial : null;
            if (mesh == null || !mesh.isReadable || mesh.subMeshCount != 1
                || mesh.GetTopology(0) != MeshTopology.Triangles)
                return prefix + "core needs one readable triangle submesh (Read/Write on, Keep Quads off).";
            if (material == null || material.mainTexture != texture)
                return prefix + "imported material must reference core_01.png; left untouched.";
            double diameter;
            float scale;
            if (!TryNormalization(mesh.vertices, mesh.triangles, out diameter, out scale))
                return prefix + "invalid or zero-volume core mesh; left untouched.";

            var cores = new HashSet<Transform>();
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
                int named = 0;
                for (int i = 0; i < core.childCount; i++)
                    if (core.GetChild(i).name == SkinName) named++;
                Transform skin = core.Find(SkinName);
                if (named > 1 || (skin != null && (skin.childCount != 0
                    || skin.GetComponent<MeshFilter>() == null || skin.GetComponent<MeshRenderer>() == null
                    || skin.GetComponents<Component>().Length != 3)))
                    return prefix + "ambiguous/custom OriginalCoreSkin child; left untouched.";
                if (core.GetComponent<MeshRenderer>() == null)
                    return prefix + "core needs its existing fallback MeshRenderer; left untouched.";
                cores.Add(core);
            }
            if (cores.Count == 0) return prefix + "no BallView in active scene.";
            if (scene.isDirty) return prefix + "save/review the active scene before assembly.";

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Assemble core skin");
            int created = 0;
            int changed = 0;
            try
            {
                foreach (Transform core in cores)
                    ApplyToCore(core, mesh, material, scale, ref created, ref changed);
                Undo.CollapseUndoOperations(group);
                if (created + changed > 0) EditorSceneManager.MarkSceneDirty(scene);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(group);
                return prefix + "rolled back: " + exception.Message;
            }
            return "Core skin: +" + created + " ~" + changed + ". " + cores.Count
                + " core(s); mesh equivalent diameter=" + diameter.ToString("G9")
                + ", normalization=" + scale.ToString("G9")
                + ". Note: displayed equivalent diameter = motor start diameter (Cancer 0.222 m);"
                + " bumpy bounds are larger. Reference-only: do not commit or upload the assembled scene.";
        }

        // Keep the runtime-owned CoreMesh as the diameter transform. Only its display child
        // is normalized; imported root scale/axis wrappers are deliberately not instantiated.
        public static void ApplyToCore(Transform core, Mesh mesh, Material material, float scale,
            ref int created, ref int changed)
        {
            int before = created + changed;
            Transform skin = core.Find(SkinName);
            if (skin == null)
            {
                var go = new GameObject(SkinName);
                Undo.RegisterCreatedObjectUndo(go, "Create core skin");
                go.layer = core.gameObject.layer;
                go.transform.SetParent(core, false);
                skin = go.transform;
                Undo.AddComponent<MeshFilter>(go);
                Undo.AddComponent<MeshRenderer>(go);
                created++;
            }
            Vector3 position = -mesh.bounds.center * scale;
            Vector3 normalizedScale = Vector3.one * scale;
            if ((skin.localPosition - position).sqrMagnitude > 1e-12f
                || skin.localRotation != Quaternion.identity || skin.localScale != normalizedScale)
            {
                Undo.RecordObject(skin, "Normalize core skin");
                skin.localPosition = position;
                skin.localRotation = Quaternion.identity;
                skin.localScale = normalizedScale;
                changed++;
            }
            MeshFilter filter = skin.GetComponent<MeshFilter>();
            if (filter.sharedMesh != mesh)
            {
                Undo.RecordObject(filter, "Assign core mesh");
                filter.sharedMesh = mesh;
                changed++;
            }
            MeshRenderer renderer = skin.GetComponent<MeshRenderer>();
            if (renderer.sharedMaterials.Length != 1 || renderer.sharedMaterial != material || !renderer.enabled)
            {
                Undo.RecordObject(renderer, "Assign core material");
                renderer.sharedMaterials = new[] { material };
                renderer.enabled = true;
                changed++;
            }
            if (!skin.gameObject.activeSelf)
            {
                Undo.RecordObject(skin.gameObject, "Show core skin");
                skin.gameObject.SetActive(true);
                changed++;
            }
            MeshRenderer fallback = core.GetComponent<MeshRenderer>();
            if (fallback.enabled)
            {
                Undo.RecordObject(fallback, "Hide fallback core");
                fallback.enabled = false;
                changed++;
            }
            if (created + changed != before)
            {
                RecordPrefabOverride(skin);
                RecordPrefabOverride(skin.gameObject);
                RecordPrefabOverride(filter);
                RecordPrefabOverride(renderer);
                RecordPrefabOverride(fallback);
            }
        }

        private static void RecordPrefabOverride(UnityEngine.Object target)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(target))
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
