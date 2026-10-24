using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Xunit;
using Xunit.Runners;

namespace BlitzCS.WinTestRunner;

/// <summary>
/// In-process xunit v2 runner (xunit.runner.utility AssemblyRunner).
///   --assembly &lt;dll&gt;   test assembly (repeatable; resolved relative to the current directory; dependencies via its .deps.json / folder)
///   --filter &lt;text&gt;    only tests whose display name contains the text (case-insensitive; repeatable = OR)
///   --threads &lt;n&gt;      max parallel collections (default 1; xunit still serialises tests inside one [Collection])
///   --verbose          also print passing tests' output
///   --list             discover and list tests without running
/// Exit code: 0 all passed, 1 failures or errors, 2 usage / nothing run.
/// </summary>
internal static class Program
{
    private static readonly object Console_ = new();

    private static int Main(string[] args)
    {
        var assemblies = new List<string>(); var filters = new List<string>(); var threads = 1; var verbose = false; var list = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--assembly": assemblies.Add(Path.GetFullPath(args[++i])); break;
                case "--filter": filters.Add(args[++i]); break;
                case "--threads": threads = int.Parse(args[++i]); break;
                case "--verbose": verbose = true; break;
                case "--list": list = true; break;
                default: Console.Error.WriteLine("unknown argument: " + args[i]); return 2;
            }
        }
        if (assemblies.Count == 0) { Console.Error.WriteLine("usage: WinTestRunner --assembly <test.dll> [--filter <text>] [--threads n] [--verbose] [--list]"); return 2; }

        Console.WriteLine($"WinTestRunner on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} / {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} / {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        int passed = 0, failed = 0, skipped = 0, errors = 0; var total = Stopwatch.StartNew();
        var failures = new List<string>();
        foreach (var path in assemblies)
        {
            if (!File.Exists(path)) { Console.Error.WriteLine("assembly not found: " + path); errors++; continue; }
            Directory.SetCurrentDirectory(Path.GetDirectoryName(path)!);
            InstallResolver(path);
            Console.WriteLine($"== {Path.GetFileName(path)}");
            using var runner = AssemblyRunner.WithoutAppDomain(path);
            var done = new ManualResetEventSlim(); var discovered = 0;
            runner.TestCaseFilter = tc => filters.Count == 0 || filters.Any(f => tc.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase));
            runner.OnDiscoveryComplete = info => { discovered = info.TestCasesToRun; Console.WriteLine($"discovered {info.TestCasesDiscovered}, selected {info.TestCasesToRun}"); };
            runner.OnErrorMessage = info => { lock (Console_) { Console.WriteLine($"ERROR [{info.MesssageType}] {info.ExceptionType}: {info.ExceptionMessage}\n{info.ExceptionStackTrace}"); } Interlocked.Increment(ref errors); };
            runner.OnTestPassed = info => { Interlocked.Increment(ref passed); lock (Console_) { Console.WriteLine($"PASS  {info.TestDisplayName} ({info.ExecutionTime * 1000:F0} ms)"); if (verbose && !string.IsNullOrWhiteSpace(info.Output)) Console.WriteLine(Indent(info.Output)); } };
            runner.OnTestSkipped = info => { Interlocked.Increment(ref skipped); lock (Console_) Console.WriteLine($"SKIP  {info.TestDisplayName} -- {info.SkipReason}"); };
            runner.OnTestFailed = info =>
            {
                Interlocked.Increment(ref failed);
                var text = $"FAIL  {info.TestDisplayName} ({info.ExecutionTime * 1000:F0} ms)\n{Indent(info.ExceptionType + ": " + info.ExceptionMessage)}\n{Indent(info.ExceptionStackTrace ?? "")}" +
                           (string.IsNullOrWhiteSpace(info.Output) ? "" : "\n  -- output --\n" + Indent(info.Output));
                lock (Console_) { Console.WriteLine(text); failures.Add(info.TestDisplayName); }
            };
            runner.OnExecutionComplete = _ => done.Set();
            if (list)
            {
                runner.OnDiscoveryComplete = info => { Console.WriteLine($"{info.TestCasesToRun} selected of {info.TestCasesDiscovered}"); done.Set(); };
            }
            runner.Start(new AssemblyRunnerStartOptions { Parallel = threads > 1, MaxParallelThreads = threads });
            if (!done.Wait(TimeSpan.FromMinutes(60))) { Console.WriteLine("ERROR: timed out after 60 minutes"); errors++; }
            _ = discovered;
        }
        Console.WriteLine();
        Console.WriteLine($"SUMMARY passed={passed} failed={failed} skipped={skipped} errors={errors} time={total.Elapsed.TotalSeconds:F1}s");
        if (failures.Count > 0) { Console.WriteLine("FAILED TESTS:"); foreach (var f in failures.Order()) Console.WriteLine("  " + f); }
        if (list) return 0;
        if (passed + failed + skipped == 0 && errors == 0) { Console.WriteLine("no tests were run"); return 2; }
        return failed > 0 || errors > 0 ? 1 : 0;
    }

    /// <summary>xunit's AssemblyRunner loads the test assembly and its dependencies by name through the default context: resolve them from the test assembly's
    /// folder, honouring its .deps.json (AssemblyDependencyResolver) with a plain same-folder fallback.</summary>
    private static void InstallResolver(string testAssembly)
    {
        var dir = Path.GetDirectoryName(testAssembly)!; var resolver = new AssemblyDependencyResolver(testAssembly);
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = resolver.ResolveAssemblyToPath(name);
            if (p is null) { var f = Path.Combine(dir, name.Name + ".dll"); if (File.Exists(f)) p = f; }
            return p is null ? null : ctx.LoadFromAssemblyPath(p);
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (asm, name) =>
        {
            var p = resolver.ResolveUnmanagedDllToPath(name);
            return p is null ? IntPtr.Zero : NativeLibrary.Load(p);
        };
    }

    private static string Indent(string s) => string.Join('\n', s.TrimEnd().Split('\n').Select(l => "    " + l.TrimEnd('\r')));
}
