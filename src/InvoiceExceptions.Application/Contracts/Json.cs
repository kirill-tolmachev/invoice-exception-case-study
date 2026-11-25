using System.Text.Encodings.Web;
using System.Text.Json;

namespace InvoiceExceptions.Application.Contracts;

/// <summary>The one JSON convention of the solution: snake_case names, nulls written explicitly, decimals as strings.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Unexpected null instead of {typeof(T).Name}.");

    public static T Deserialize<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new JsonException($"Unexpected null instead of {typeof(T).Name}.");
}
