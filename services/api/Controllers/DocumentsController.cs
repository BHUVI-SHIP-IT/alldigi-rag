using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagBackend.Api.Data;
using RagBackend.Api.DTOs;
using RagBackend.Api.Models;
using RagBackend.Api.Services;

namespace RagBackend.Api.Controllers;

[ApiController]
[Route("api/documents")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx" };
    private const long MaxFileSizeBytes = 50L * 1024 * 1024; // 50 MB

    private readonly AppDbContext _db;
    private readonly MinioService _minio;
    private readonly IngestionService _ingestion;

    public DocumentsController(
        AppDbContext db,
        MinioService minio,
        IngestionService ingestion)
    {
        _db = db;
        _minio = minio;
        _ingestion = ingestion;
    }

    [HttpPost]
    public async Task<IActionResult> Upload(IFormFile? file)
    {
        // Role check — only Admin
        var role = User.FindFirst("role")?.Value;
        if (role != "Admin")
            return Forbid();

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        // Size check
        if (file.Length > MaxFileSizeBytes)
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge,
                new { error = "File size exceeds the 50 MB limit." });

        // Extension check
        var ext = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(ext))
            return UnprocessableEntity(new
            {
                error = $"Unsupported file type '{ext}'. Supported: .pdf, .docx"
            });

        var documentId = Guid.NewGuid();
        var uploaderId = User.FindFirst("sub")?.Value
            ?? User.FindFirst("email")?.Value
            ?? string.Empty;

        // Resolve user ID from email if needed
        var emailClaim = User.FindFirst("email")?.Value ?? string.Empty;
        var userEntity = await _db.Users
            .FirstOrDefaultAsync(u => u.Email == emailClaim);
        var resolvedUploaderId = userEntity?.Id ?? uploaderId;

        // Upload to MinIO
        await using var stream = file.OpenReadStream();
        await _minio.UploadAsync(documentId, stream, file.ContentType ?? "application/octet-stream");

        // Create document record
        var document = new Document
        {
            Id = documentId,
            FileName = file.FileName,
            UploaderId = resolvedUploaderId,
            Status = "queued",
            CreatedAt = DateTimeOffset.UtcNow,
            ChunkCount = 0
        };
        _db.Documents.Add(document);
        await _db.SaveChangesAsync();

        // Enqueue for background processing
        await _ingestion.EnqueueAsync(documentId);

        return StatusCode(StatusCodes.Status201Created, new UploadResponse
        {
            DocumentId = documentId.ToString()
        });
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        // Role check — only Admin
        var role = User.FindFirst("role")?.Value;
        if (role != "Admin")
            return Forbid();

        // Fix #3: Use a JOIN between Documents and Users so EF Core generates a single
        // SQL query instead of a separate sub-query per document row (N+1 problem).
        var records = await (
            from d in _db.Documents
            join u in _db.Users on d.UploaderId equals u.Id into uploaderJoin
            from uploader in uploaderJoin.DefaultIfEmpty()   // LEFT JOIN — handle missing users
            orderby d.CreatedAt descending
            select new DocumentRecord
            {
                Id         = d.Id.ToString(),
                FileName   = d.FileName,
                Uploader   = uploader != null ? (uploader.Email ?? string.Empty) : string.Empty,
                Status     = d.Status,
                CreatedAt  = d.CreatedAt.ToString("O"),
                ChunkCount = d.ChunkCount
            }
        ).ToListAsync();

        return Ok(records);
    }
}
