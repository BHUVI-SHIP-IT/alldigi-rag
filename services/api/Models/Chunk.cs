namespace RagBackend.Api.Models;

public class Chunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    public string Text { get; set; } = string.Empty;
    public Guid QdrantPointId { get; set; }

    // Navigation
    public Document? Document { get; set; }
}
