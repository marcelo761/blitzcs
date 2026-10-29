using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blitz3DCS.Audio;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Blitz3DCS.Runtime;
using Blitz3DCS.Windowing;

var repository = FindRepository();
var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(repository, "artifacts", "windows-runtime-catalog"));
var nativeRoot = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(repository, "artifacts", "native"));
if (args.Length > 2) throw new ArgumentException("Usage: GenerateWindowsRuntimeCatalog [output-directory] [native-runtime-directory]");

// Retain only actual implementations. Historical declaration-only entries must not
// make an incomplete modern runtime appear to implement additional commands.
var basePath = Path.Combine(repository, "samples", "Audio", "scp.json");
using var baseCatalog = JsonDocument.Parse(File.ReadAllText(basePath));
var functions = new SortedDictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
// Reflection may load a project dependency from the generator's output directory,
// while the hand-maintained base catalog points at the canonical Release output.
// Keep every binding for one DLL filename on that existing path so CLI assembly
// discovery never sees duplicate copies of the same runtime assembly identity.
var assemblyPathsByFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var item in baseCatalog.RootElement.GetProperty("functions").EnumerateArray())
{
    if (!item.TryGetProperty("method", out _) || !item.TryGetProperty("assembly", out var assembly)) continue;
    var function = JsonNode.Parse(item.GetRawText())!.AsObject();
    var name = function["name"]!.GetValue<string>();
    if (function.ContainsKey("binary")) throw new InvalidDataException($"The modern runtime cannot bind binary-translated command {name}.");
    var assemblyPath = Path.GetFullPath(assembly.GetString()!, Path.GetDirectoryName(basePath)!);
    if (!File.Exists(assemblyPath)) throw new FileNotFoundException("Build the managed runtime projects in Release before generating their catalog.", assemblyPath);
    assemblyPath = CanonicalAssemblyPath(assemblyPath);
    function["assembly"] = assemblyPath;
    functions[name] = function;
}

foreach (var type in new[] { typeof(LegacyScene), typeof(LegacyGraphics), typeof(LegacyWindowBindings), typeof(LegacyKernel32Bindings), typeof(LegacyInput), typeof(LegacyMath), typeof(LegacyStreams), typeof(LegacyCompression), typeof(LegacyZip), typeof(LegacySystem) })
{
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        if (method.IsSpecialName) continue;
        var parameters = method.GetParameters();
        var names = new JsonArray();
        foreach (var parameter in parameters) names.Add(ParameterType(parameter));
        var function = new JsonObject
        {
            ["name"] = method.Name,
            ["assembly"] = CanonicalAssemblyPath(type.Assembly.Location),
            ["type"] = type.FullName,
            ["method"] = method.Name,
            ["parameters"] = names,
            ["returnType"] = ScalarType(method.ReturnType)
        };
        var defaults = parameters.SkipWhile(p => !p.HasDefaultValue).ToArray();
        if (defaults.Length > 0)
        {
            if (defaults.Any(p => !p.HasDefaultValue)) throw new InvalidDataException($"Non-trailing default argument on {method.Name}.");
            function["defaults"] = JsonSerializer.SerializeToNode(defaults.Select(p => p.DefaultValue).ToArray());
        }
        functions[method.Name] = function;
    }
}

foreach (var cameraCommand in new[] { "CameraProjMode", "CameraZoom" })
    if (!functions.TryGetValue(cameraCommand, out var cameraFunction) || cameraFunction["type"]?.GetValue<string>() != typeof(LegacyGraphics).FullName)
        throw new InvalidDataException($"Legacy graphics camera command {cameraCommand} is missing or bound to the wrong runtime type.");

if (functions["CopyEntity"]["type"]?.GetValue<string>() != typeof(LegacyGraphics).FullName)
    throw new InvalidDataException("The runtime catalog must bind CopyEntity through LegacyGraphics so graphics-owned subtrees are protected.");

var conflictingAssemblyPaths = functions.Values
    .Select(function => function["assembly"]!.GetValue<string>())
    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
    .Where(group => group.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
    .Select(group => group.Key)
    .ToArray();
if (conflictingAssemblyPaths.Length != 0)
    throw new InvalidDataException($"Runtime catalog bindings reference multiple files named {string.Join(", ", conflictingAssemblyPaths)}.");

// These scalar FSOUND adapters expose the spatial mixer without placing CLR
// structs in the BASIC-facing command signature. Keep the legacy FSOUND_ names.
var spatialAudioCommands = new HashSet<string>(StringComparer.Ordinal)
{
    "PlaySample3D", "SetChannel3D", "SetChannel2D", "SetListener3D", "SetSpatialSettings"
};
var audioAssembly = functions["FSOUND_Init"]["assembly"]!.GetValue<string>();
foreach (var method in typeof(FSOUND).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
{
    if (!spatialAudioCommands.Contains(method.Name)) continue;
    var parameters = method.GetParameters();
    var function = new JsonObject
    {
        ["name"] = "FSOUND_" + method.Name,
        ["assembly"] = audioAssembly,
        ["type"] = typeof(FSOUND).FullName,
        ["method"] = method.Name,
        ["parameters"] = new JsonArray(parameters.Select(p => (JsonNode?)JsonValue.Create(ParameterType(p))).ToArray()),
        ["returnType"] = ScalarType(method.ReturnType)
    };
    var defaults = parameters.SkipWhile(p => !p.HasDefaultValue).ToArray();
    if (defaults.Length > 0)
    {
        if (defaults.Any(p => !p.HasDefaultValue)) throw new InvalidDataException($"Non-trailing default argument on {method.Name}.");
        function["defaults"] = JsonSerializer.SerializeToNode(defaults.Select(p => p.DefaultValue).ToArray());
    }
    functions["FSOUND_" + method.Name] = function;
}

Directory.CreateDirectory(output);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var scpFunctions = functions.Values
    .Where(f => !new[] { "EntityScaleX", "EntityScaleY", "EntityScaleZ" }
        .Contains(f["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
    .ToArray();
foreach (var (target, rid) in new[] { ("Win32", "win-x86"), ("Win64", "win-x64") })
{
    var native = new[]
    {
        Path.Combine(nativeRoot, "sdl3", rid, "SDL3.dll"),
        Path.Combine(nativeRoot, "libsndfile", rid, "sndfile.dll")
    };
    foreach (var path in native)
        if (!File.Exists(path)) throw new FileNotFoundException($"Install native dependencies for {target} before generating its runtime catalog.", path);
    JsonObject MakeCatalog(IEnumerable<JsonObject> bindings) => new()
    {
        ["functions"] = new JsonArray(bindings.Select(f => (JsonNode)f.DeepClone()).ToArray()),
        ["nativeLibraries"] = new JsonArray(native.Select(path => (JsonNode)JsonValue.Create(path)!).ToArray())
    };
    File.WriteAllText(Path.Combine(output, target + ".json"), MakeCatalog(functions.Values).ToJsonString(jsonOptions) + "\n");
    File.WriteAllText(Path.Combine(output, target + ".scpcb.json"), MakeCatalog(scpFunctions).ToJsonString(jsonOptions) + "\n");
}
JsonObject MakeManifest(string win32, string win64) => new()
{
    ["runtimeTargets"] = new JsonObject { ["Win32"] = win32, ["Win64"] = win64 }
};
File.WriteAllText(Path.Combine(output, "runtime.json"), MakeManifest("Win32.json", "Win64.json").ToJsonString(jsonOptions) + "\n");
File.WriteAllText(Path.Combine(output, "scpcb.json"), MakeManifest("Win32.scpcb.json", "Win64.scpcb.json").ToJsonString(jsonOptions) + "\n");
Console.WriteLine($"Generated {functions.Count} general and {scpFunctions.Length} SCP-compatible command bindings per Windows target: {Path.Combine(output, "runtime.json")} and {Path.Combine(output, "scpcb.json")}");
return;

// Struct-pointer parameters receive a BASIC Type's field image; [Out] marks write-only images.
static string ParameterType(ParameterInfo parameter) =>
    parameter.ParameterType == typeof(ReadOnlySpan<byte>) ? "PointerIn"
    : parameter.ParameterType == typeof(Span<byte>) ? (parameter.IsOut ? "PointerOut" : "PointerInOut")
    : ScalarType(parameter.ParameterType);

static string ScalarType(Type type) => type == typeof(void) ? "Void"
    : type == typeof(int) ? "Integer"
    : type == typeof(float) ? "Float"
    : type == typeof(string) ? "String"
    : throw new InvalidDataException($"BASIC runtime bindings must use Int32, Single, String, Void or byte spans, not {type}.");

string CanonicalAssemblyPath(string path)
{
    path = Path.GetFullPath(path);
    var fileName = Path.GetFileName(path);
    if (assemblyPathsByFileName.TryGetValue(fileName, out var canonicalPath)) return canonicalPath;
    assemblyPathsByFileName.Add(fileName, path);
    return path;
}

static string FindRepository()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "BlitzCS.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run the generator from the repository or one of its subdirectories.");
}
