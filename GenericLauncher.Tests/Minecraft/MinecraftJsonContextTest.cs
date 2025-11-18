using System.IO;
using System.Text.Json;
using GenericLauncher.Minecraft.Json;
using JetBrains.Annotations;
using Xunit;

namespace GenericLauncher.Tests.Minecraft;

[TestSubject(typeof(MinecraftJsonContext))]
public class MinecraftJsonContextTest
{
    [Fact]
    public void Test_ParseMinecraftVersionDetailsJson()
    {
        var json = File.ReadAllText("../../../Data/client_1.21.10.json");
        var details = JsonSerializer.Deserialize(json, MinecraftJsonContext.Default.VersionDetails);

        _ = ArgumentsParser.FlattenArguments(details!.Arguments?.Game, "windows");
    }
}
