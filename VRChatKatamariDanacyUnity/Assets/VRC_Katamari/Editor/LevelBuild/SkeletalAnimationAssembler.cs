using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// Gives the reference level's skeletal props an ordinary <c>Animator</c> (owner D-11: one
    /// per instance, shared controller per type, no vertex-animation texture).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Input (all under <c>Assets/_RefUnlicensed/Animation/</c>, git-ignored, copied there by the
    /// operator from <c>~/KatamariRef/props/skinned_fbx</c>, which
    /// <c>Tools/levelbuild/blender/skinned_glb_to_fbx.py</c> writes): <c>skinned_defs.json</c>
    /// (def to type) and <c>type_N/type_N.fbx</c> (armature, skinned meshes, one take per clip:
    /// walk, walk_fast, slot2, idle, slot4). Nothing here is ever written outside
    /// <c>_RefUnlicensed</c>, and the menu refuses any other root.
    /// </para>
    /// <para>
    /// Per type: the FBX is imported Generic with every take looping, and one
    /// <c>AnimatorController</c> is created (parameters <c>Moving</c>, <c>Panic</c>; states
    /// <c>idle</c> default, <c>walk</c>, <c>walk_fast</c>). Per prop whose def is a skinned
    /// type: a child <c>Skin</c> (the FBX instance) carrying the Animator, aligned to the static
    /// model by mesh-bounds centre, the static renderers switched off (kept in place; the pickup sync
    /// measures only them and skips the <c>Skin</c> subtree). <see cref="MotionHost"/> then starts each Animator at its phase,
    /// sets <c>Moving</c>/<c>Panic</c> and disables the component when the prop is culled.
    /// </para>
    /// <para>
    /// Which clip is "walk" is the exporter's naming (slot0, 0.967 s, +-15 degree leg swing),
    /// the research's strongest reading, not a proven fact (ORIGINAL_SKELETAL_ANIMATION 9.2);
    /// when to switch idle/walk/panic is our behaviour model, PROVISIONAL. Idempotent: an existing
    /// controller, Skin child or matching import setting is left alone, so a second run prints
    /// <c>+0 ~0 -0</c>. The skin/static fit is logged as <c>[anim_fit]</c> because the FBX axis
    /// conversion could not be verified without Unity.
    /// </para>
    /// </remarks>
    public static class SkeletalAnimationAssembler
    {
        public const string MenuPath = "VRC Katamari/Level/Assemble Skeletal Animation (reference)";
        public const string AnimationRoot = "Assets/_RefUnlicensed/Animation";
        public const string SkinChildName = "Skin";
        public const string MovingParameter = "Moving";
        public const string PanicParameter = "Panic";
        public const string IdleState = "idle";
        public const string WalkState = "walk";
        public const string PanicState = "walk_fast";
        /// <summary>A skin whose size differs from the static model by more than this is reported.</summary>
        public const float FitTolerance = 0.25f;

        public sealed class Result
        {
            public int Created;
            public int Updated;
            public int Skipped;
            public int Problems;
            public readonly List<string> Lines = new List<string>();
            public string Headline()
            {
                return "[anim] +" + Created + " ~" + Updated + " -0 skip=" + Skipped + " problems=" + Problems;
            }
        }

        [MenuItem(MenuPath)]
        public static void Run()
        {
            Result result = Assemble();
            string text = result.Headline() + "\n  " + string.Join("\n  ", result.Lines.ToArray());
            if (result.Problems > 0) Debug.LogError(text);
            else Debug.Log(text);
        }

        public static Result Assemble()
        {
            var result = new Result();
            string defsPath = AnimationRoot + "/skinned_defs.json";
            if (!File.Exists(defsPath))
            {
                result.Problems++;
                result.Lines.Add("missing " + defsPath + " (copy ~/KatamariRef/props/skinned_fbx there; see the class remarks)");
                return result;
            }

            Dictionary<string, string> typeOfDef = ReadDefs(defsPath, result);
            var controllers = new Dictionary<string, AnimatorController>(StringComparer.Ordinal);
            var fbxOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string type in new HashSet<string>(typeOfDef.Values))
            {
                string fbx = AnimationRoot + "/" + type + "/" + type + ".fbx";
                if (!File.Exists(fbx))
                {
                    result.Problems++;
                    result.Lines.Add("missing " + fbx);
                    continue;
                }
                fbxOf[type] = fbx;
                ConfigureImporter(fbx, result);
                AnimatorController controller = EnsureController(type, fbx, result);
                if (controller != null) controllers[type] = controller;
            }

            GameObject content = GameObject.Find("LevelContent");
            if (content == null)
            {
                result.Problems++;
                result.Lines.Add("no LevelContent in the open scene; controllers were prepared, no props were touched");
                return result;
            }

            Transform[] all = content.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t.name == SkinChildName || t.GetComponent<Renderer>() == null) continue;
                string def = DefOfItem(t.name);
                string type;
                if (!typeOfDef.TryGetValue(def, out type) || !controllers.ContainsKey(type)) continue;
                AssemblePropSkin(t, fbxOf[type], controllers[type], result);
            }
            if (result.Created > 0) EditorUtility.SetDirty(content);
            result.Lines.Add("next: re-run the level import so MotionHost's rowAnimators column picks the new Animators up");
            return result;
        }

        /// <summary>The def id of an item name: the item id without its <c>_NNN</c> suffix and any <c>@size=</c> tag.</summary>
        public static string DefOfItem(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return "";
            string id = objectName;
            int at = id.IndexOf('@');
            if (at >= 0) id = id.Substring(0, at);
            int cut = id.LastIndexOf('_');
            if (cut <= 0 || cut == id.Length - 1) return id;
            for (int i = cut + 1; i < id.Length; i++)
            {
                if (id[i] < '0' || id[i] > '9') return id;
            }
            return id.Substring(0, cut);
        }

        private static Dictionary<string, string> ReadDefs(string path, Result result)
        {
            // skinned_defs.json is {"defs": {def: type}, "types": [...]}; JsonUtility cannot read
            // a dictionary, so the small object is parsed with the project's own reader.
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            LevelJsonNode root = LevelJsonParser.ParseText(File.ReadAllText(path));
            LevelJsonNode defs = root == null ? null : root.Get("defs");
            if (defs == null)
            {
                result.Problems++;
                result.Lines.Add(path + " has no defs object");
                return map;
            }
            string[] keys = defs.KeyArray();
            for (int i = 0; i < keys.Length; i++) map[keys[i]] = defs.GetString(keys[i]);
            return map;
        }

        private static void ConfigureImporter(string fbx, Result result)
        {
            var importer = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (importer == null)
            {
                result.Problems++;
                result.Lines.Add(fbx + " is not importable as a model");
                return;
            }
            ModelImporterClipAnimation[] clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;
            bool needs = importer.animationType != ModelImporterAnimationType.Generic ||
                         !importer.importAnimation || importer.clipAnimations == null ||
                         importer.clipAnimations.Length != clips.Length;
            for (int i = 0; i < clips.Length && !needs; i++) needs = !clips[i].loopTime;
            if (!needs) { result.Skipped++; return; }
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.importAnimation = true;
            for (int i = 0; i < clips.Length; i++)
            {
                clips[i].loopTime = true;
                int bar = clips[i].name.LastIndexOf('|');
                if (bar >= 0) clips[i].name = clips[i].name.Substring(bar + 1);
            }
            importer.clipAnimations = clips;
            importer.SaveAndReimport();
            result.Updated++;
            result.Lines.Add("import settings written: " + fbx + " (" + clips.Length + " looping takes)");
        }

        private static AnimationClip FindClip(string fbx, string name)
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(fbx);
            for (int i = 0; i < assets.Length; i++)
            {
                var clip = assets[i] as AnimationClip;
                if (clip == null || clip.name.StartsWith("__preview__", StringComparison.Ordinal)) continue;
                string clipName = clip.name;
                int bar = clipName.LastIndexOf('|');
                if (bar >= 0) clipName = clipName.Substring(bar + 1);
                if (string.Equals(clipName, name, StringComparison.Ordinal)) return clip;
            }
            return null;
        }

        private static AnimatorController EnsureController(string type, string fbx, Result result)
        {
            string path = AnimationRoot + "/" + type + ".controller";
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (existing != null) { result.Skipped++; return existing; }

            AnimationClip idle = FindClip(fbx, IdleState);
            AnimationClip walk = FindClip(fbx, WalkState);
            AnimationClip panic = FindClip(fbx, PanicState);
            if (idle == null || walk == null || panic == null)
            {
                result.Problems++;
                result.Lines.Add(fbx + " lacks a take named " + IdleState + "/" + WalkState + "/" + PanicState +
                                 " (takes found: " + ListClips(fbx) + ")");
                return null;
            }

            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter(MovingParameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter(PanicParameter, AnimatorControllerParameterType.Bool);
            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            AnimatorState idleState = machine.AddState(IdleState);
            idleState.motion = idle;
            AnimatorState walkState = machine.AddState(WalkState);
            walkState.motion = walk;
            AnimatorState panicState = machine.AddState(PanicState);
            panicState.motion = panic;
            machine.defaultState = idleState;

            AddTransition(idleState, walkState, MovingParameter, true);
            AddTransition(walkState, idleState, MovingParameter, false);
            AnimatorStateTransition toPanic = machine.AddAnyStateTransition(panicState);
            toPanic.hasExitTime = false;
            toPanic.duration = 0f;
            toPanic.canTransitionToSelf = false;
            toPanic.AddCondition(AnimatorConditionMode.If, 0f, PanicParameter);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            result.Created++;
            result.Lines.Add("controller created: " + path);
            return controller;
        }

        private static void AddTransition(AnimatorState from, AnimatorState to, string parameter, bool value)
        {
            AnimatorStateTransition transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.duration = 0.1f;
            transition.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, parameter);
        }

        private static string ListClips(string fbx)
        {
            var names = new List<string>();
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(fbx);
            for (int i = 0; i < assets.Length; i++)
            {
                var clip = assets[i] as AnimationClip;
                if (clip != null) names.Add(clip.name);
            }
            return string.Join(",", names.ToArray());
        }

        private static void AssemblePropSkin(Transform prop, string fbx, AnimatorController controller, Result result)
        {
            Transform skin = prop.Find(SkinChildName);
            if (skin == null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
                if (prefab == null)
                {
                    result.Problems++;
                    result.Lines.Add(prop.name + ": " + fbx + " did not load as a model");
                    return;
                }
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, prop);
                Undo.RegisterCreatedObjectUndo(instance, "Assemble skeletal animation");
                instance.name = SkinChildName;
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;
                skin = instance.transform;
                AlignToStatic(prop, skin, result);
                result.Created++;
            }

            var animator = skin.GetComponent<Animator>();
            if (animator == null) animator = skin.gameObject.AddComponent<Animator>();
            if (animator.runtimeAnimatorController != controller)
            {
                animator.runtimeAnimatorController = controller;
                result.Updated++;
            }
            else
            {
                result.Skipped++;
            }

            // The static visual stays (bounds, pickup sync) but is no longer drawn.
            Renderer[] renderers = prop.GetComponents<Renderer>();
            for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = false;
            Renderer[] children = prop.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].transform.IsChildOf(skin)) continue;
                children[i].enabled = false;
            }
        }

        // The skin is moved so its bounds centre sits on the static model's; the size ratio per axis
        // is the orientation/scale check (a 90 degree error swaps two axes). Both sides are measured
        // the same way: the mesh's own bounds (bind pose for a skinned mesh) carried through the
        // renderer's transform. SkinnedMeshRenderer.bounds is not used: it covers every clip pose in
        // the root bone's tilted frame and is always larger than the model.
        private static void AlignToStatic(Transform prop, Transform skin, Result result)
        {
            Bounds staticBounds;
            bool haveStatic = TryBounds(prop, skin, false, out staticBounds);
            Bounds skinBounds;
            bool haveSkin = TryBounds(skin, null, true, out skinBounds);
            if (!haveStatic || !haveSkin)
            {
                result.Problems++;
                result.Lines.Add("[anim_fit] " + prop.name + ": no bounds to align (static=" + haveStatic + " skin=" + haveSkin + ")");
                return;
            }
            skin.position += staticBounds.center - skinBounds.center;
            Vector3 ratio = SizeRatio(staticBounds.size, skinBounds.size);
            bool fits = FitsTolerance(ratio);
            if (!fits) result.Problems++;
            result.Lines.Add("[anim_fit] " + prop.name + ": skin/static size ratio (" + ratio.x.ToString("0.00") + ", " +
                             ratio.y.ToString("0.00") + ", " + ratio.z.ToString("0.00") + ")" + (fits ? "" : " OUT OF TOLERANCE: check FBX axes"));
        }

        /// <summary>Per-axis <c>measured / reference</c>; an axis whose reference is degenerate reads 0.</summary>
        public static Vector3 SizeRatio(Vector3 reference, Vector3 measured)
        {
            return new Vector3(
                reference.x > 1e-6f ? measured.x / reference.x : 0f,
                reference.y > 1e-6f ? measured.y / reference.y : 0f,
                reference.z > 1e-6f ? measured.z / reference.z : 0f);
        }

        /// <summary>True when every axis of the ratio is within <see cref="FitTolerance"/> of 1.</summary>
        public static bool FitsTolerance(Vector3 ratio)
        {
            return Mathf.Abs(ratio.x - 1f) <= FitTolerance && Mathf.Abs(ratio.y - 1f) <= FitTolerance &&
                   Mathf.Abs(ratio.z - 1f) <= FitTolerance;
        }

        /// <summary>The world-space AABB of a mesh-local box carried through <paramref name="localToWorld"/> (8 corners).</summary>
        public static Bounds MeshBoundsInWorld(Bounds meshBounds, Matrix4x4 localToWorld)
        {
            Vector3 c = meshBounds.center;
            Vector3 e = meshBounds.extents;
            Vector3 first = localToWorld.MultiplyPoint3x4(c + new Vector3(-e.x, -e.y, -e.z));
            var result = new Bounds(first, Vector3.zero);
            for (int i = 1; i < 8; i++)
            {
                var sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                result.Encapsulate(localToWorld.MultiplyPoint3x4(c + Vector3.Scale(e, sign)));
            }
            return result;
        }

        private static bool TryBounds(Transform root, Transform exclude, bool onlyUnder, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (!onlyUnder && exclude != null && renderers[i].transform.IsChildOf(exclude)) continue;
                Mesh mesh = SharedMeshOf(renderers[i]);
                if (mesh == null) continue;
                Bounds world = MeshBoundsInWorld(mesh.bounds, renderers[i].transform.localToWorldMatrix);
                if (!any) { bounds = world; any = true; }
                else bounds.Encapsulate(world);
            }
            return any;
        }

        private static Mesh SharedMeshOf(Renderer renderer)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null) return skinned.sharedMesh;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }
}
