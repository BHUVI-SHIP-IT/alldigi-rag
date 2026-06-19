using System.ComponentModel.DataAnnotations;

namespace RagBackend.Api.DTOs;

public class QueryRequest
{
    [Required]
    public string? Question { get; set; }
}
