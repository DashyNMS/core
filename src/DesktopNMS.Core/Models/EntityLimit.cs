using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// A rule's <c>max_entities</c> to write (#11): nothing (the default - the
/// key isn't sent and the rule keeps its value), null (inherit the server's
/// default), or a number.
/// </summary>
[JsonConverter(typeof(EntityLimitConverter))]
public readonly record struct EntityLimit(bool IsSet, int? Value)
{
    /// <summary>Back to the server's default.</summary>
    public static EntityLimit Inherit { get; } = new(true, null);

    public static EntityLimit Of(int? value) => new(true, value);

    public override string ToString() => !IsSet ? "unchanged" : Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "inherit";
}

internal sealed class EntityLimitConverter : JsonConverter<EntityLimit>
{
    public override bool HandleNull => true;

    public override EntityLimit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? EntityLimit.Inherit : EntityLimit.Of(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, EntityLimit value, JsonSerializerOptions options)
    {
        if (value.Value is { } limit)
        {
            writer.WriteNumberValue(limit);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
