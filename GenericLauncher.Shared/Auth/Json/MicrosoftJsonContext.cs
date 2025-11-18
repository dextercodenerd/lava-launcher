using System.Text.Json.Serialization;
using GenericLauncher.Auth.Json;

namespace GenericLauncher.Microsoft.Json;

[JsonSerializable(typeof(MicrosoftTokenResponse))]
[JsonSerializable(typeof(MinecraftAuthRequest))]
[JsonSerializable(typeof(MinecraftAuthResponse))]
[JsonSerializable(typeof(EntitlementsResponse))]
[JsonSerializable(typeof(EntitlementItem))]
[JsonSerializable(typeof(MinecraftProfile))]
[JsonSerializable(typeof(Skin))]
[JsonSerializable(typeof(Cape))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
internal partial class MicrosoftJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(XboxLiveAuthRequest))]
[JsonSerializable(typeof(XboxLiveAuthProperties))]
[JsonSerializable(typeof(XboxLiveAuthResponse))]
[JsonSerializable(typeof(DisplayClaims))]
[JsonSerializable(typeof(Xui))]
[JsonSerializable(typeof(XstsAuthRequest))]
[JsonSerializable(typeof(XstsAuthProperties))]
[JsonSerializable(typeof(XstsAuthResponse))]
[JsonSerializable(typeof(XstsAuthErrorResponse))]
[JsonSourceGenerationOptions(
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
internal partial class XboxLiveJsonContext : JsonSerializerContext;