namespace RagBackend.Api.DTOs;

public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public UserInfo User { get; set; } = new();
}

public class UserInfo
{
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
