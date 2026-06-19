namespace RagBackend.Api.Models;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserEmail { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string[] RetrievedSources { get; set; } = Array.Empty<string>();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
