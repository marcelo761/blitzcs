using BlitzCS.BcRun;

// BcRun <exe> [--target windows-x64|linux-x64] [--base 0x...] [--allow-errors] [--env NAME=VALUE]... [--sandbox DIR] [--cache-dir DIR] [--no-guest-dlls] [--verbose] [--trace] -- <args...>
// stdout/stderr/stdin of the guest are BcRun's own; the exit code is the guest's (125 when the run itself failed).
string? exe = null, target = null, sandbox = null, cache = Environment.GetEnvironmentVariable("BCRUN_CACHE");
uint? baseAddress = null;
bool allowErrors = false, verbose = false, trace = false, guestDlls = true;
var env = new Dictionary<string, string>();
var guestArgs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (a == "--") { guestArgs.AddRange(args.Skip(i + 1)); break; }
    string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    switch (a)
    {
        case "--target": target = Value(); break;
        case "--base": baseAddress = Convert.ToUInt32(Value().Replace("0x", ""), 16); break;
        case "--allow-errors": allowErrors = true; break;
        case "--env": { var kv = Value(); var eq = kv.IndexOf('='); if (eq <= 0) throw new ArgumentException("--env NAME=VALUE"); env[kv[..eq]] = kv[(eq + 1)..]; break; }
        case "--sandbox": sandbox = Value(); break;
        case "--cache": case "--cache-dir": cache = Value(); break;
        case "--no-guest-dlls": guestDlls = false; break;
        case "--verbose": verbose = true; break;
        case "--trace": trace = true; break;
        default:
            if (exe is null && !a.StartsWith("--")) exe = a;
            else { Console.Error.WriteLine($"BcRun: unexpected argument '{a}'"); return BcRunner.RunnerFailure; }
            break;
    }
}
if (exe is null)
{
    Console.Error.WriteLine("usage: BcRun <exe> [--target windows-x64|linux-x64] [--base 0x..] [--allow-errors] [--env N=V] [--sandbox DIR] [--cache-dir DIR] [--no-guest-dlls] [--verbose] [--trace] -- <args...>");
    return BcRunner.RunnerFailure;
}
var result = BcRunner.Run(new BcRunOptions
{
    ExePath = exe, Args = guestArgs, TargetOs = target, Base = baseAddress, AllowTranslationErrors = allowErrors, Environment = env,
    SandboxRoot = sandbox, CacheDirectory = cache, TraceCalls = trace, GuestDlls = guestDlls,
    Log = verbose || trace ? m => Console.Error.WriteLine("BcRun: " + m) : null,
});
if (verbose) foreach (var d in result.Diagnostics) Console.Error.WriteLine("BcRun: loader: " + d);
if (result.Failure is { } why)
{
    Console.Error.WriteLine("BcRun: " + why);
    if (result.UnboundImports.Count > 0 && !verbose) Console.Error.WriteLine($"BcRun: {result.UnboundImports.Count} import(s) of the image are not provided (--verbose lists them).");
    return BcRunner.RunnerFailure;
}
return unchecked((int)result.ExitCode);
