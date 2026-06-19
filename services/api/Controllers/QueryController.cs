using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RagBackend.Api.DTOs;
using RagBackend.Api.Services;

namespace RagBackend.Api.Controllers;

[ApiController]
[Route("api/query")]
[Authorize]
public class QueryController : ControllerBase
{
    private readonly RagService _rag;
    private readonly ILogger<QueryController> _logger;

    public QueryController(RagService rag, ILogger<QueryController> logger)
    {
        _rag = rag;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Query([FromBody] QueryRequest request)
    {
        // Role check — only Employee
        var role = User.FindFirst("role")?.Value;
        if (role != "Employee")
            return Forbid();

        var question = request.Question?.Trim();

        if (string.IsNullOrEmpty(question))
            return BadRequest(new { error = "question is required and must be non-empty" });

        if (question.Length > 2000)
            return BadRequest(new { error = "question must not exceed 2000 characters" });

        var userEmail = User.FindFirst("email")?.Value ?? "unknown";

        try
        {
            await _rag.ExecuteQueryAsync(question, userEmail, Response, HttpContext.RequestAborted);
            return new EmptyResult();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Embedding service"))
        {
            return StatusCode(502, new { error = "Embedding service unavailable" });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Language model"))
        {
            return StatusCode(502, new { error = "Language model service unavailable" });
        }
    }
}
