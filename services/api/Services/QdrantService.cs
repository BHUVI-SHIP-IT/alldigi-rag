using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace RagBackend.Api.Services;

public class QdrantService
{
    private readonly QdrantClient _client;
    private const string CollectionName = "documents";
    private const uint VectorSize = 768;

    public QdrantService(QdrantClient client)
    {
        _client = client;
    }

    public async Task EnsureCollectionAsync()
    {
        var collections = await _client.ListCollectionsAsync();
        if (!collections.Any(c => c == CollectionName))
        {
            await _client.CreateCollectionAsync(CollectionName, new VectorParams
            {
                Size = VectorSize,
                Distance = Distance.Cosine
            });
        }
    }

    public async Task UpsertChunkAsync(Guid pointId, float[] vector, Dictionary<string, string> payload)
    {
        var point = new PointStruct
        {
            Id = new PointId { Uuid = pointId.ToString() },
            Vectors = vector
        };

        foreach (var kvp in payload)
        {
            point.Payload[kvp.Key] = new Value { StringValue = kvp.Value };
        }

        await _client.UpsertAsync(CollectionName, new[] { point });
    }

    public async Task<List<ScoredPoint>> SearchAsync(float[] queryVector, int topK = 5)
    {
        var results = await _client.SearchAsync(
            CollectionName,
            queryVector,
            limit: (ulong)topK,
            payloadSelector: true
        );
        return results.ToList();
    }
}
