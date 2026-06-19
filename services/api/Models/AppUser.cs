using Microsoft.AspNetCore.Identity;

namespace RagBackend.Api.Models;

public class AppUser : IdentityUser
{
    public string Role { get; set; } = "Employee";
}
