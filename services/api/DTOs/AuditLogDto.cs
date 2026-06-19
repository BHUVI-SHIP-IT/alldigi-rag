namespace RagBackend.Api.DTOs;

public class AuditLogDto
{
    public string Id { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string[] RetrievedSources { get; set; } = Array.Empty<string>();
    public string CreatedAt { get; set; } = string.Empty;
}
