using Newtonsoft.Json;
using Shoko.VFS.FUSE.Host.Models;

namespace Shoko.VFS.FUSE.Tests.Models;

public class PartialDateOnlyConverterTests
{
    private static readonly JsonConverter Conv = new PartialDateOnlyConverter();

    private static T Deser<T>(string json) => JsonConvert.DeserializeObject<T>(json, Conv)!;

    [Fact]
    public void IsoDateString_Parses()
    {
        var v = Deser<PartialDateOnly>("\"2007-12-22\"");
        Assert.Equal(2007, v.Year);
        Assert.Equal(12, v.Month);
        Assert.Equal((int?)22, v.Day);
    }

    [Fact]
    public void IsoDateString_YearMonthOnly_Parses()
    {
        var v = Deser<PartialDateOnly>("\"2007-12\"");
        Assert.Equal(2007, v.Year);
        Assert.Equal(12, v.Month);
        Assert.Null(v.Day);
    }

    [Fact]
    public void ObjectShape_Parses()
    {
        var v = Deser<PartialDateOnly>("{\"Year\":2007,\"Month\":12,\"Day\":22}");
        Assert.Equal(2007, v.Year);
        Assert.Equal(12, v.Month);
        Assert.Equal((int?)22, v.Day);
    }

    [Fact]
    public void NonNullable_Null_ReturnsDefault()
    {
        var v = Deser<PartialDateOnly>("null");
        Assert.Equal(0, v.Year);
        Assert.False(v.IsComplete);
    }

    [Fact]
    public void Nullable_String_Parses()
    {
        var v = Deser<PartialDateOnly?>("\"2007-12-22\"");
        Assert.NotNull(v);
        Assert.Equal(2007, v!.Value.Year);
        Assert.Equal((int?)22, v.Value.Day);
    }

    [Fact]
    public void Nullable_Null_ReturnsNull()
    {
        var v = Deser<PartialDateOnly?>("null");
        Assert.Null(v);
    }

    [Fact]
    public void NestedInSeries_HandlesStringAndObject()
    {
        var json = "[" +
                   "{\"AirDate\":\"2007-12-22\"}," +
                   "{\"AirDate\":{\"Year\":2020,\"Month\":5,\"Day\":3}}" +
                   "]";
        var list = JsonConvert.DeserializeObject<List<AniDate>>(json, Conv)!;
        Assert.Equal(2007, list[0].AirDate.Year);
        Assert.Equal(12, list[0].AirDate.Month);
        Assert.Equal((int?)22, list[0].AirDate.Day);
        Assert.Equal(2020, list[1].AirDate.Year);
        Assert.Equal(5, list[1].AirDate.Month);
        Assert.Equal((int?)3, list[1].AirDate.Day);
    }

    [Fact]
    public void NestedInSeries_HandlesNullableString()
    {
        // Real-world payload shape: List[].AniDB.AirDate is nullable.
        var json = "[" +
                   "{\"AniDB\":{\"AirDate\":\"2007-12-22\"}}," +
                   "{\"AniDB\":{\"AirDate\":null}}" +
                   "]";
        var list = JsonConvert.DeserializeObject<List<AniWrap>>(json, Conv)!;
        Assert.NotNull(list[0].AniDB!.AirDate);
        Assert.Equal(2007, list[0].AniDB!.AirDate!.Value.Year);
        Assert.Null(list[1].AniDB!.AirDate);
    }

    [Fact]
    public void EmptyString_ReturnsDefault()
    {
        var v = Deser<PartialDateOnly>("\"\"");
        Assert.Equal(0, v.Year);
    }

    private sealed class AniDate { public PartialDateOnly AirDate { get; set; } }
    private sealed class AniWrap { public AniBlock? AniDB { get; set; } }
    private sealed class AniBlock { public PartialDateOnly? AirDate { get; set; } }
}
