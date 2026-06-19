using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using RagBackend.Api.Models;

namespace RagBackend.Api.Services;

public class TokenService
{
    private readonly string _secret;
    private readonly int _expiryMinutes;

    public TokenService(IConfiguration configuration)
    {
        _secret = configuration["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured");
        _expiryMinutes = int.Parse(configuration["Jwt:ExpiryMinutes"] ?? "480");
    }

    public string GenerateToken(AppUser user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now = DateTimeOffset.UtcNow;
        var iat = now.ToUnixTimeSeconds();
        var exp = iat + (_expiryMinutes * 60);

        var claims = new[]
        {
            new Claim("email", user.Email ?? string.Empty),
            new Claim("role", user.Role),
            new Claim(JwtRegisteredClaimNames.Iat, iat.ToString(), ClaimValueTypes.Integer64),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
