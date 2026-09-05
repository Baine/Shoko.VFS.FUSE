using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Attributes;
using Shoko.VFS.FUSE.Configuration;
using Shoko.VFS.FUSE.Models;

namespace Shoko.VFS.FUSE.Tests.Configuration;

public class FusePluginConfigurationTests
{
    [Fact]
    public void Configuration_HasSafeDefaultsAndStorageLocation()
    {
        var configuration = new FusePluginConfiguration();
        var storageLocation = typeof(FusePluginConfiguration).GetCustomAttribute<StorageLocationAttribute>();

        Assert.IsAssignableFrom<IConfiguration>(configuration);
        Assert.NotNull(storageLocation);
        Assert.Equal("Shoko.VFS.FUSE.json", storageLocation!.FileName);
        Assert.False(configuration.RelayEnabled);
        Assert.False(configuration.FinEnabled);
        Assert.Equal(ReadMode.DirectRead, configuration.ReadMode);
        Assert.Equal(MovieGenerationMode.Disabled, configuration.MovieGenerationMode);
        Assert.Equal("!ShokoRelayVFS", configuration.RelayTvFolderName);
        Assert.Equal("!ShokoRelayMovieVFS", configuration.RelayMovieFolderName);
        Assert.True(configuration.TmdbEpNumbering);
        Assert.False(configuration.MergeTmdbSeries);
        Assert.True(configuration.PlexLocalExtras);
        Assert.Equal("", configuration.FolderExclusions);
        Assert.Equal("", configuration.ManagedFolderExclusions);
        Assert.Equal("SHOKO", configuration.SeriesTitleLanguage);
        Assert.Equal("SHOKO", configuration.EpisodeTitleLanguage);
        Assert.True(configuration.MoveCommonSeriesTitlePrefixes);
        Assert.True(configuration.TmdbEpGroupNames);
    }

    [Fact]
    public void Configuration_DeserializesNumericMovieGenerationMode()
    {
        var configuration = JsonSerializer.Deserialize<FusePluginConfiguration>("{\"MovieGenerationMode\":1}");

        Assert.NotNull(configuration);
        Assert.Equal(MovieGenerationMode.EnabledMaintain, configuration!.MovieGenerationMode);
    }

    [Fact]
    public void Configuration_StringEnumOptionsRoundTripAndAcceptNumericInput()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var json = JsonSerializer.Serialize(new FusePluginConfiguration { MovieGenerationMode = MovieGenerationMode.EnabledMaintain }, options);
        var roundTrip = JsonSerializer.Deserialize<FusePluginConfiguration>(json, options);
        var numeric = JsonSerializer.Deserialize<FusePluginConfiguration>("{\"MovieGenerationMode\":1}", options);

        Assert.Contains("\"MovieGenerationMode\":\"EnabledMaintain\"", json);
        Assert.Equal(MovieGenerationMode.EnabledMaintain, roundTrip!.MovieGenerationMode);
        Assert.Equal(MovieGenerationMode.EnabledMaintain, numeric!.MovieGenerationMode);
    }

    [Fact]
    public void Configuration_DefaultAndZeroTimeoutsValidate()
    {
        Assert.Empty(FusePluginConfiguration.Validate(new FusePluginConfiguration(), null!, null!));

        var configurations = new[]
        {
            new FusePluginConfiguration { AttrTimeout = 0 },
            new FusePluginConfiguration { EntryTimeout = 0 },
            new FusePluginConfiguration { NegativeTimeout = 0 },
        };

        foreach (var configuration in configurations)
            Assert.Empty(FusePluginConfiguration.Validate(configuration, null!, null!));
    }

    [Fact]
    public void Configuration_InvalidTimeoutsAreKeyedByProperty()
    {
        var properties = new (string Name, Action<FusePluginConfiguration> SetValue)[]
        {
            (nameof(FusePluginConfiguration.AttrTimeout), configuration => configuration.AttrTimeout = 0),
            (nameof(FusePluginConfiguration.EntryTimeout), configuration => configuration.EntryTimeout = 0),
            (nameof(FusePluginConfiguration.NegativeTimeout), configuration => configuration.NegativeTimeout = 0),
        };
        var invalidValues = new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity };

        foreach (var (name, setValue) in properties)
        foreach (var invalidValue in invalidValues)
        {
            var configuration = new FusePluginConfiguration();
            setValue(configuration);
            switch (name)
            {
                case nameof(FusePluginConfiguration.AttrTimeout):
                    configuration.AttrTimeout = invalidValue;
                    break;
                case nameof(FusePluginConfiguration.EntryTimeout):
                    configuration.EntryTimeout = invalidValue;
                    break;
                default:
                    configuration.NegativeTimeout = invalidValue;
                    break;
            }

            var errors = FusePluginConfiguration.Validate(configuration, null!, null!);
            Assert.Contains(name, errors.Keys);
            Assert.Single(errors);
        }
    }

    [Theory]
    [InlineData(nameof(FusePluginConfiguration.RelayTvFolderName), "")]
    [InlineData(nameof(FusePluginConfiguration.RelayTvFolderName), ".")]
    [InlineData(nameof(FusePluginConfiguration.RelayTvFolderName), "..")] 
    [InlineData(nameof(FusePluginConfiguration.RelayTvFolderName), " . ")]
    [InlineData(nameof(FusePluginConfiguration.RelayTvFolderName), "a/b")]
    [InlineData(nameof(FusePluginConfiguration.RelayMovieFolderName), "a\\b")]
    public void Configuration_RejectsInvalidRelayRootSegments(string property, string value)
    {
        var configuration = new FusePluginConfiguration();
        if (property == nameof(FusePluginConfiguration.RelayTvFolderName))
            configuration.RelayTvFolderName = value;
        else
            configuration.RelayMovieFolderName = value;

        var errors = FusePluginConfiguration.Validate(configuration, null!, null!);

        Assert.Contains(property, errors.Keys);
    }

    [Fact]
    public void Configuration_RejectsDuplicateRelayRootSegmentsCaseInsensitively()
    {
        var configuration = new FusePluginConfiguration { RelayTvFolderName = "Relay", RelayMovieFolderName = "relay" };

        var errors = FusePluginConfiguration.Validate(configuration, null!, null!);

        Assert.Contains(nameof(FusePluginConfiguration.RelayMovieFolderName), errors.Keys);
    }

    [Fact]
    public void Configuration_FinDisabledAllowsBlankOrRelativeMountPoint()
    {
        var blank = new FusePluginConfiguration { FinEnabled = false, FinMountPoint = "" };
        var relative = new FusePluginConfiguration { FinEnabled = false, FinMountPoint = "fin/vfs" };

        Assert.Empty(FusePluginConfiguration.Validate(blank, null!, null!));
        Assert.Empty(FusePluginConfiguration.Validate(relative, null!, null!));
    }

    [Fact]
    public void Configuration_FinEnabledRejectsBlankMountPoint()
    {
        var configuration = new FusePluginConfiguration { FinEnabled = true, FinMountPoint = "" };

        var errors = FusePluginConfiguration.Validate(configuration, null!, null!);

        Assert.Contains(nameof(FusePluginConfiguration.FinMountPoint), errors.Keys);
        Assert.Single(errors);
    }

    [Fact]
    public void Configuration_FinEnabledRejectsRelativeMountPoint()
    {
        var configuration = new FusePluginConfiguration { FinEnabled = true, FinMountPoint = "fin/vfs" };

        var errors = FusePluginConfiguration.Validate(configuration, null!, null!);

        Assert.Contains(nameof(FusePluginConfiguration.FinMountPoint), errors.Keys);
        Assert.Single(errors);
    }

    [Fact]
    public void Configuration_FinEnabledAcceptsRootedMountPoint()
    {
        var configuration = new FusePluginConfiguration { FinEnabled = true, FinMountPoint = "/shoko/vfs-fuse" };

        Assert.Empty(FusePluginConfiguration.Validate(configuration, null!, null!));
    }
}
