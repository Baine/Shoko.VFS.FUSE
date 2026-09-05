using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Resolvers;

namespace Shoko.VFS.FUSE.Tests;

/// <summary>
/// Simple in-memory resolver for tests: a dictionary of absolute paths to entries.
/// Directory children are derived by prefix matching on "/".
/// </summary>
public sealed class InMemoryResolver : IVirtualPathResolver
{
    private readonly Dictionary<string, VirtualEntry> _entries = new(StringComparer.Ordinal);

    public InMemoryResolver Add(string path, VirtualEntry entry)
    {
        _entries[path] = entry;
        return this;
    }

    public IReadOnlyList<VirtualEntry> ReadDirectory(string mountRelativePath)
    {
        // Normalize: kernel passes "/12345", storage uses "12345".
        string normalized = mountRelativePath.TrimStart('/').TrimEnd('/');

        // Root
        if (normalized.Length == 0)
            return _entries.Keys.Where(k => !k.Contains('/'))
                .Select(k => _entries[k]).ToList();

        string prefix = normalized + "/";
        return _entries.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Where(k => !k[prefix.Length..].Contains('/'))
            .Select(k => _entries[k])
            .ToList();
    }

    public VirtualEntry? Lookup(string mountRelativePath)
    {
        string normalized = mountRelativePath.TrimStart('/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            return new VirtualEntry { Name = "root", NodeType = VirtualNodeType.Directory };
        }
        return _entries.TryGetValue(normalized, out var e) ? e : null;
    }

    public string? GetSourcePath(string mountRelativePath) => Lookup(mountRelativePath)?.SourcePath;

    public void Invalidate(string mountRelativePath, bool includeChildren = false)
    {
    }

    public void Rebuild()
    {
    }
}
