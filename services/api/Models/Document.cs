namespace RagBackend.Api.Models;

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string UploaderId { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int ChunkCount { get; set; }

    // Navigation
    public AppUser? Uploader { get; set; }
    public ICollection<Chunk> Chunks { get; set; } = new List<Chunk>();
}
