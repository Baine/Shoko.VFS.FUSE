using System.Globalization;
using Newtonsoft.Json;

namespace Shoko.VFS.FUSE.Host.Models;

/// <summary>
/// DTO-only shape of the server's <c>Shoko.Abstractions.Metadata.PartialDateOnly</c>.
/// The server's serialization is inconsistent across endpoints: some return the
/// struct as a plain object (<c>Year</c>/<c>Month</c>/<c>Day</c>...) and some
/// return it as an ISO date string (<c>"2007-12-22"</c>). <see cref="PartialDateOnlyConverter"/>
/// accepts both shapes so the relay client does not blow up on whichever the
/// server happens to send. Convert to the Abstractions struct with its
/// <c>PartialDateOnly(int year, int? month, int? day)</c> constructor at the
/// RelayRaw* construction sites.
/// </summary>
public struct PartialDateOnly
{
    public int Year { get; set; }

    public int? Month { get; set; }

    public int? Day { get; set; }

    public int? DayOfYear { get; set; }

    public int? DayOfWeek { get; set; }

    public int? DayNumber { get; set; }

    public bool IsComplete => Month.HasValue && Day.HasValue;
}

/// <summary>
/// Newtonsoft converter for <see cref="PartialDateOnly"/> and <see cref="Nullable{PartialDateOnly}"/>.
/// The Shoko server returns <c>AniDB.AirDate</c> as an ISO date string (<c>"2007-12-22"</c>)
/// in some endpoints and as a <c>{Year,Month,Day}</c> object in others; both must round-trip.
/// Non-generic so Newtonsoft applies it to both the struct and its nullable variant.
/// </summary>
public sealed class PartialDateOnlyConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) =>
        objectType == typeof(PartialDateOnly) || objectType == typeof(PartialDateOnly?);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var isNullable = objectType == typeof(PartialDateOnly?);
        if (reader.TokenType == JsonToken.Null)
            return isNullable ? null : (object)default(PartialDateOnly);

        if (reader.TokenType == JsonToken.String)
            return ParseIsoDate((string?)reader.Value);

        if (reader.TokenType == JsonToken.StartObject)
        {
            int year = 0;
            int? month = null;
            int? day = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject)
                    break;
                if (reader.TokenType != JsonToken.PropertyName)
                    continue;
                var name = (string)reader.Value!;
                if (!reader.Read()) break;
                switch (name)
                {
                    case "Year": year = Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture); break;
                    case "Month": month = reader.Value is null ? null : Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture); break;
                    case "Day": day = reader.Value is null ? null : Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture); break;
                    default: reader.Skip(); break;
                }
            }
            return new PartialDateOnly { Year = year, Month = month, Day = day };
        }

        // Unknown shape: don't blow up the whole snapshot, skip and return default.
        reader.Skip();
        return isNullable ? null : (object)default(PartialDateOnly);
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null) { writer.WriteNull(); return; }
        var v = (PartialDateOnly)value;
        writer.WriteStartObject();
        writer.WritePropertyName("Year"); writer.WriteValue(v.Year);
        if (v.Month.HasValue) { writer.WritePropertyName("Month"); writer.WriteValue(v.Month.Value); }
        if (v.Day.HasValue) { writer.WritePropertyName("Day"); writer.WriteValue(v.Day.Value); }
        writer.WriteEndObject();
    }

    private static PartialDateOnly ParseIsoDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return default;
        var trimmed = s!.Trim();
        var datePart = trimmed.Length >= 10 ? trimmed[..10] : trimmed;
        if (!DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) &&
            !DateTime.TryParseExact(datePart, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) &&
            !DateTime.TryParseExact(datePart, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) &&
            !DateTime.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            return default;
        }
        return new PartialDateOnly
        {
            Year = dt.Year,
            Month = datePart.Length >= 7 ? (int?)dt.Month : null,
            Day = datePart.Length >= 10 ? (int?)dt.Day : null,
        };
    }
}