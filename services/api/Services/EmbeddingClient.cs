using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RagBackend.Api.Services;

public record ChunkDto(int Ordinal, string Text, float[] Vector);

public class EmbeddingClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public EmbeddingClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<ChunkDto>> ProcessAsync(string fileBase64, string fileName, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120)); // large file timeout

        var body = new { file_b64 = fileBase64, filename = fileName };
        using var response = await _http.PostAsJsonAsync("/process", body, JsonOpts, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Embedding /process failed ({(int)response.StatusCode}): {err}");
        }

        var result = await response.Content.ReadFromJsonAsync<ProcessResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty response from embedding /process");

        return result.Chunks;
    }

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var body = new { text };
        using var response = await _http.PostAsJsonAsync("/embed/query", body, JsonOpts, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Embedding /embed/query failed ({(int)response.StatusCode}): {err}");
        }

        var result = await response.Content.ReadFromJsonAsync<EmbedQueryResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty response from embedding /embed/query");

        return result.Vector;
    }

    private record ProcessResponse([property: JsonPropertyName("chunks")] List<ChunkDto> Chunks);
    private record EmbedQueryResponse([property: JsonPropertyName("vector")] float[] Vector);
}
