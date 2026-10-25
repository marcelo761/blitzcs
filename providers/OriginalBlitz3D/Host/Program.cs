using System.Reflection;
using System.Runtime.Loader;
using BlitzCS.CLI;
using BlitzCS.OriginalRuntime;

try
{
    if (args.Length >= 2 && args[0] == "--log")
    {
        var log = new StreamWriter(Path.GetFullPath(args[1])) { AutoFlush = true };
        Console.SetOut(log); Console.SetError(log);
        args = args[2..];
    }
    Console.Error.WriteLine($"BlitzCS original-runtime host: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}.");
    if (args.Length > 0 && args[0] == "--run")
    {
        if (args.Length != 3 && !(args.Length == 5 && args[3] == "--capture")) throw new ArgumentException("--run <compiled.dll> <original-runtime.dll> [--capture <frame.bmp>]");
        var program = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var candidate = Path.Combine(Path.GetDirectoryName(program)!, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(program);
        Commands.Run(args[2], () =>
        {
            var entry = assembly.EntryPoint ?? throw new MissingMethodException("Compiled BASIC assembly has no entry point.");
            try
            {
                entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() });
                if (args.Length == 5) Commands.CaptureBitmap(args[4]);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
        });
        return 0;
    }
    return CliApplication.Run(args, Console.Out, Console.Error);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
