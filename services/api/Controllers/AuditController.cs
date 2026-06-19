using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagBackend.Api.Data;
using RagBackend.Api.DTOs;

namespace RagBackend.Api.Controllers;

[ApiController]
[Route("api/audit")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuditController> _logger;

    public AuditController(AppDbContext db, ILogger<AuditController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        // Role check — only Admin
        var role = User.FindFirst("role")?.Value;
        if (role != "Admin")
            return Forbid();

        try
        {
            var logs = await _db.AuditLogs
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => new AuditLogDto
                {
                    Id = l.Id.ToString(),
                    UserEmail = l.UserEmail,
                    Query = l.Query,
                    RetrievedSources = l.RetrievedSources,
                    CreatedAt = l.CreatedAt.ToString("O")
                })
                .ToListAsync();

            return Ok(logs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch audit logs");
            return StatusCode(503, new { error = "Audit log temporarily unavailable" });
        }
    }
}
