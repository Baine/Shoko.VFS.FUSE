using System.Collections.ObjectModel;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Resolvers;

/// <summary>Immutable tree used by all resolver snapshot traversal.</summary>
internal sealed class VirtualTreeSnapshot
{
    private readonly VirtualTreeNode _root;

    private VirtualTreeSnapshot(VirtualTreeNode root) => _root = root;

    internal static VirtualTreeSnapshot Build(
        IReadOnlyList<VirtualTreeEntryData> entries,
        bool legacyFirstWins,
        DateTimeOffset defaultLastModified)
    {
        var normalized = new List<NormalizedEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is null)
                throw new ArgumentException("A virtual tree entry must not be null.", nameof(entries));

            var parts = NormalizePath(entry.Path);
            if (!legacyFirstWins)
                ValidatePayload(entry, parts.Path);
            normalized.Add(new NormalizedEntry(parts.Path, parts.Segments, entry));
        }

        if (!legacyFirstWins)
            ValidateCollisions(normalized);

        var root = new MutableNode("root", VirtualNodeType.Directory, 0, VirtualEntry.DefaultMode(VirtualNodeType.Directory), defaultLastModified);
        foreach (var entry in normalized)
            Add(root, entry, legacyFirstWins, defaultLastModified);

        return new VirtualTreeSnapshot(Freeze(root));
    }

    internal IReadOnlyList<VirtualEntry> ReadDirectory(IReadOnlyList<string> segments)
    {
        var node = Find(segments);
        if (node is null || node.NodeType != VirtualNodeType.Directory)
            return Array.Empty<VirtualEntry>();

        return node.Children.Select(ToVirtualEntry).ToArray();
    }

    internal VirtualEntry? Lookup(IReadOnlyList<string> segments)
    {
        var node = Find(segments);
        return node is null ? null : ToVirtualEntry(node);
    }

    private VirtualTreeNode? Find(IReadOnlyList<string> segments)
    {
        var current = _root;
        foreach (var segment in segments)
        {
            if (!current.ChildrenByName.TryGetValue(segment, out current!))
                return null;
        }

        return current;
    }

    private static VirtualEntry ToVirtualEntry(VirtualTreeNode node) => new()
    {
        Name = node.Name,
        NodeType = node.NodeType,
        Size = node.Size,
        Mode = node.Mode,
        LastModified = node.LastModified,
        SourcePath = node.SourcePath,
        SymlinkTarget = node.SymlinkTarget,
    };

    private static void ValidateCollisions(IReadOnlyList<NormalizedEntry> entries)
    {
        var byPath = new Dictionary<string, NormalizedEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!byPath.TryAdd(entry.Path, entry))
                throw new InvalidOperationException($"Duplicate virtual tree path '{entry.Path}'.");
        }

        var ordered = entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        for (int index = 0; index < ordered.Length; index++)
        {
            var entry = ordered[index];
            if (entry.Entry.NodeType != VirtualNodeType.Directory
                && index + 1 < ordered.Length
                && ordered[index + 1].Path.StartsWith(entry.Path + "/", StringComparison.Ordinal))
                throw new InvalidOperationException($"Virtual tree node '{entry.Path}' cannot have descendants.");

            for (int ancestorIndex = entry.Segments.Length - 1; ancestorIndex > 0; ancestorIndex--)
            {
                string parentPath = string.Join('/', entry.Segments[..ancestorIndex]);
                if (byPath.TryGetValue(parentPath, out var parent) && parent.Entry.NodeType != VirtualNodeType.Directory)
                    throw new InvalidOperationException($"Virtual tree node '{parentPath}' cannot have descendants.");
            }
        }
    }

    private static void Add(MutableNode root, NormalizedEntry normalized, bool legacyFirstWins, DateTimeOffset defaultLastModified)
    {
        var current = root;
        for (int index = 0; index < normalized.Segments.Length; index++)
        {
            string segment = normalized.Segments[index];
            bool isLeaf = index == normalized.Segments.Length - 1;

            if (!current.ChildrenByName.TryGetValue(segment, out var child))
            {
                child = new MutableNode(
                    segment,
                    VirtualNodeType.Directory,
                    0,
                    VirtualEntry.DefaultMode(VirtualNodeType.Directory),
                    defaultLastModified
                );
                current.ChildrenByName.Add(segment, child);
                current.Children.Add(child);
            }

            if (!isLeaf)
            {
                if (child.NodeType != VirtualNodeType.Directory && child.IsExplicit)
                    return;
                current = child;
                continue;
            }

            if (child.IsExplicit)
                return;
            if (child.Children.Count > 0 && normalized.Entry.NodeType != VirtualNodeType.Directory)
                return;

            child.NodeType = normalized.Entry.NodeType;
            child.Size = normalized.Entry.Size;
            child.Mode = normalized.Entry.Mode;
            child.LastModified = normalized.Entry.LastModified.ToUniversalTime();
            child.SourcePath = normalized.Entry.SourcePath;
            child.SymlinkTarget = normalized.Entry.SymlinkTarget;
            child.IsExplicit = true;
        }
    }

    private static VirtualTreeNode Freeze(MutableNode node)
    {
        var children = node.Children.Select(Freeze).ToArray();
        return new VirtualTreeNode(
            node.Name,
            node.NodeType,
            node.Size,
            node.Mode,
            node.LastModified,
            node.SourcePath,
            node.SymlinkTarget,
            new ReadOnlyCollection<VirtualTreeNode>(children)
        );
    }

    private static (string Path, string[] Segments) NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Virtual tree paths must not be blank.", nameof(path));
        if (path.IndexOf('\0') >= 0)
            throw new ArgumentException("Virtual tree paths must not contain NUL characters.", nameof(path));

        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(path)
            || IsDriveRooted(normalized))
            throw new ArgumentException($"Virtual tree path '{path}' must be mount-root relative.", nameof(path));

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            throw new ArgumentException($"Virtual tree path '{path}' contains an unsafe component.", nameof(path));

        return (normalized, segments);
    }

    private static void ValidatePayload(VirtualTreeEntryData entry, string path)
    {
        if (!Enum.IsDefined(entry.NodeType))
            throw new ArgumentException($"Virtual tree path '{path}' has an invalid node type.", nameof(entry));
        if (entry.Size < 0)
            throw new ArgumentOutOfRangeException(nameof(entry.Size), $"Virtual tree path '{path}' has a negative size.");

        switch (entry.NodeType)
        {
            case VirtualNodeType.Directory when entry.SourcePath is not null || entry.SymlinkTarget is not null:
                throw new ArgumentException($"Directory '{path}' cannot have file or symlink payload.", nameof(entry));
            case VirtualNodeType.File:
                if (entry.SymlinkTarget is not null || string.IsNullOrWhiteSpace(entry.SourcePath) || !Path.IsPathRooted(entry.SourcePath))
                    throw new ArgumentException($"File '{path}' must have an absolute SourcePath and no symlink target.", nameof(entry));
                break;
            case VirtualNodeType.Symlink:
                if (entry.SourcePath is not null || string.IsNullOrWhiteSpace(entry.SymlinkTarget))
                    throw new ArgumentException($"Symlink '{path}' must have a target and no SourcePath.", nameof(entry));
                break;
        }
    }

    private static bool IsDriveRooted(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '/';

    private sealed record NormalizedEntry(string Path, string[] Segments, VirtualTreeEntryData Entry);

    private sealed class MutableNode(
        string name,
        VirtualNodeType nodeType,
        long size,
        uint mode,
        DateTimeOffset lastModified)
    {
        public string Name { get; } = name;
        public VirtualNodeType NodeType { get; set; } = nodeType;
        public long Size { get; set; } = size;
        public uint Mode { get; set; } = mode;
        public DateTimeOffset LastModified { get; set; } = lastModified;
        public string? SourcePath { get; set; }
        public string? SymlinkTarget { get; set; }
        public bool IsExplicit { get; set; }
        public List<MutableNode> Children { get; } = [];
        public Dictionary<string, MutableNode> ChildrenByName { get; } = new(StringComparer.Ordinal);
    }

    private sealed class VirtualTreeNode(
        string name,
        VirtualNodeType nodeType,
        long size,
        uint mode,
        DateTimeOffset lastModified,
        string? sourcePath,
        string? symlinkTarget,
        IReadOnlyList<VirtualTreeNode> children)
    {
        public string Name { get; } = name;
        public VirtualNodeType NodeType { get; } = nodeType;
        public long Size { get; } = size;
        public uint Mode { get; } = mode;
        public DateTimeOffset LastModified { get; } = lastModified;
        public string? SourcePath { get; } = sourcePath;
        public string? SymlinkTarget { get; } = symlinkTarget;
        public IReadOnlyList<VirtualTreeNode> Children { get; } = children;
        public IReadOnlyDictionary<string, VirtualTreeNode> ChildrenByName { get; } =
            new ReadOnlyDictionary<string, VirtualTreeNode>(children.ToDictionary(child => child.Name, StringComparer.Ordinal));
    }
}
