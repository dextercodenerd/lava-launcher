using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GenericLauncher.Misc;

namespace GenericLauncher.Minecraft.Json;

public static class ArgumentsParser
{
    public static List<string> FlattenArguments(List<JsonElement>? arguments, LauncherPlatform platform)
    {
        var result = new List<string>();

        if (arguments is null || arguments.Count == 0)
        {
            return result;
        }

        foreach (var argElement in arguments)
        {
            Argument argument = argElement.ValueKind switch
            {
                JsonValueKind.String => new StringArgument(argElement.GetString()!),
                JsonValueKind.Object => DeserializeObjectArgument(argElement),
                _ => throw new InvalidOperationException($"Unsupported argument type: {argElement.ValueKind}"),
            };

            switch (argument)
            {
                case StringArgument strArg:
                    result.Add(strArg.Value);
                    break;

                case ObjectArgument objArg:
                    if (IsRuleAllowed(objArg.Rules, platform))
                    {
                        FlattenArgumentValue(objArg.Value, result);
                    }

                    break;
            }
        }

        return result;
    }

    private static void FlattenArgumentValue(JsonElement value, List<string> result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                result.Add(value.GetString() ?? "");
                break;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        result.Add(item.GetString() ?? "");
                    }
                    else
                    {
                        throw new JsonException($"Unsupported array element type: {item.ValueKind}");
                    }
                }

                break;

            default:
                throw new JsonException($"Unsupported value type: {value.ValueKind}");
        }
    }

    private static ObjectArgument DeserializeObjectArgument(JsonElement element)
    {
        List<Rule>? rules = null;
        if (element.TryGetProperty("rules", out var rulesElement))
        {
            rules = JsonSerializer.Deserialize(
                rulesElement.GetRawText(),
                MinecraftJsonContext.Default.ListRule
            );
        }

        if (!element.TryGetProperty("value", out var valueElement))
        {
            throw new JsonException("Object argument missing 'value' property");
        }

        return new ObjectArgument(rules, valueElement);
    }

    internal static bool IsRuleAllowed(List<Rule>? rules, LauncherPlatform platform)
    {
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = rules.All(r => !string.Equals(r.Action, "allow", StringComparison.Ordinal));
        foreach (var rule in rules)
        {
            if (!IsSingleRuleTargetMatch(rule, platform))
            {
                continue;
            }

            allowed = rule.Action == "allow";
        }

        return allowed;
    }

    private static bool IsSingleRuleTargetMatch(Rule rule, LauncherPlatform platform)
    {
        // OS-based rules
        if (rule.Os != null && !platform.MatchesOs(rule.Os))
        {
            return false;
        }

        // Feature-based rules e.g., quick play
        if (rule.Features != null)
        {
            foreach (var feature in rule.Features)
            {
                // TODO: In a real implementation, you'd check if the feature is available.
                //  This should come from your feature tracking.
                var featureAvailable = false;
                if (feature.Value != featureAvailable)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
