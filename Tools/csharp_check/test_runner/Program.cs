// test_runner: hosts NUnit 3.5's NUnitTestAssemblyRunner around Unity's real, unmodified
// managed DLLs on .NET 8.  It is plumbing only:
//
//   * resolve UnityEngine.* / UnityEditor.* / VRC.* from the reference cache (never stubbed);
//   * run the tests of one assembly (optionally filtered) and write NUnit's own result XML;
//   * or, with --list, write the discovered test tree without running anything;
//   * while running, record every engine-native call that the .NET runtime refused
//     (SecurityException "ECall methods must be packaged into a system module") together
//     with the extern methods the failing method contains, as a JSON sidecar.
//
// What a result means (passed / failed / not runnable here / differs from Unity) is decided
// by run_tests.py from the XML and the sidecar; this program never judges a failure.
//
// Two builds of this one file exist (NUnitFlavor in the csproj):
//   3.5   (default)  NUnit 3.5.0, the version Unity ships: the faithful run.  An assertion
//                    failure ends the test, exactly like in Unity.
//   3.14             NUnit 3.14.0 with NUNIT_CONTINUE: --continue-after-failure keeps
//                    executing a test after a failed assertion and records every failure
//                    (NUnit 3.5 has no Assert.Multiple).  It is a diagnostic view: later
//                    assertions may fail only because an earlier one did.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using NUnit.Framework.Api;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

#if NUNIT_CONTINUE
// Makes every test body behave as if it were inside Assert.Multiple: NUnit 3.14 reads
// MultipleAssertLevel from the test's own execution context, and an IApplyToContext
// attribute is the one hook that runs on the test thread with that context.  The tests are
// not edited: the attribute is attached to the loaded test objects at run time.
internal sealed class ContinueAfterFailureAttribute : Attribute, IApplyToContext
{
    private static readonly PropertyInfo Level = typeof(TestExecutionContext).GetProperty(
        "MultipleAssertLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public void ApplyToContext(TestExecutionContext context)
    {
        Level.SetValue(context, 1);
    }
}

internal sealed class MethodWithContinue : IMethodInfo
{
    private readonly IMethodInfo inner;
    public MethodWithContinue(IMethodInfo inner) { this.inner = inner; }
    public ITypeInfo TypeInfo => inner.TypeInfo;
    public MethodInfo MethodInfo => inner.MethodInfo;
    public string Name => inner.Name;
    public bool IsAbstract => inner.IsAbstract;
    public bool IsPublic => inner.IsPublic;
    public bool IsStatic => inner.IsStatic;
    public bool ContainsGenericParameters => inner.ContainsGenericParameters;
    public bool IsGenericMethod => inner.IsGenericMethod;
    public bool IsGenericMethodDefinition => inner.IsGenericMethodDefinition;
    public ITypeInfo ReturnType => inner.ReturnType;
    public IParameterInfo[] GetParameters() => inner.GetParameters();
    public Type[] GetGenericArguments() => inner.GetGenericArguments();
    public IMethodInfo MakeGenericMethod(params Type[] typeArguments) =>
        new MethodWithContinue(inner.MakeGenericMethod(typeArguments));
    public object Invoke(object fixture, params object[] args) => inner.Invoke(fixture, args);
    public bool IsDefined<T>(bool inherit) where T : class => inner.IsDefined<T>(inherit);
    public T[] GetCustomAttributes<T>(bool inherit) where T : class
    {
        var list = new List<T>(inner.GetCustomAttributes<T>(inherit));
        if (typeof(T).IsAssignableFrom(typeof(ContinueAfterFailureAttribute)))
            list.Add((T)(object)new ContinueAfterFailureAttribute());
        return list.ToArray();
    }
}
#endif

internal static class Program
{
    private sealed class NativeRefusal
    {
        public string Test { get; set; }
        public string Message { get; set; }
        public string Thrower { get; set; }
        public List<string> Natives { get; set; }
    }

    private static readonly List<NativeRefusal> Refusals = new List<NativeRefusal>();
    private static readonly object RefusalLock = new object();

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(
            "usage: test_runner --bin DIR --refs DIR --assembly NAME --out FILE.xml " +
            "[--events FILE.json] [--filter REGEX]... [--list] [--continue-after-failure]");
        return 2;
    }

    private static int Main(string[] args)
    {
        string bin = null, refs = null, assemblyName = null, outFile = null, eventsFile = null;
        bool listOnly = false, continueAfterFailure = false;
        var filters = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bin": bin = Next(args, ref i); break;
                case "--refs": refs = Next(args, ref i); break;
                case "--assembly": assemblyName = Next(args, ref i); break;
                case "--out": outFile = Next(args, ref i); break;
                case "--events": eventsFile = Next(args, ref i); break;
                case "--filter": filters.Add(Next(args, ref i)); break;
                case "--list": listOnly = true; break;
                case "--continue-after-failure": continueAfterFailure = true; break;
                default: return Usage("unknown argument " + args[i]);
            }
        }
        if (bin == null || refs == null || assemblyName == null || outFile == null)
            return Usage("--bin, --refs, --assembly and --out are required");

        // A fixed culture: the host's LANG must not decide what a test formats or parses.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
        {
            if (e.Exception is SecurityException && e.Exception.Message.Contains("ECall"))
                RecordRefusal(e.Exception);
        };

        string[] searchDirs = { bin, refs };
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            // The .NET runtime supplies the BCL; Unity's mscorlib/netstandard/System.* facades
            // (4.8 API surface) must not shadow it.
            if (name.Name == "mscorlib" || name.Name == "netstandard" ||
                name.Name.StartsWith("System", StringComparison.Ordinal) ||
                name.Name.StartsWith("Microsoft.", StringComparison.Ordinal))
                return null;
            foreach (string dir in searchDirs)
            {
                string path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };

        Assembly assembly = Assembly.LoadFrom(Path.Combine(bin, assemblyName + ".dll"));
        // Unity has every project assembly loaded in the AppDomain, and some tests find our
        // editor-only types by scanning AppDomain.GetAssemblies(); load the sibling
        // assemblies the same way (only those compiled into --bin, never the reference cache).
        foreach (string dll in Directory.GetFiles(bin, "*.dll"))
        {
            string name = Path.GetFileNameWithoutExtension(dll);
            if (name != assemblyName) AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        }

        var runner = new NUnitTestAssemblyRunner(new DefaultTestAssemblyBuilder());
        runner.Load(assembly, new Dictionary<string, object>());

        if (continueAfterFailure)
        {
#if NUNIT_CONTINUE
            AttachContinue(runner.LoadedTest);
#else
            return Usage("--continue-after-failure needs the NUnit 3.14 build of the runner");
#endif
        }

        TestFilter filter = TestFilter.Empty;
        if (filters.Count > 0)
        {
            var xml = new StringBuilder("<filter><or>");
            foreach (string pattern in filters)
                xml.Append("<test re=\"1\">").Append(SecurityElement.Escape(pattern)).Append("</test>");
            xml.Append("</or></filter>");
            filter = TestFilter.FromXml(xml.ToString());
        }

        string document;
        if (listOnly)
        {
            document = runner.LoadedTest.ToXml(true).OuterXml;
        }
        else
        {
            ITestResult result = runner.Run(TestListener.NULL, filter);
            document = result.ToXml(true).OuterXml;
        }
        File.WriteAllText(outFile, document, new UTF8Encoding(false));
        if (eventsFile != null)
        {
            lock (RefusalLock)
            {
                var payload = new
                {
                    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    nativeRefusals = Refusals,
                };
                File.WriteAllText(eventsFile, JsonSerializer.Serialize(payload), new UTF8Encoding(false));
            }
        }
        return 0;
    }

#if NUNIT_CONTINUE
    private static void AttachContinue(ITest test)
    {
        if (test is TestMethod method && method.Method != null)
            method.Method = new MethodWithContinue(method.Method);
        foreach (ITest child in test.Tests) AttachContinue(child);
    }
#endif

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value");
        return args[++i];
    }

    // ---- engine-native refusals ------------------------------------------------------

    private static void RecordRefusal(Exception exception)
    {
        string test = "";
        try { test = TestContext.CurrentContext.Test.FullName ?? ""; } catch { }
        MethodBase thrower = exception.TargetSite;
        // When the runtime refuses the test method itself, the failing frame is NUnit's own
        // reflection call (System.*); the method that holds the extern calls is the test.
        if (thrower == null || (thrower.DeclaringType != null && thrower.DeclaringType.Namespace != null &&
            thrower.DeclaringType.Namespace.StartsWith("System", StringComparison.Ordinal)))
        {
            try
            {
                MethodInfo testMethod = TestExecutionContext.CurrentContext.CurrentTest.Method?.MethodInfo;
                if (testMethod != null) thrower = testMethod;
            }
            catch { }
        }
        var natives = new List<string>();
        try { if (thrower != null) natives = ExternCallees(thrower); } catch { }
        lock (RefusalLock)
        {
            Refusals.Add(new NativeRefusal
            {
                Test = test,
                Message = exception.Message,
                Thrower = thrower == null ? "" : Describe(thrower),
                Natives = natives,
            });
        }
    }

    private static string Describe(MethodBase method)
    {
        return (method.DeclaringType == null ? "" : method.DeclaringType.FullName + "::") + method.Name;
    }

    private static bool IsInternalCall(MethodBase method)
    {
        return (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0;
    }

    private static readonly Dictionary<short, OpCode> OpCodeTable = BuildOpCodeTable();

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<short, OpCode>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op) table[op.Value] = op;
        }
        return table;
    }

    /// <summary>
    /// The extern (InternalCall) methods a method is, or directly calls. The runtime refuses
    /// the whole failing method, so this is the honest "what needs the engine here" answer.
    /// </summary>
    private static List<string> ExternCallees(MethodBase method)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        CollectExterns(method, 3, found, new HashSet<MethodBase>());
        return found.ToList();
    }

    // The JIT can refuse a method because of an extern in a callee it inlines, so the scan
    // follows calls into non-BCL methods a few levels deep.
    private static void CollectExterns(MethodBase method, int depth, SortedSet<string> found, HashSet<MethodBase> seen)
    {
        if (!seen.Add(method)) return;
        if (IsInternalCall(method))
        {
            found.Add(Describe(method));
            return;
        }
        MethodBody body = method.GetMethodBody();
        byte[] il = body == null ? null : body.GetILAsByteArray();
        if (il == null) return;
        Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType
            ? method.DeclaringType.GetGenericArguments() : null;
        Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int position = 0;
        while (position < il.Length)
        {
            short code = il[position++];
            if (code == 0xFE) code = (short)(0xFE00 | il[position++]);
            if (!OpCodeTable.TryGetValue(code, out OpCode op)) break;
            int size;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: size = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: size = 1; break;
                case OperandType.InlineVar: size = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: size = 8; break;
                case OperandType.InlineSwitch:
                    size = 4 + 4 * BitConverter.ToInt32(il, position);
                    break;
                default: size = 4; break;
            }
            if (op.OperandType == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(il, position);
                try
                {
                    MethodBase callee = method.Module.ResolveMethod(token, typeArgs, methodArgs);
                    if (callee != null)
                    {
                        string assemblyName = callee.DeclaringType?.Assembly.GetName().Name ?? "";
                        bool bcl = assemblyName == "mscorlib" || assemblyName == "netstandard" ||
                            assemblyName.StartsWith("System", StringComparison.Ordinal);
                        if (IsInternalCall(callee) && !bcl) found.Add(Describe(callee));
                        else if (depth > 0 && !bcl && !IsInternalCall(callee))
                            CollectExterns(callee, depth - 1, found, seen);
                    }
                }
                catch (Exception) { }
            }
            position += size;
        }
    }
}
