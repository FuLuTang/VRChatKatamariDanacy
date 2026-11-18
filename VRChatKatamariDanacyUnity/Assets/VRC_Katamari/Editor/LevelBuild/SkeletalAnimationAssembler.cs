using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    /// (def to type, clip seconds) and <c>type_N/type_N.fbx</c> (armature, skinned meshes, one take
    /// per clip: walk, walk_fast, slot2, idle, slot4). Nothing here is ever written outside
    /// <c>_RefUnlicensed</c>, and the menu refuses any other root.
    /// </para>
    /// <para>
    /// Per type: the FBX is imported Generic with every take looping, and one
    /// <c>AnimatorController</c> is created (parameters <c>Moving</c>, <c>Panic</c>; states
    /// <c>idle</c> default, <c>walk</c>, <c>walk_fast</c>). Per prop whose def is a skinned
    /// type: a child <c>Skin</c> (the FBX instance) carrying the Animator, aligned to the static
    /// model by mesh-bounds centre, the static renderers switched off (kept in place; the pickup sync
    /// measures only them and skips the <c>Skin</c> subtree). <see cref="MotionHost"/> then starts each
    /// Animator at its phase, sets <c>Moving</c>/<c>Panic</c> and disables the component when the prop
    /// is culled. Each skin's Animator is set to <c>CullUpdateTransforms</c> and each
    /// <c>SkinnedMeshRenderer</c>'s <c>skinnedMotionVectors</c> to false (see
    /// <see cref="AssemblePropSkin"/>): an offscreen crab stops writing bone transforms, and
    /// the renderer no longer keeps a motion-vector variant for a world with no post-processing.
    /// </para>
    /// <para>
    /// Source tracking: each type's stamp (SHA-256 of the FBX bytes and of <c>skinned_defs.json</c>) is
    /// kept in the FBX importer's <c>userData</c>. A stamp that differs from the files on disk means the
    /// source changed: the clip settings are re-read from the file (take ranges included), the
    /// controller's states are re-wired to the re-imported clips, and every existing <c>Skin</c> of the
    /// type is rebuilt. A <c>Skin</c> is also rebuilt when its mesh is gone, it instances another asset,
    /// or it has drifted off the static model's bounds centre. A rebuild first checks the <c>Skin</c>
    /// subtree for a <c>MonoBehaviour</c> (an <c>UdonBehaviour</c> included); if one exists nothing is
    /// destroyed and the run reports a problem. After the re-import every clip's length is compared with
    /// the seconds in <c>skinned_defs.json</c> (<c>[anim_clip]</c>). A skeletal FBX instance anywhere
    /// under a prop other than <c>Skin</c> (<c>[anim_stray]</c>) is reported as a problem and never
    /// deleted: its renderers widen the prop's measured bounds.
    /// </para>
    /// <para>
    /// Which clip is "walk" is the exporter's naming (slot0, 0.967 s, +-15 degree leg swing), the
    /// research's strongest reading, not a proven fact (ORIGINAL_SKELETAL_ANIMATION 9.2); when to switch
    /// idle/walk/panic is our behaviour model, PROVISIONAL. Idempotent: with unchanged sources a second
    /// run prints <c>+0 ~0 -0</c>. The skin/static fit is logged as <c>[anim_fit]</c> for every skin on
    /// every run.
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
        /// <summary>A skin whose bounds centre is further than this (metres) from the static model's is realigned.</summary>
        public const float AlignTolerance = 0.0005f;
        /// <summary>A clip whose length differs from <c>skinned_defs.json</c> by more than this (seconds) is reported.</summary>
        public const float ClipLengthTolerance = 0.002f;

        public sealed class Result
        {
            public int Created;
            public int Updated;
            public int Removed;
            public int Skipped;
            public int Problems;
            public readonly List<string> Lines = new List<string>();
            public string Headline()
            {
                return "[anim] +" + Created + " ~" + Updated + " -" + Removed + " skip=" + Skipped + " problems=" + Problems;
            }
        }

        /// <summary>What one skeletal type needs across the run: its FBX, its source stamp and its controller.</summary>
        private sealed class TypeSource
        {
            public string Type;
            public string Fbx;
            public string Stamp;
            public bool SourceChanged;
            public bool RebuildBlocked;
            public AnimatorController Controller;
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

            byte[] defsBytes = File.ReadAllBytes(defsPath);
            Dictionary<string, Dictionary<string, float>> secondsOfType;
            Dictionary<string, string> typeOfDef = ReadDefs(Encoding.UTF8.GetString(defsBytes), defsPath, result, out secondsOfType);
            var sources = new Dictionary<string, TypeSource>(StringComparer.Ordinal);
            foreach (string type in new HashSet<string>(typeOfDef.Values))
            {
                string fbx = AnimationRoot + "/" + type + "/" + type + ".fbx";
                if (!File.Exists(fbx))
                {
                    result.Problems++;
                    result.Lines.Add("missing " + fbx);
                    continue;
                }
                var source = new TypeSource();
                source.Type = type;
                source.Fbx = fbx;
                source.Stamp = SourceStamp(File.ReadAllBytes(fbx), defsBytes);
                source.SourceChanged = ConfigureImporter(fbx, source.Stamp, result);
                source.Controller = EnsureController(type, fbx, result);
                Dictionary<string, float> seconds;
                if (secondsOfType.TryGetValue(type, out seconds)) CheckClipLengths(type, fbx, seconds, result);
                if (source.Controller != null) sources[type] = source;
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
                if (t == null || t.name == SkinChildName || t.GetComponent<Renderer>() == null) continue;
                string def = DefOfItem(t.name);
                string type;
                TypeSource source;
                if (!typeOfDef.TryGetValue(def, out type) || !sources.TryGetValue(type, out source)) continue;
                AssemblePropSkin(t, source, result);
            }
            foreach (TypeSource source in sources.Values)
            {
                if (source.RebuildBlocked) ForgetStamp(source, result);
            }
            if (result.Created > 0 || result.Removed > 0 || result.Updated > 0) EditorUtility.SetDirty(content);
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

        /// <summary>
        /// The source stamp of one type: the first 8 bytes of the SHA-256 of the FBX and of
        /// <c>skinned_defs.json</c>, as hex. Equal stamps mean the same files were assembled.
        /// </summary>
        public static string SourceStamp(byte[] fbxBytes, byte[] defsBytes)
        {
            return "fbx=" + Hash16(fbxBytes) + ";defs=" + Hash16(defsBytes);
        }

        private static string Hash16(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes ?? new byte[0]);
                var text = new StringBuilder(16);
                for (int i = 0; i < 8; i++) text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>True when a clip's imported length is within <see cref="ClipLengthTolerance"/> of the source's seconds.</summary>
        public static bool ClipLengthMatches(float actualSeconds, float expectedSeconds)
        {
            return Mathf.Abs(actualSeconds - expectedSeconds) <= ClipLengthTolerance;
        }

        /// <summary>True for an object name of the exporter's FBX root, <c>type_</c> followed by digits.</summary>
        public static bool IsSkeletalInstanceName(string objectName)
        {
            const string prefix = "type_";
            if (string.IsNullOrEmpty(objectName) || objectName.Length <= prefix.Length ||
                !objectName.StartsWith(prefix, StringComparison.Ordinal)) return false;
            for (int i = prefix.Length; i < objectName.Length; i++)
            {
                if (objectName[i] < '0' || objectName[i] > '9') return false;
            }
            return true;
        }

        /// <summary>True when an asset path lies under <see cref="AnimationRoot"/>.</summary>
        public static bool IsUnderAnimationRoot(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) &&
                   assetPath.StartsWith(AnimationRoot + "/", StringComparison.Ordinal);
        }

        private static Dictionary<string, string> ReadDefs(string text, string path, Result result,
                                                           out Dictionary<string, Dictionary<string, float>> secondsOfType)
        {
            // skinned_defs.json is {"defs": {def: type}, "types": [{type, clips:[{name, seconds}]}]};
            // JsonUtility cannot read a dictionary, so the small object is parsed with the project's own reader.
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            secondsOfType = new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal);
            LevelJsonNode root = LevelJsonParser.ParseText(text);
            LevelJsonNode defs = root == null ? null : root.Get("defs");
            if (defs == null)
            {
                result.Problems++;
                result.Lines.Add(path + " has no defs object");
                return map;
            }
            string[] keys = defs.KeyArray();
            for (int i = 0; i < keys.Length; i++) map[keys[i]] = defs.GetString(keys[i]);

            LevelJsonNode types = root.Get("types");
            if (types != null && types.IsArray)
            {
                for (int i = 0; i < types.Count; i++)
                {
                    LevelJsonNode entry = types.At(i);
                    string type = entry == null ? "" : entry.GetString("type", "");
                    LevelJsonNode clips = entry == null ? null : entry.Get("clips");
                    if (string.IsNullOrEmpty(type) || clips == null || !clips.IsArray) continue;
                    var seconds = new Dictionary<string, float>(StringComparer.Ordinal);
                    for (int c = 0; c < clips.Count; c++)
                    {
                        LevelJsonNode clip = clips.At(c);
                        string name = clip == null ? "" : clip.GetString("name", "");
                        if (name.Length > 0) seconds[name] = clip.GetFloat("seconds", 0f);
                    }
                    secondsOfType[type] = seconds;
                }
            }
            return map;
        }

        /// <summary>
        /// Writes the import settings; true when the source changed since the last run (the stored
        /// stamp differs from <paramref name="stamp"/>), in which case the clip ranges are taken from
        /// the file again instead of the ones kept in the importer.
        /// </summary>
        private static bool ConfigureImporter(string fbx, string stamp, Result result)
        {
            var importer = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (importer == null)
            {
                result.Problems++;
                result.Lines.Add(fbx + " is not importable as a model");
                return false;
            }

            bool sourceChanged = !string.Equals(importer.userData, stamp, StringComparison.Ordinal);
            if (sourceChanged)
            {
                AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceUpdate);
                importer = AssetImporter.GetAtPath(fbx) as ModelImporter;
            }

            ModelImporterClipAnimation[] clips = importer.clipAnimations;
            if (sourceChanged || clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;
            bool needs = sourceChanged || importer.animationType != ModelImporterAnimationType.Generic ||
                         !importer.importAnimation || importer.clipAnimations == null ||
                         importer.clipAnimations.Length != clips.Length;
            for (int i = 0; i < clips.Length && !needs; i++) needs = !clips[i].loopTime;
            if (!needs) { result.Skipped++; return false; }
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.importAnimation = true;
            for (int i = 0; i < clips.Length; i++)
            {
                clips[i].loopTime = true;
                int bar = clips[i].name.LastIndexOf('|');
                if (bar >= 0) clips[i].name = clips[i].name.Substring(bar + 1);
            }
            importer.clipAnimations = clips;
            importer.userData = stamp;
            importer.SaveAndReimport();
            result.Updated++;
            result.Lines.Add("import settings written: " + fbx + " (" + clips.Length + " looping takes" +
                             (sourceChanged ? ", source changed: clip ranges re-read from the file" : "") + ")");
            return sourceChanged;
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

        private static void CheckClipLengths(string type, string fbx, Dictionary<string, float> secondsOfClip, Result result)
        {
            var line = new StringBuilder("[anim_clip] " + type + ":");
            foreach (KeyValuePair<string, float> expected in secondsOfClip)
            {
                AnimationClip clip = FindClip(fbx, expected.Key);
                if (clip == null)
                {
                    result.Problems++;
                    line.Append(" " + expected.Key + " MISSING");
                    continue;
                }
                bool ok = ClipLengthMatches(clip.length, expected.Value);
                if (!ok) result.Problems++;
                line.Append(" " + expected.Key + " " + clip.length.ToString("0.0000") + "s/" +
                            expected.Value.ToString("0.0000") + "s" + (ok ? "" : " LENGTH DIFFERS FROM skinned_defs.json"));
            }
            result.Lines.Add(line.ToString());
        }

        private static AnimatorController EnsureController(string type, string fbx, Result result)
        {
            string path = AnimationRoot + "/" + type + ".controller";
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

            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (existing != null)
            {
                RewireStates(existing, idle, walk, panic, result);
                return existing;
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

        // An existing controller keeps its structure; each of its three states is pointed at the
        // clip the FBX has now (a re-imported FBX, or a controller built from the wrong take).
        private static void RewireStates(AnimatorController controller, AnimationClip idle, AnimationClip walk,
                                         AnimationClip panic, Result result)
        {
            bool changed = false;
            ChildAnimatorState[] states = controller.layers[0].stateMachine.states;
            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState state = states[i].state;
                AnimationClip wanted = state.name == IdleState ? idle
                                     : state.name == WalkState ? walk
                                     : state.name == PanicState ? panic : null;
                if (wanted == null || state.motion == wanted) continue;
                state.motion = wanted;
                changed = true;
                result.Lines.Add("controller " + controller.name + ": state " + state.name + " re-wired to " + wanted.name);
            }
            if (changed)
            {
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                result.Updated++;
            }
            else
            {
                result.Skipped++;
            }
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

        private static void AssemblePropSkin(Transform prop, TypeSource source, Result result)
        {
            Transform skin = prop.Find(SkinChildName);
            List<Transform> strays = FindStrays(prop, skin);
            for (int i = 0; i < strays.Count; i++)
            {
                result.Problems++;
                result.Lines.Add("[anim_stray] " + HierarchyPath(strays[i]) + ": a skeletal FBX instance outside " +
                                 SkinChildName + " (renderers " + CountEnabled(strays[i]) + "); its bounds widen the " +
                                 "prop's measured size; delete it by hand, it is not removed automatically");
            }

            if (skin != null)
            {
                string reason = source.SourceChanged ? "source files changed" : StaleReason(prop, skin, strays, source);
                if (reason != null)
                {
                    string blocker = FindScriptComponent(skin);
                    if (blocker != null)
                    {
                        result.Problems++;
                        result.Lines.Add(prop.name + ": " + SkinChildName + " needs a rebuild (" + reason + ") but " +
                                         blocker + " is attached under it; nothing was destroyed");
                        source.RebuildBlocked = true;
                        return;
                    }
                    Undo.DestroyObjectImmediate(skin.gameObject);
                    skin = null;
                    result.Removed++;
                }
            }

            bool created = skin == null;
            if (created)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(source.Fbx);
                if (prefab == null)
                {
                    result.Problems++;
                    result.Lines.Add(prop.name + ": " + source.Fbx + " did not load as a model");
                    return;
                }
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, prop);
                Undo.RegisterCreatedObjectUndo(instance, "Assemble skeletal animation");
                instance.name = SkinChildName;
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;
                skin = instance.transform;
                AlignToStatic(prop, skin, strays);
                result.Created++;
            }

            var animator = skin.GetComponent<Animator>();
            if (animator == null) animator = skin.gameObject.AddComponent<Animator>();
            if (animator.runtimeAnimatorController != source.Controller)
            {
                animator.runtimeAnimatorController = source.Controller;
                if (!created) result.Updated++;
            }
            else
            {
                result.Skipped++;
            }

            // Offscreen, the skin stops driving its bones. Measured on the assembled hub, all
            // 120 crab Animators were AlwaysAnimate: every frame, even 80 m away from the ball,
            // they retargeted and wrote their 5 bone transforms; their SkinnedMeshRenderers
            // also kept skinnedMotionVectors on, which makes each of them skin twice whenever a
            // camera asks for motion vectors (this world has no post-processing that does, so
            // the setting is simply not wanted here). CullUpdateTransforms still advances the
            // state machine and the clip time, so the pose is correct the frame the skin comes
            // back into view; CullCompletely would freeze the clip instead. Nothing at runtime
            // reads a bone: MotionHost drives the prop's own transform and enables/disables the
            // component, PickupFieldHost/PropFallHost read the prop root, and the editor-side
            // pickup sync measures the static model, not the pose.
            if (animator.cullingMode != AnimatorCullingMode.CullUpdateTransforms)
            {
                Undo.RecordObject(animator, "Assemble skeletal animation");
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                if (!created) result.Updated++;
            }
            SkinnedMeshRenderer[] skins = skin.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skins.Length; i++)
            {
                if (!skins[i].skinnedMotionVectors) continue;
                Undo.RecordObject(skins[i], "Assemble skeletal animation");
                skins[i].skinnedMotionVectors = false;
                if (!created) result.Updated++;
            }

            ReportFit(prop, skin, strays, result);

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

        // A blocked rebuild must be retried: the stored stamp is cleared so the next run sees the source as changed.
        private static void ForgetStamp(TypeSource source, Result result)
        {
            var importer = AssetImporter.GetAtPath(source.Fbx) as ModelImporter;
            if (importer == null) return;
            importer.userData = "";
            importer.SaveAndReimport();
            result.Lines.Add(source.Type + ": stamp cleared so the next run rebuilds the skins that were left");
        }

        // Why an existing skin must be rebuilt although the source stamp matches, or null when it is fine.
        private static string StaleReason(Transform prop, Transform skin, List<Transform> strays, TypeSource source)
        {
            SkinnedMeshRenderer[] meshes = skin.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (meshes.Length == 0) return "it has no skinned mesh";
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i].sharedMesh == null) return "its mesh reference is missing";
            }
            GameObject fbxSource = PrefabUtility.GetCorrespondingObjectFromSource(skin.gameObject);
            string sourcePath = fbxSource == null ? "" : AssetDatabase.GetAssetPath(fbxSource);
            if (!string.Equals(sourcePath, source.Fbx, StringComparison.Ordinal)) return "it instances '" + sourcePath + "', not " + source.Fbx;
            Bounds staticBounds, skinBounds;
            if (TryFitBounds(prop, skin, strays, out staticBounds, out skinBounds) &&
                (staticBounds.center - skinBounds.center).magnitude > AlignTolerance)
            {
                return "its bounds centre is " + (staticBounds.center - skinBounds.center).magnitude.ToString("0.0000") +
                       " m off the static model's";
            }
            return null;
        }

        // A script component (UdonBehaviour included) under the skin, described for the report, or null.
        private static string FindScriptComponent(Transform skin)
        {
            Component[] components = skin.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] is MonoBehaviour)
                {
                    return components[i].GetType().Name + " on " + HierarchyPath(components[i].transform);
                }
            }
            return null;
        }

        // Skeletal FBX instances under the prop that are not the Skin: named like the exporter's root, or an
        // instance of an asset under the animation root.
        private static List<Transform> FindStrays(Transform prop, Transform skin)
        {
            var strays = new List<Transform>();
            Transform[] all = prop.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == prop || (skin != null && t.IsChildOf(skin))) continue;
                bool stray = IsSkeletalInstanceName(t.name);
                if (!stray)
                {
                    GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
                    stray = source != null && IsUnderAnimationRoot(AssetDatabase.GetAssetPath(source));
                }
                if (!stray) continue;
                bool nested = false;
                for (int s = 0; s < strays.Count && !nested; s++) nested = t.IsChildOf(strays[s]);
                if (!nested) strays.Add(t);
            }
            return strays;
        }

        private static string CountEnabled(Transform root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            int enabled = 0;
            for (int i = 0; i < renderers.Length; i++) if (renderers[i].enabled) enabled++;
            return renderers.Length + ", enabled " + enabled;
        }

        private static string HierarchyPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        // The skin is moved so its bounds centre sits on the static model's.
        private static void AlignToStatic(Transform prop, Transform skin, List<Transform> strays)
        {
            Bounds staticBounds, skinBounds;
            if (!TryFitBounds(prop, skin, strays, out staticBounds, out skinBounds)) return;
            skin.position += staticBounds.center - skinBounds.center;
        }

        // The size ratio per axis is the orientation/scale check (a 90 degree error swaps two axes). Both
        // sides are measured the same way: the mesh's own bounds (bind pose for a skinned mesh) carried
        // through the renderer's transform. SkinnedMeshRenderer.bounds is not used: it covers every clip
        // pose in the root bone's tilted frame and is always larger than the model.
        private static void ReportFit(Transform prop, Transform skin, List<Transform> strays, Result result)
        {
            Bounds staticBounds, skinBounds;
            if (!TryFitBounds(prop, skin, strays, out staticBounds, out skinBounds))
            {
                result.Problems++;
                result.Lines.Add("[anim_fit] " + prop.name + ": no bounds to compare");
                return;
            }
            Vector3 ratio = SizeRatio(staticBounds.size, skinBounds.size);
            bool fits = FitsTolerance(ratio);
            if (!fits) result.Problems++;
            result.Lines.Add("[anim_fit] " + prop.name + ": skin/static size ratio (" + ratio.x.ToString("0.00") + ", " +
                             ratio.y.ToString("0.00") + ", " + ratio.z.ToString("0.00") + ")" + (fits ? "" : " OUT OF TOLERANCE: check FBX axes"));
        }

        private static bool TryFitBounds(Transform prop, Transform skin, List<Transform> strays,
                                         out Bounds staticBounds, out Bounds skinBounds)
        {
            var excluded = new List<Transform>(strays);
            excluded.Add(skin);
            bool haveStatic = TryBounds(prop, excluded, out staticBounds);
            bool haveSkin = TryBounds(skin, null, out skinBounds);
            return haveStatic && haveSkin;
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

        // Renderers under root, minus any subtree in excluded.
        private static bool TryBounds(Transform root, List<Transform> excluded, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (IsUnder(renderers[i].transform, excluded)) continue;
                Mesh mesh = SharedMeshOf(renderers[i]);
                if (mesh == null) continue;
                Bounds world = MeshBoundsInWorld(mesh.bounds, renderers[i].transform.localToWorldMatrix);
                if (!any) { bounds = world; any = true; }
                else bounds.Encapsulate(world);
            }
            return any;
        }

        private static bool IsUnder(Transform t, List<Transform> roots)
        {
            if (roots == null) return false;
            for (int i = 0; i < roots.Count; i++)
            {
                if (roots[i] != null && t.IsChildOf(roots[i])) return true;
            }
            return false;
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
