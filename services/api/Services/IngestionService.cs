using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RagBackend.Api.Data;
using RagBackend.Api.Models;

namespace RagBackend.Api.Services;

public class IngestionService : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(IServiceScopeFactory scopeFactory, ILogger<IngestionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async ValueTask EnqueueAsync(Guid documentId)
        => await _queue.Writer.WriteAsync(documentId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var documentId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessDocumentAsync(documentId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unhandled error ingesting document {DocumentId}", documentId);
            }
        }
    }

    private async Task ProcessDocumentAsync(Guid documentId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var minio = scope.ServiceProvider.GetRequiredService<MinioService>();
        var embedding = scope.ServiceProvider.GetRequiredService<EmbeddingClient>();
        var qdrant = scope.ServiceProvider.GetRequiredService<QdrantService>();
        var cache = scope.ServiceProvider.GetRequiredService<CacheService>();

        var document = await db.Documents.FindAsync(new object[] { documentId }, ct);
        if (document is null)
        {
            _logger.LogWarning("Document {DocumentId} not found in DB; skipping.", documentId);
            return;
        }

        // ── 1. Mark processing ────────────────────────────────────────────
        document.Status = "processing";
        await db.SaveChangesAsync(ct);

        try
        {
            // ── 2. Fetch raw file from MinIO ──────────────────────────────
            var fileBase64 = await minio.GetFileBase64Async(documentId);

            // ── 3. Call embedding /process ────────────────────────────────
            var chunks = await embedding.ProcessAsync(fileBase64, document.FileName, ct);

            // ── 4. Transaction: insert chunks + update document ───────────
            //
            // Fix #4: Qdrant upserts are intentionally performed AFTER CommitAsync.
            // Qdrant is not transactional — if we upsert during the DB transaction and
            // the commit then fails, the DB rolls back but Qdrant retains the vectors,
            // creating orphaned points that return results with no matching DB record.
            // By collecting the upsert payloads first and writing to Qdrant only after
            // a successful commit, the two stores stay consistent.

            // Collect the upsert payloads we will push to Qdrant after commit.
            var qdrantPayloads = new List<(Guid PointId, float[] Vector, Dictionary<string, string> Payload)>(chunks.Count);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var chunkEntities = new List<Chunk>(chunks.Count);
                foreach (var c in chunks)
                {
                    var qdrantPointId = Guid.NewGuid();
                    var chunk = new Chunk
                    {
                        Id = Guid.NewGuid(),
                        DocumentId = documentId,
                        Ordinal = c.Ordinal,
                        Text = c.Text,
                        QdrantPointId = qdrantPointId
                    };
                    chunkEntities.Add(chunk);
                    db.Chunks.Add(chunk);

                    // Record the Qdrant payload — do NOT call Qdrant yet.
                    qdrantPayloads.Add((qdrantPointId, c.Vector, new Dictionary<string, string>
                    {
                        ["documentId"] = documentId.ToString(),
                        ["chunkId"]    = chunk.Id.ToString(),
                        ["text"]       = c.Text,
                        ["fileName"]   = document.FileName
                    }));
                }

                document.Status = "indexed";
                document.ChunkCount = chunkEntities.Count;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }

            // ── 4b. Push to Qdrant only after a successful DB commit ──────
            // Qdrant failures here are handled separately: the DB is already consistent so we
            // must NOT trigger the outer DB-cleanup block. Log the error and surface it so the
            // document status is set to "failed" by the outer catch, but skip chunk deletion.
            try
            {
                foreach (var (pointId, vector, payload) in qdrantPayloads)
                {
                    await qdrant.UpsertChunkAsync(pointId, vector, payload);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Qdrant upsert failed after DB commit for document {DocumentId}. "
                    + "DB is consistent; document marked failed so it can be re-ingested.",
                    documentId);

                // Update status to failed directly — do NOT fall through to the outer cleanup
                // which would delete the committed chunks.
                try
                {
                    using var qdrantFailScope = _scopeFactory.CreateScope();
                    var qdrantFailDb = qdrantFailScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var failDoc = await qdrantFailDb.Documents.FindAsync(new object[] { documentId });
                    if (failDoc is not null)
                    {
                        failDoc.Status = "failed";
                        await qdrantFailDb.SaveChangesAsync();
                    }
                }
                catch (Exception statusEx)
                {
                    _logger.LogError(statusEx,
                        "Failed to update document status to 'failed' after Qdrant error for {DocumentId}",
                        documentId);
                }

                return; // Exit without entering the outer DB-cleanup catch block.
            }

            // ── 5. Cache invalidation (before status write — status already written above) ──
            try
            {
                await cache.InvalidateByFileNameAsync(document.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Cache invalidation failed for document {DocumentId} ({FileName}); proceeding.",
                    documentId, document.FileName);
            }

            _logger.LogInformation(
                "Document {DocumentId} indexed with {Count} chunks.", documentId, chunks.Count);
        }
        catch (Exception ex)
        {
            // This catch handles pre-commit failures: MinIO download, embedding call, or DB
            // transaction failure. In all of these cases the DB may have partial state that
            // needs cleanup. Qdrant post-commit errors are handled separately above and never
            // reach this block (they call return early).
            _logger.LogError(ex, "Ingestion failed for document {DocumentId}", documentId);

            // Cleanup partial state
            try
            {
                using var cleanupScope = _scopeFactory.CreateScope();
                var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var doc = await cleanupDb.Documents.FindAsync(new object[] { documentId });
                if (doc is not null)
                {
                    var partialChunks = await cleanupDb.Chunks
                        .Where(c => c.DocumentId == documentId)
                        .ToListAsync();
                    cleanupDb.Chunks.RemoveRange(partialChunks);
                    doc.Status = "failed";
                    await cleanupDb.SaveChangesAsync();
                }
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx,
                    "Cleanup after ingestion failure also failed for {DocumentId}", documentId);
            }
        }
    }
}
