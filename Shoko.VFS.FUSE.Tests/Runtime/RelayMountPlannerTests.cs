using Shoko.Abstractions.Video.Enums;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Models;
using Shoko.VFS.FUSE.Runtime;

namespace Shoko.VFS.FUSE.Tests.Runtime;

public sealed class RelayMountPlannerTests
{
    [Fact]
    public void PlansOnlyEligibleFoldersAsDirectChildren()
    {
        var configuration = new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false };
        var folders = new[]
        {
            Folder(1, "Library", "/library", DropFolderType.Excluded),
            Folder(2, "Source", "/source", DropFolderType.Source),
            Folder(3, "Named", "/named", DropFolderType.Excluded),
        };

        var plan = new RelayMountPlanner().Plan(configuration, folders);

        Assert.Equal(new[] { 1, 3 }, plan.EligibleFolders.Select(folder => folder.Id));
        Assert.Equal(new[] { 2 }, plan.ExcludedFolders.Select(folder => folder.Id));
        Assert.Equal(new[] { "/library/!ShokoRelayVFS", "/named/!ShokoRelayVFS" }, plan.Targets.Select(target => target.TargetPath));
        Assert.All(plan.Targets, target => Assert.Equal(RelayMountRootKind.Tv, target.RootKind));
    }

    [Fact]
    public void ExcludesManagedFoldersByIdOrName()
    {
        var plan = new RelayMountPlanner().Plan(
            new FusePluginConfiguration
            {
                RelayEnabled = true,
                PlexLocalExtras = false,
                ManagedFolderExclusions = "2\nNamed",
            },
            [
                Folder(1, "Library", "/library", DropFolderType.Excluded),
                Folder(2, "ById", "/by-id", DropFolderType.Excluded),
                Folder(3, "Named", "/named", DropFolderType.Excluded),
            ]
        );

        Assert.Equal(new[] { 1 }, plan.EligibleFolders.Select(folder => folder.Id));
        Assert.Equal(new[] { 2, 3 }, plan.ExcludedFolders.Select(folder => folder.Id));
    }

    [Fact]
    public void PlansMovieMatrixAndIndependentResolverFlags()
    {
        var folder = new[] { Folder(1, "Library", "/library", DropFolderType.Excluded) };
        foreach (var mode in Enum.GetValues<MovieGenerationMode>())
        {
            var plan = new RelayMountPlanner().Plan(new FusePluginConfiguration
            {
                RelayEnabled = true,
                PlexLocalExtras = false,
                MovieGenerationMode = mode,
            }, folder);

            var tv = Assert.Single(plan.Targets, target => target.RootKind == RelayMountRootKind.Tv);
            var movie = plan.Targets.SingleOrDefault(target => target.RootKind == RelayMountRootKind.Movie);
            if (mode == MovieGenerationMode.Disabled)
            {
                Assert.Null(movie);
                Assert.True(tv.ResolverOptions.Shows);
                Assert.True(tv.ResolverOptions.MoviesAsTv);
            }
            else
            {
                Assert.NotNull(movie);
                Assert.True(movie!.ResolverOptions.StandaloneMovies);
                Assert.Equal(mode != MovieGenerationMode.EnabledRemove, tv.ResolverOptions.MoviesAsTv);
            }
        }
    }

    [Fact]
    public void BlocksDuplicateAndNestedCanonicalRoots()
    {
        var configuration = new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = false };
        var folders = new[]
        {
            Folder(1, "One", "/library", DropFolderType.Excluded),
            Folder(2, "Two", "/library/!ShokoRelayVFS", DropFolderType.Excluded),
        };

        var plan = new RelayMountPlanner().Plan(configuration, folders);

        Assert.True(plan.IsBlocked);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "NestedRoot" && diagnostic.Blocking);
    }

    [Fact]
    public void ReportsLocalExtrasAsDegradedButNotBlocking()
    {
        var plan = new RelayMountPlanner().Plan(
            new FusePluginConfiguration { RelayEnabled = true, PlexLocalExtras = true },
            [Folder(1, "Library", "/library", DropFolderType.Excluded)]
        );

        Assert.True(plan.IsDegraded);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "LocalExtrasUnsupported" && !diagnostic.Blocking);
    }

    private static RelayManagedFolderSnapshot Folder(int id, string name, string path, DropFolderType type) =>
        new(id, name, path, type);
}
