using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RagBackend.Api.Services;

public class LlmClient
{
    private readonly HttpClient _http;

    public LlmClient(HttpClient http)
    {
        _http = http;
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "local-model",
                messages = new[] { new { role = "user", content = prompt } },
                stream = true
            })
        };

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"LLM service error ({(int)response.StatusCode}): {err}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:")) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") yield break;

            string? token = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                token = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("delta")
                    .TryGetProperty("content", out var contentEl)
                        ? contentEl.GetString()
                        : null;
            }
            catch (JsonException)
            {
                // Skip malformed SSE lines
            }

            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }
}
