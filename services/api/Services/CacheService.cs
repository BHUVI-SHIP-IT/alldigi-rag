using System.Text.Json;
using StackExchange.Redis;

namespace RagBackend.Api.Services;

/// <summary>
/// Semantic cache backed by Redis.
///
/// Key design:
///  - Each entry is stored as a Redis Hash:  cache:{guid}  → { vector, answer, sources }
///  - A Redis Sorted Set  cache:index  tracks every live key, scored by its Unix expiry
///    timestamp. This means:
///      1. Expired entries are pruned from the index before every lookup (no scan needed).
///      2. The index never accumulates dead members — ZREMRANGEBYSCORE removes them atomically.
///      3. No separate SET is required; ZRANGEBYSCORE returns the live members.
/// </summary>
public class CacheService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<CacheService> _logger;

    // Sorted-set key that indexes all live cache entry keys, scored by expiry (Unix seconds).
    private const string IndexKey = "cache:index";

    public CacheService(IConnectionMultiplexer redis, ILogger<CacheService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private IDatabase Db => _redis.GetDatabase();

    /// <summary>
    /// Cosine similarity between two float vectors.
    /// Returns 0 if either vector is zero-length, all-zeros, or the lengths differ.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        // Fix #2: Guard against mismatched vector dimensions to prevent IndexOutOfRangeException.
        if (a.Length != b.Length || a.Length == 0)
            return 0f;

        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0f || normB == 0f) return 0f;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    public async Task<(string Answer, string[] Sources)?> GetCachedAnswerAsync(
        float[] queryVector, float threshold = 0.92f)
    {
        try
        {
            var db = Db;
            var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Fix #1: Prune entries that have already expired from the sorted-set index.
            // ZREMRANGEBYSCORE cache:index -inf <nowEpoch> removes all members whose score
            // (= expiry timestamp) is in the past, keeping the index clean atomically.
            await db.SortedSetRemoveRangeByScoreAsync(
                IndexKey,
                double.NegativeInfinity,
                nowEpoch - 1); // strictly before now

            // Retrieve all still-live keys (score > nowEpoch means they haven't expired yet).
            var liveEntries = await db.SortedSetRangeByScoreAsync(
                IndexKey,
                start: nowEpoch,
                stop: double.PositiveInfinity);

            foreach (var entry in liveEntries)
            {
                var key = entry.ToString();

                var vectorJson = await db.HashGetAsync(key, "vector");
                if (vectorJson.IsNullOrEmpty) continue;

                var storedVector = JsonSerializer.Deserialize<float[]>(vectorJson.ToString());
                if (storedVector is null) continue;

                var similarity = CosineSimilarity(queryVector, storedVector);
                if (similarity >= threshold)
                {
                    var answer  = await db.HashGetAsync(key, "answer");
                    var sources = await db.HashGetAsync(key, "sources");
                    if (answer.IsNullOrEmpty) continue;

                    var sourcesArr = sources.IsNullOrEmpty
                        ? Array.Empty<string>()
                        : JsonSerializer.Deserialize<string[]>(sources.ToString()) ?? Array.Empty<string>();

                    return (answer.ToString(), sourcesArr);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis error during cache lookup");
        }

        return null;
    }

    public async Task StoreCacheEntryAsync(
        float[] queryVector, string answer, string[] sources, int ttlSeconds)
    {
        try
        {
            var db = Db;
            var key = $"cache:{Guid.NewGuid()}";
            var expiryEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSeconds;

            await db.HashSetAsync(key, new HashEntry[]
            {
                new("vector",  JsonSerializer.Serialize(queryVector)),
                new("answer",  answer),
                new("sources", JsonSerializer.Serialize(sources))
            });

            // Set the hash's own TTL so Redis evicts it automatically.
            await db.KeyExpireAsync(key, TimeSpan.FromSeconds(ttlSeconds));

            // Fix #1: Register in the sorted-set index scored by expiry epoch.
            // When the score (expiry) is in the past, the next lookup prunes it via
            // ZREMRANGEBYSCORE — no orphaned index entries ever accumulate.
            await db.SortedSetAddAsync(IndexKey, key, expiryEpoch);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis error storing cache entry");
        }
    }

    public async Task InvalidateByFileNameAsync(string fileName)
    {
        try
        {
            var db = Db;
            var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Prune stale index entries before scanning.
            await db.SortedSetRemoveRangeByScoreAsync(IndexKey, double.NegativeInfinity, nowEpoch - 1);

            var liveEntries = await db.SortedSetRangeByScoreAsync(
                IndexKey,
                start: nowEpoch,
                stop: double.PositiveInfinity);

            foreach (var entry in liveEntries)
            {
                var key = entry.ToString();

                var sourcesJson = await db.HashGetAsync(key, "sources");
                if (sourcesJson.IsNullOrEmpty) continue;

                var entrySources = JsonSerializer.Deserialize<string[]>(sourcesJson.ToString());
                if (entrySources is null) continue;

                if (entrySources.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    await db.KeyDeleteAsync(key);
                    await db.SortedSetRemoveAsync(IndexKey, key);
                    _logger.LogInformation(
                        "Evicted cache entry {Key} referencing {FileName}", key, fileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Redis error during cache invalidation for {FileName}; continuing.", fileName);
        }
    }
}
