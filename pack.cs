#:property TargetFramework=net10.0

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

var root = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var publish = args.Contains("--publish");
var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "Nyakaze/AuraPlugins";
var metaPath = Path.Combine(root, "meta.json");
var outputPath = publish ? metaPath : Path.Combine(root, "dist", "meta.json");
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

var previous = File.Exists(metaPath) && JsonNode.Parse(File.ReadAllText(metaPath))?["plugins"] is JsonArray listed
    ? listed.OfType<JsonObject>().ToDictionary(entry => (string)entry["internalName"]!, StringComparer.OrdinalIgnoreCase)
    : new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

var plugins = new JsonArray();

foreach (var folder in Directory.GetDirectories(Path.Combine(root, "plugins")).Order(StringComparer.Ordinal))
{
    var name = Path.GetFileName(folder);
    var infoPath = Path.Combine(folder, "plugin.json");

    if (!File.Exists(infoPath))
        continue;

    var version = XDocument.Load(Path.Combine(folder, name + ".csproj")).Descendants("Version").FirstOrDefault()?.Value
                  ?? Fail($"{name}.csproj has no <Version>.");
    var info = JsonNode.Parse(File.ReadAllText(infoPath))!.AsObject();
    var entry = new JsonObject { ["internalName"] = name };

    foreach (var (key, value) in info)
        entry[key] = value?.DeepClone();

    if (File.Exists(Path.Combine(folder, "icon.png")))
        entry["iconUrl"] = $"plugins/{name}/icon.png";

    if (previous.TryGetValue(name, out var released) && (string?)released["version"] == version)
    {
        foreach (var key in new[] { "version", "sdkVersion", "downloadUrl", "sha256", "size" })
            entry[key] = released[key]?.DeepClone();

        Console.WriteLine($"{name} {version}: already released.");
    }
    else
    {
        var (zip, sdkVersion) = Build(name, folder, version);
        var tag = $"{name}-{version}";
        var bytes = File.ReadAllBytes(zip);

        entry["version"] = version;
        entry["sdkVersion"] = sdkVersion;
        entry["downloadUrl"] = $"https://github.com/{repository}/releases/download/{tag}/{Path.GetFileName(zip)}";
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        entry["size"] = bytes.LongLength;

        if (publish)
            Release(tag, zip, $"{(string?)info["name"] ?? name} {version}");

        Console.WriteLine($"{name} {version}: packed {Path.GetFileName(zip)} ({bytes.LongLength / 1048576.0:0.0} MB, Aura API {sdkVersion}).");
    }

    plugins.Add(entry);
}

var meta = new JsonObject
{
    ["name"] = "Aura",
    ["url"] = $"https://github.com/{repository}",
    ["plugins"] = plugins
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, meta.ToJsonString(json) + "\n");
Console.WriteLine($"Wrote {outputPath}.");

(string Zip, string SdkVersion) Build(string name, string folder, string version)
{
    var output = Path.Combine(root, "out", name);

    if (Directory.Exists(output))
        Directory.Delete(output, recursive: true);

    Run("dotnet", "publish", Path.Combine(folder, name + ".csproj"), "-c", "Release", "-o", output, "--nologo");

    var assembly = Path.Combine(output, name + ".dll");

    if (!File.Exists(assembly))
        Fail($"{name}: the build has no {name}.dll.");

    var bundled = Directory.GetFiles(output, "*.dll").Select(Path.GetFileNameWithoutExtension)
        .Where(file => file is "Aura.Presence.Sdk" or "WinRT.Runtime" or "Microsoft.Windows.SDK.NET" ||
                       file!.StartsWith("Aura.Presence.Services.", StringComparison.Ordinal)).ToList();

    if (bundled.Count > 0)
        Fail($"{name}: the build contains {string.Join(", ", bundled)}, which Aura provides.");

    var built = AssemblyName.GetAssemblyName(assembly).Version!;

    if ($"{built.Major}.{built.Minor}.{Math.Max(built.Build, 0)}" != version)
        Fail($"{name}: built version {built} doesn't match <Version>{version}</Version>.");

    var dist = Directory.CreateDirectory(Path.Combine(root, "dist")).FullName;
    var zip = Path.Combine(dist, $"{name}-{version}.zip");
    File.Delete(zip);
    ZipFile.CreateFromDirectory(output, zip, CompressionLevel.Optimal, includeBaseDirectory: false);

    return (zip, ApiVersion(assembly));
}

static string ApiVersion(string assembly)
{
    using var reader = new PEReader(File.OpenRead(assembly));
    var metadata = reader.GetMetadataReader();
    var used = metadata.AssemblyReferences
        .Select(metadata.GetAssemblyReference)
        .Where(reference => metadata.GetString(reference.Name) is var name &&
                            (name == "Aura.Presence.Sdk" || name.StartsWith("Aura.Presence.Services.", StringComparison.Ordinal)))
        .Select(reference => reference.Version)
        .DefaultIfEmpty(new Version(1, 0))
        .Max()!;

    return $"{used.Major}.{used.Minor}";
}

static void Release(string tag, string zip, string title)
{
    if (!TryRun("gh", "release", "create", tag, zip, "--title", title, "--notes", $"{title}, built from {Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "a local checkout"}."))
        Run("gh", "release", "upload", tag, zip, "--clobber");
}

static void Run(string file, params string[] arguments)
{
    if (!TryRun(file, arguments))
        Fail($"{file} {string.Join(' ', arguments)} failed.");
}

static bool TryRun(string file, params string[] arguments)
{
    var start = new ProcessStartInfo(file);

    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    process.WaitForExit();

    return process.ExitCode == 0;
}

static string Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(1);
    return message;
}
