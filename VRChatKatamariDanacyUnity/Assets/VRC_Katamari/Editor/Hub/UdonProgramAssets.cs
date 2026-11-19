using System;
using System.Collections.Generic;
using UdonSharp;
using UnityEditor;
using UnityEngine;

namespace VRCKatamari.Editor.Hub
{
    /// <summary>
    /// 「缺则建」U# 程序资产：给已存在的 U# 行为脚本补 <c>&lt;Name&gt;.asset</c>
    /// （<see cref="UdonSharpProgramAsset"/>，sourceCsScript 指向 .cs），再强制编译一次。
    /// </summary>
    /// <remarks>
    /// <para>
    /// U# 只在用它自己的「新建 U# 脚本」菜单时建程序资产；从仓库同步过来的 .cs 没有资产，
    /// <c>GetProgramAssetForClass</c> 返回 null，<c>UdonSharpUndo.AddComponent</c> 抛
    /// TargetInvocationException，即使建出组件，backing UdonBehaviour 的 programSource 也为空，
    /// 之后 <c>CopyProxyToUdon</c> 抛 "Value cannot be null"。做法与 U# 自己的
    /// UdonSharpBehaviourEditor 一致：CreateInstance + sourceCsScript + CreateAsset。
    /// </para>
    /// <para>
    /// ScriptVersion 在建资产前设为当前版本：否则导入后处理器会把它排进升级队列，升级器会
    /// 改写 .cs 源文件。新资产需要一次强制编译才有 serializedProgramAsset。
    /// </para>
    /// <para>
    /// 只处理 <see cref="OwnScriptRoot"/> 下的脚本（包里的脚本由包自带资产）。装配器添加 U# 组件时走
    /// <see cref="AddComponent{T}"/>；菜单一次补齐项目里全部缺失的资产。
    /// </para>
    /// </remarks>
    public static class UdonProgramAssets
    {
        public const string MenuPath = "VRC Katamari/Udon/Create Missing Program Assets";
        private const string Tag = "[udon-programs] ";

        /// <summary>只给我们自己的脚本建资产，不碰第三方或 SDK 示例。</summary>
        public const string OwnScriptRoot = "Assets/VRC_Katamari";

        [MenuItem(MenuPath, false, 420)]
        public static void CreateMissingMenu()
        {
            Debug.Log(CreateMissing());
        }

        /// <summary>扫描项目里全部 U# 行为类型，缺资产的补建，有新建时编译一次。返回报告。</summary>
        public static string CreateMissing()
        {
            var problems = new List<string>();
            var created = new List<string>();
            int scanned = EnsureAll(created, problems);
            string text = Tag + "scanned " + scanned + " U# behaviour(s) under " + OwnScriptRoot + "/, created " + created.Count
                + (created.Count > 0 ? " (" + string.Join(", ", created.ToArray()) + ")" : "")
                + ", problems=" + problems.Count;
            for (int i = 0; i < problems.Count; i++) text += "\n" + Tag + "PROBLEM: " + problems[i];
            return text;
        }

        /// <summary>
        /// 给 <see cref="OwnScriptRoot"/> 下每个缺程序资产的 U# 行为补建资产，有新建时强制编译一次。
        /// 新建的类型全名追加到 <paramref name="created"/>（排序），返回扫描到的 U# 行为数。
        /// 什么都不缺时不编译、不碰任何场景。
        /// </summary>
        public static int EnsureAll(List<string> created, List<string> problems)
        {
            int first = created.Count;
            int scanned = 0;
            string[] guids = AssetDatabase.FindAssets("t:MonoScript", new[] { OwnScriptRoot });
            for (int g = 0; g < guids.Length; g++)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guids[g]));
                Type type = script == null ? null : script.GetClass();
                if (type == null || type.IsAbstract || !type.IsSubclassOf(typeof(UdonSharpBehaviour))) continue;
                scanned++;
                if (HasProgramAsset(type)) continue;
                if (CreateAsset(type, script, problems)) created.Add(type.FullName);
            }
            if (created.Count == first) return scanned;
            Compile(problems);
            for (int i = first; i < created.Count; i++)
            {
                Type type = FindLoadedType(created[i]);
                if (type != null && !HasProgramAsset(type))
                    problems.Add(created[i] + ".asset was created but U# does not resolve it yet; let the"
                        + " compile finish and run again");
            }
            created.Sort(first, created.Count - first, StringComparer.Ordinal);
            return scanned;
        }

        /// <summary>
        /// 确保 <paramref name="type"/> 有程序资产；缺则建并编译。成功（已有或已建好）返回 true，
        /// 否则把原因写进 <paramref name="problems"/>。<paramref name="notes"/> 可为 null。
        /// </summary>
        public static bool Ensure(Type type, List<string> problems, List<string> notes)
        {
            if (HasProgramAsset(type)) return true;
            MonoScript script = FindScript(type);
            if (script == null)
            {
                problems.Add("no MonoScript found for " + type.Name + "; its U# program asset cannot be created");
                return false;
            }
            if (!CreateAsset(type, script, problems)) return false;
            Compile(problems);
            if (!HasProgramAsset(type))
            {
                problems.Add(type.Name + ".asset was created but U# does not resolve it yet; let the compile"
                    + " finish, save the scene and run the assembly again");
                return false;
            }
            if (notes != null) notes.Add("created U# program asset " + ProgramAssetPath(AssetDatabase.GetAssetPath(script)));
            return true;
        }

        /// <summary>
        /// <see cref="Ensure"/> 后用 <c>UdonSharpUndo.AddComponent</c> 加组件；失败返回 null 并记 problem。
        /// </summary>
        public static T AddComponent<T>(GameObject go, List<string> problems, List<string> notes)
            where T : UdonSharpBehaviour
        {
            if (!Ensure(typeof(T), problems, notes)) return null;
            try
            {
                return UdonSharpEditor.UdonSharpUndo.AddComponent<T>(go);
            }
            catch (Exception exception)
            {
                Exception inner = exception.InnerException ?? exception;
                problems.Add("could not add " + typeof(T).Name + " to '" + go.name + "': " + inner.Message);
                return null;
            }
        }

        /// <summary>脚本路径对应的程序资产路径：同目录同名，扩展名 .asset；不是 .cs 时返回 null。</summary>
        public static string ProgramAssetPath(string scriptPath)
        {
            if (string.IsNullOrEmpty(scriptPath)) return null;
            if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return null;
            return scriptPath.Substring(0, scriptPath.Length - 3) + ".asset";
        }

        // ------------------------------------------------------------------ internals ---

        private static bool HasProgramAsset(Type type)
        {
            try
            {
                return UdonSharpProgramAsset.GetProgramAssetForClass(type) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool CreateAsset(Type type, MonoScript script, List<string> problems)
        {
            string assetPath = ProgramAssetPath(AssetDatabase.GetAssetPath(script));
            if (assetPath == null)
            {
                problems.Add(type.Name + " is not defined in a .cs file under Assets/");
                return false;
            }
            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (existing != null)
            {
                var program = existing as UdonSharpProgramAsset;
                if (program == null)
                {
                    problems.Add(assetPath + " exists but is not a U# program asset; move it away and run again");
                    return false;
                }
                if (program.sourceCsScript != script)
                {
                    problems.Add(assetPath + " points at another script; fix its Source Script by hand");
                    return false;
                }
                return true; // 资产在，只是缓存或编译还没跟上：交给编译。
            }
            var asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            asset.sourceCsScript = script;
            asset.ScriptVersion = UdonSharpProgramVersion.CurrentVersion;
            try
            {
                AssetDatabase.CreateAsset(asset, assetPath);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception exception)
            {
                problems.Add("creating " + assetPath + " threw: " + exception.Message);
                return false;
            }
            return true;
        }

        private static void Compile(List<string> problems)
        {
            try
            {
                UdonSharpProgramAsset.CompileAllCsPrograms(true, false);
            }
            catch (Exception exception)
            {
                problems.Add("the UdonSharp compile after creating program assets threw: " + exception.Message);
            }
        }

        private static MonoScript FindScript(Type type)
        {
            string[] guids = AssetDatabase.FindAssets("t:MonoScript " + type.Name);
            for (int i = 0; i < guids.Length; i++)
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (script != null && script.GetClass() == type) return script;
            }
            return null;
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (Type type in TypeCache.GetTypesDerivedFrom<UdonSharpBehaviour>())
                if (type.FullName == fullName) return type;
            return null;
        }
    }
}
