using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RagBackend.Api.Services;

namespace RagBackend.Api.Services;

public class RagService
{
    private readonly EmbeddingClient _embedding;
    private readonly CacheService _cache;
    private readonly QdrantService _qdrant;
    private readonly LlmClient _llm;
    private readonly AuditService _audit;
    private readonly IConfiguration _config;
    private readonly ILogger<RagService> _logger;

    public RagService(
        EmbeddingClient embedding,
        CacheService cache,
        QdrantService qdrant,
        LlmClient llm,
        AuditService audit,
        IConfiguration config,
        ILogger<RagService> logger)
    {
        _embedding = embedding;
        _cache = cache;
        _qdrant = qdrant;
        _llm = llm;
        _audit = audit;
        _config = config;
        _logger = logger;
    }

    public async Task ExecuteQueryAsync(
        string question,
        string userEmail,
        HttpResponse response,
        CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        // 1. Embed question
        float[] queryVector;
        try
        {
            queryVector = await _embedding.EmbedQueryAsync(question, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding failed for question from {User}", userEmail);
            throw new InvalidOperationException("Embedding service unavailable", ex);
        }

        // 2. Cache check
        var ttlDays = int.Parse(_config["Cache:TtlDays"] ?? "7");
        var cached = await _cache.GetCachedAnswerAsync(queryVector);
        if (cached.HasValue)
        {
            var (cachedAnswer, cachedSources) = cached.Value;
            await WriteSseAsync(response, "sources", JsonSerializer.Serialize(cachedSources), ct);
            foreach (var word in cachedAnswer.Split(' '))
            {
                await WriteSseDataAsync(response, JsonSerializer.Serialize(new { token = word + " " }), ct);
            }
            await WriteSseDataAsync(response, "[DONE]", ct);
            await _audit.WriteAuditLogAsync(userEmail, question, cachedSources, ct);
            await response.Body.FlushAsync(ct);
            return;
        }

        // 3. Qdrant search
        var searchResults = await _qdrant.SearchAsync(queryVector, topK: 5);
        var chunkTexts = new List<string>();
        var sourceSet = new LinkedList<string>();

        foreach (var result in searchResults)
        {
            if (result.Payload.TryGetValue("text", out var textVal))
                chunkTexts.Add(textVal.StringValue);
            if (result.Payload.TryGetValue("fileName", out var fnVal))
            {
                var fn = fnVal.StringValue;
                if (!sourceSet.Contains(fn))
                    sourceSet.AddLast(fn);
            }
        }

        var sources = sourceSet.ToArray();

        // 4. Emit sources event
        await WriteSseAsync(response, "sources", JsonSerializer.Serialize(sources), ct);

        // 5. Build prompt
        var sb = new StringBuilder();
        sb.AppendLine("You are a helpful assistant. Answer the question based only on the following context:");
        sb.AppendLine();
        foreach (var text in chunkTexts)
        {
            sb.AppendLine(text);
            sb.AppendLine("---");
        }
        sb.AppendLine();
        sb.AppendLine($"Question: {question}");
        var prompt = sb.ToString();

        // 6. Stream LLM response
        var fullAnswer = new StringBuilder();
        try
        {
            await foreach (var token in _llm.StreamCompletionAsync(prompt, ct))
            {
                fullAnswer.Append(token);
                await WriteSseDataAsync(response, JsonSerializer.Serialize(new { token }), ct);
                await response.Body.FlushAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM streaming failed for user {User}", userEmail);
            throw new InvalidOperationException("Language model service unavailable", ex);
        }

        // 7. Send [DONE]
        await WriteSseDataAsync(response, "[DONE]", ct);
        await response.Body.FlushAsync(ct);

        // 8. Store in cache
        await _cache.StoreCacheEntryAsync(
            queryVector,
            fullAnswer.ToString(),
            sources,
            ttlDays * 86400);

        // 9. Audit log
        await _audit.WriteAuditLogAsync(userEmail, question, sources, ct);
    }

    private static async Task WriteSseAsync(HttpResponse res, string eventName, string data, CancellationToken ct)
    {
        await res.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
        await res.Body.FlushAsync(ct);
    }

    private static async Task WriteSseDataAsync(HttpResponse res, string data, CancellationToken ct)
    {
        await res.WriteAsync($"data: {data}\n\n", ct);
    }
}
