using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenericLauncher.Minecraft.Json;

public class ArgumentConverter : JsonConverter<Argument>
{
    public override Argument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Create a copy of the reader since we need to inspect the token type
        var readerCopy = reader;

        switch (readerCopy.TokenType)
        {
            case JsonTokenType.String:
                var stringValue = reader.GetString()!;
                return new StringArgument(stringValue);

            case JsonTokenType.StartObject:
                using (var document = JsonDocument.ParseValue(ref reader))
                {
                    var root = document.RootElement;

                    // Check if it's an object argument with rules and value
                    if (root.TryGetProperty("rules", out var rulesElement) ||
                        root.TryGetProperty("value", out var valueElement))
                    {
                        List<Rule>? rules = null;
                        JsonElement value = default;

                        if (rulesElement.ValueKind != JsonValueKind.Undefined)
                        {
                            rules = JsonSerializer.Deserialize(
                                rulesElement.GetRawText(),
                                MinecraftJsonContext.Default.ListRule
                            );
                        }

                        if (root.TryGetProperty("value", out valueElement))
                        {
                            value = valueElement;
                        }

                        return new ObjectArgument(rules, value);
                    }

                    throw new JsonException("Invalid object argument structure");
                }

            default:
                throw new JsonException($"Unexpected token type: {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, Argument value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case StringArgument stringArg:
                writer.WriteStringValue(stringArg.Value);
                break;

            case ObjectArgument objectArg:
                writer.WriteStartObject();
                if (objectArg.Rules != null)
                {
                    writer.WritePropertyName("rules");
                    JsonSerializer.Serialize(
                        writer,
                        objectArg.Rules,
                        MinecraftJsonContext.Default.ListRule
                    );
                }

                writer.WritePropertyName("value");
                if (objectArg.Value.ValueKind == JsonValueKind.String)
                {
                    writer.WriteStringValue(objectArg.Value.GetString());
                }
                else if (objectArg.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var element in objectArg.Value.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                        {
                            writer.WriteStringValue(element.GetString());
                        }
                        else
                        {
                            element.WriteTo(writer);
                        }
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    objectArg.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
                break;
        }
    }
}