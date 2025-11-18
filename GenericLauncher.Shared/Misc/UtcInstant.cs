using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenericLauncher.Misc;

[JsonConverter(typeof(UtcInstantJsonConverter))]
public readonly struct UtcInstant : IEquatable<UtcInstant>, IComparable<UtcInstant>
{
    private readonly DateTime _value;

    public UtcInstant()
    {
        _value = DateTime.UtcNow;
    }

    public UtcInstant(DateTime dateTime)
    {
        if (dateTime.Kind != DateTimeKind.Utc)
        {
            _value = dateTime.ToUniversalTime();
        }

        _value = dateTime;
    }

    // Factory methods
    public static UtcInstant Now
    {
        get => new(DateTime.UtcNow);
    }

    public static UtcInstant FromUnixTimeMilliseconds(long milliseconds) =>
        new(DateTime.UnixEpoch.AddMilliseconds(milliseconds));

    // Parse methods
    public static UtcInstant Parse(string isoString)
    {
        var dt = DateTime.Parse(isoString, null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new UtcInstant(dt);
    }

    public static bool TryParse(string isoString, out UtcInstant result)
    {
        if (DateTime.TryParse(isoString,
                null,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            result = new UtcInstant(dt);
            return true;
        }

        result = default;
        return false;
    }

    public static UtcInstant ParseExact(string input, string format)
    {
        var dt = DateTime.ParseExact(input,
            format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new UtcInstant(dt);
    }

    // Conversions
    public DateTime ToDateTime() => _value;
    public DateTimeOffset ToDateTimeOffset() => new(_value);

    public long UnixTimeMilliseconds
    {
        get => (long)(_value - DateTime.UnixEpoch).TotalMilliseconds;
    }

    // Arithmetic
    public UtcInstant Add(TimeSpan timeSpan) => new(_value.Add(timeSpan));
    public UtcInstant AddDays(double days) => new(_value.AddDays(days));
    public UtcInstant AddHours(double hours) => new(_value.AddHours(hours));
    public UtcInstant AddMinutes(double minutes) => new(_value.AddMinutes(minutes));
    public UtcInstant AddSeconds(double seconds) => new(_value.AddSeconds(seconds));
    public UtcInstant AddMilliseconds(double milliseconds) => new(_value.AddMilliseconds(milliseconds));

    public TimeSpan Subtract(UtcInstant other) => _value - other._value;
    public UtcInstant Subtract(TimeSpan timeSpan) => new(_value - timeSpan);

    // Comparisons
    public bool Equals(UtcInstant other) => _value.Equals(other._value);
    public int CompareTo(UtcInstant other) => _value.CompareTo(other._value);

    public override bool Equals(object? obj) => obj is UtcInstant other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();

    // Operators
    public static bool operator ==(UtcInstant left, UtcInstant right) => left.Equals(right);
    public static bool operator !=(UtcInstant left, UtcInstant right) => !left.Equals(right);
    public static bool operator <(UtcInstant left, UtcInstant right) => left._value < right._value;
    public static bool operator >(UtcInstant left, UtcInstant right) => left._value > right._value;
    public static bool operator <=(UtcInstant left, UtcInstant right) => left._value <= right._value;
    public static bool operator >=(UtcInstant left, UtcInstant right) => left._value >= right._value;

    public static TimeSpan operator -(UtcInstant left, UtcInstant right) => left.Subtract(right);
    public static UtcInstant operator +(UtcInstant instant, TimeSpan duration) => instant.Add(duration);
    public static UtcInstant operator -(UtcInstant instant, TimeSpan duration) => instant.Subtract(duration);

    // String representations
    public override string ToString() => ToRfc3339SecondsString();
    public string ToString(string format) => _value.ToString(format);

    public string ToIsoString() => _value.ToString("O");
    public string ToRfc3339SecondsString() => _value.ToString("yyyy-MM-dd'T'HH:mm:ssZ");
    public string ToRfc3339MillisecondsString() => _value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffZ");

    // Common values
    public static UtcInstant MinValue
    {
        get => new(DateTime.MinValue.ToUniversalTime());
    }

    public static UtcInstant MaxValue
    {
        get => new(DateTime.MaxValue.ToUniversalTime());
    }

    public static UtcInstant UnixEpoch
    {
        get => new(DateTime.UnixEpoch);
    }
}

// JSON Converter for System.Text.Json
public class UtcInstantJsonConverter : JsonConverter<UtcInstant>
{
    public override UtcInstant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Handle string ISO format
        if (reader.TokenType == JsonTokenType.String)
        {
            var stringValue = reader.GetString();
            if (string.IsNullOrEmpty(stringValue))
            {
                return default;
            }

            if (UtcInstant.TryParse(stringValue, out var result))
            {
                return result;
            }

            throw new JsonException($"Unable to parse '{stringValue}' as UtcInstant.");
        }

        // Handle numeric (epoch milliseconds)
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out var epochMillis))
            {
                return UtcInstant.FromUnixTimeMilliseconds(epochMillis);
            }
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} when parsing UtcInstant.");
    }

    public override void Write(Utf8JsonWriter writer, UtcInstant value, JsonSerializerOptions options)
    {
        // Write as ISO 8601 string by default
        writer.WriteStringValue(value.ToIsoString());
    }
}
