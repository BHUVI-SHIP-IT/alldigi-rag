using RagBackend.Api.Data;
using RagBackend.Api.Models;

namespace RagBackend.Api.Services;

public class AuditService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuditService> _logger;

    public AuditService(AppDbContext db, ILogger<AuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task WriteAuditLogAsync(
        string userEmail,
        string query,
        string[] retrievedSources,
        CancellationToken ct = default)
    {
        try
        {
            var log = new AuditLog
            {
                Id = Guid.NewGuid(),
                UserEmail = userEmail,
                Query = query,
                RetrievedSources = retrievedSources,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.AuditLogs.Add(log);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write audit log for user {Email}, query: {Query}",
                userEmail, query);
        }
    }
}
