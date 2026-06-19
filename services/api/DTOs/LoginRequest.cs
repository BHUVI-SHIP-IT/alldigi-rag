using System.ComponentModel.DataAnnotations;

namespace RagBackend.Api.DTOs;

public class LoginRequest
{
    [Required, EmailAddress, MaxLength(254)]
    public string Email { get; set; } = string.Empty;

    [Required, MinLength(8), MaxLength(72)]
    public string Password { get; set; } = string.Empty;
}
