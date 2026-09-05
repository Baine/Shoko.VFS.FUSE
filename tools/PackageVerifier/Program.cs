using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class Program
{
    private const string RootAssembly = "Shoko.VFS.FUSE";
    private const string PluginId = "c6b59b26-9fd1-4220-ab6d-0b2d92e19022";
    private const string RuntimeIdentifier = "linux-x64";
    private const string TargetFramework = ".NETCoreApp,Version=v10.0";

    // These are deliberately exact. The host supplies the Shoko API and the .NET framework.
    private static readonly HashSet<string> HostLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shoko.Abstractions",
    };

    private static readonly HashSet<string> FrameworkLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NETCore.App",
        "Microsoft.AspNetCore.App",
    };

    private static readonly HashSet<string> HostAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shoko.Abstractions.dll",
    };

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: PackageVerifier <staging-directory> <numeric-version>");
            return 2;
        }

        try
        {
            Verify(Path.GetFullPath(args[0]), args[1]);
            Console.WriteLine("Package verifier: PASS");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or BadImageFormatException)
        {
            Console.Error.WriteLine($"Package verifier: FAIL: {ex.Message}");
            return 1;
        }
    }

    private static void Verify(string stagingDirectory, string version)
    {
        if (!Directory.Exists(stagingDirectory))
            throw new InvalidDataException($"staging directory does not exist: {stagingDirectory}");
        if (!IsNumericVersion(version))
            throw new InvalidDataException($"version is not numeric x.y.z: {version}");

        string depsPath = Path.Combine(stagingDirectory, $"{RootAssembly}.deps.json");
        if (!File.Exists(depsPath))
            throw new InvalidDataException($"missing dependency graph: {Path.GetFileName(depsPath)}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(depsPath));
        JsonElement root = document.RootElement;
        string expectedTarget = $"{TargetFramework}/{RuntimeIdentifier}";
        string actualTarget = root.GetProperty("runtimeTarget").GetProperty("name").GetString() ?? "";
        if (!string.Equals(actualTarget, expectedTarget, StringComparison.Ordinal))
            throw new InvalidDataException($"runtime target is {actualTarget}, expected {expectedTarget}");

        JsonElement targetGraph = root.GetProperty("targets").GetProperty(expectedTarget);
        string rootKey = $"{RootAssembly}/{version}";
        if (!targetGraph.TryGetProperty(rootKey, out _))
            throw new InvalidDataException($"target graph does not contain {rootKey}");

        var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in targetGraph.EnumerateObject())
        {
            nodes.Add(property.Name, property.Value);
            (string id, string nodeVersion) = SplitNodeKey(property.Name);
            identities.Add(Identity(id, nodeVersion), property.Name);
        }

        var reachableDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>([rootKey]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count != 0)
        {
            string nodeKey = pending.Dequeue();
            if (!visited.Add(nodeKey))
                continue;
            if (!nodes.TryGetValue(nodeKey, out JsonElement node))
                throw new InvalidDataException($"reachable dependency node is missing: {nodeKey}");

            (string id, _) = SplitNodeKey(nodeKey);
            bool excludedLibrary = HostLibraries.Contains(id) || FrameworkLibraries.Contains(id);
            if (excludedLibrary)
                continue;

            if (node.TryGetProperty("runtime", out JsonElement runtime))
            {
                foreach (JsonProperty asset in runtime.EnumerateObject())
                {
                    string assetName = Path.GetFileName(asset.Name);
                    if (assetName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && !HostAssets.Contains(assetName))
                    {
                        reachableDlls.Add(assetName);
                    }
                }
            }

            if (!node.TryGetProperty("dependencies", out JsonElement dependencies))
                continue;
            foreach (JsonProperty dependency in dependencies.EnumerateObject())
            {
                string dependencyVersion = dependency.Value.GetString()
                    ?? throw new InvalidDataException($"dependency version is not a string: {dependency.Name}");
                string dependencyIdentity = Identity(dependency.Name, dependencyVersion);
                if (identities.TryGetValue(dependencyIdentity, out string? dependencyKey))
                {
                    pending.Enqueue(dependencyKey);
                }
                else if (!HostLibraries.Contains(dependency.Name)
                    && !FrameworkLibraries.Contains(dependency.Name))
                {
                    throw new InvalidDataException($"reachable dependency node is missing: {dependency.Name}/{dependencyVersion}");
                }
            }
        }

        var stagedDlls = Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(stagingDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedStagedDlls = reachableDlls.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missing = expectedStagedDlls.Except(stagedDlls, StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToArray();
        string[] unexpected = stagedDlls.Except(expectedStagedDlls, StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToArray();
        if (missing.Length != 0 || unexpected.Length != 0)
        {
            throw new InvalidDataException(
                $"runtime DLL closure mismatch; missing=[{string.Join(", ", missing)}], "
                + $"unexpected=[{string.Join(", ", unexpected)}]");
        }

        string assemblyPath = Path.Combine(stagingDirectory, $"{RootAssembly}.dll");
        VerifyAssemblyMetadata(assemblyPath, version);
    }

    private static void VerifyAssemblyMetadata(string assemblyPath, string version)
    {
        if (!File.Exists(assemblyPath))
            throw new InvalidDataException($"missing plugin assembly: {Path.GetFileName(assemblyPath)}");

        using FileStream stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            throw new BadImageFormatException("plugin assembly has no CLI metadata");

        MetadataReader metadata = peReader.GetMetadataReader();
        AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
        Version expectedAssemblyVersion = new($"{version}.0");
        if (assembly.Version != expectedAssemblyVersion)
            throw new InvalidDataException($"AssemblyVersion is {assembly.Version}, expected {expectedAssemblyVersion}");

        var assemblyMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CustomAttributeHandle attributeHandle in assembly.GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(attributeHandle);
            if (!string.Equals(GetAttributeTypeName(metadata, attribute.Constructor), "System.Reflection.AssemblyMetadataAttribute", StringComparison.Ordinal))
                continue;

            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
                throw new InvalidDataException("invalid AssemblyMetadataAttribute constructor data");
            string key = value.ReadSerializedString()
                ?? throw new InvalidDataException("AssemblyMetadataAttribute key is null");
            string metadataValue = value.ReadSerializedString()
                ?? throw new InvalidDataException($"AssemblyMetadataAttribute value is null for {key}");
            if (!assemblyMetadata.TryAdd(key, metadataValue))
                throw new InvalidDataException($"duplicate AssemblyMetadataAttribute key: {key}");
        }

        RequireMetadata(assemblyMetadata, "PackageID", PluginId);
        RequireMetadata(assemblyMetadata, "RuntimeIdentifier", RuntimeIdentifier);
    }

    private static void RequireMetadata(IReadOnlyDictionary<string, string> metadata, string key, string expected)
    {
        if (!metadata.TryGetValue(key, out string? actual) || !string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"assembly metadata {key} is {actual ?? "<missing>"}, expected {expected}");
    }

    private static string GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        EntityHandle typeHandle = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default,
        };
        if (typeHandle.IsNil)
            return "";

        return typeHandle.Kind switch
        {
            HandleKind.TypeDefinition => FullTypeName(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
            HandleKind.TypeReference => FullTypeName(metadata, metadata.GetTypeReference((TypeReferenceHandle)typeHandle)),
            _ => "",
        };
    }

    private static string FullTypeName(MetadataReader metadata, TypeDefinition definition)
    {
        string @namespace = metadata.GetString(definition.Namespace);
        string name = metadata.GetString(definition.Name);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static string FullTypeName(MetadataReader metadata, TypeReference reference)
    {
        string @namespace = metadata.GetString(reference.Namespace);
        string name = metadata.GetString(reference.Name);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static (string Id, string Version) SplitNodeKey(string key)
    {
        int separator = key.LastIndexOf('/');
        if (separator <= 0 || separator == key.Length - 1)
            throw new InvalidDataException($"invalid dependency node key: {key}");
        return (key[..separator], key[(separator + 1)..]);
    }

    private static string Identity(string id, string version) => $"{id}\0{version}";

    private static bool IsNumericVersion(string value)
    {
        string[] parts = value.Split('.');
        return parts.Length == 3 && parts.All(part => part.Length != 0 && part.All(char.IsAsciiDigit));
    }
}
