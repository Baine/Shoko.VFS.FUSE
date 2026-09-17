using Shoko.VFS.FUSE.Resolvers.Relay;

namespace Shoko.VFS.FUSE.Tests.Resolvers;

/// <summary>
/// Covers the upstream-mirrored ignore helpers (VfsShared.GetIgnoredFolderNames names +
/// IsPathIgnored inline local-extra file tail). The sibling probe pattern is
/// "&lt;base&gt;.*" with the dot taken from the ".*" suffix and literal in the base-cut —
/// upstream semantics, replicated exactly.
/// </summary>
public sealed class RelayIgnoreRulesTests : IDisposable
{
    private readonly string _root;

    public RelayIgnoreRulesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"shoko-vfs-ignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void FeatureRootNames_MatchUpstreamDefaults()
    {
        Assert.Equal("!AnimeThemes", RelayIgnoreRules.AnimeThemesRootName);
        Assert.Equal("!CollectionImages", RelayIgnoreRules.CollectionImagesRootName);
    }

    [Theory]
    [InlineData("Show S01E01", "-trailer.mkv")]
    [InlineData("Show S01E01", "-behindthescenes.mkv")]
    [InlineData("Show S01E01", "-deleted2.mkv")]
    [InlineData("Movie", "-Featurette.mkv")]
    public void InlineExtraFile_IsDropped_WhenSameBaseVideoExists(string parentBase, string extraSuffixAndExtension)
    {
        File.WriteAllText(Path.Combine(_root, parentBase + ".mkv"), "video");
        string extraPath = Path.Combine(_root, parentBase + extraSuffixAndExtension);
        File.WriteAllText(extraPath, "extra");

        Assert.True(RelayIgnoreRules.IsInlineLocalExtraFile(extraPath));
    }

    [Fact]
    public void PlainVideo_IsNotAnInlineExtra()
    {
        File.WriteAllText(Path.Combine(_root, "movie.mkv"), "x");

        Assert.False(RelayIgnoreRules.IsInlineLocalExtraFile(Path.Combine(_root, "movie.mkv")));
    }

    [Fact]
    public void InlineExtraSuffixWithoutSiblingVideo_IsNotDropped()
    {
        string extraPath = Path.Combine(_root, "Solitary-trailer.mkv");
        File.WriteAllText(extraPath, "extra");

        Assert.False(RelayIgnoreRules.IsInlineLocalExtraFile(extraPath));
    }
}
