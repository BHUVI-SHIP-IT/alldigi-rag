using Microsoft.AspNetCore.Identity;
using RagBackend.Api.Models;

namespace RagBackend.Api.Data;

public class SeedService
{
    private readonly UserManager<AppUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SeedService> _logger;

    public SeedService(
        UserManager<AppUser> userManager,
        IConfiguration configuration,
        ILogger<SeedService> logger)
    {
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SeedAsync()
    {
        var adminEmail = _configuration["Seed:AdminEmail"];
        var adminPassword = _configuration["Seed:AdminPassword"];

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            _logger.LogInformation("No admin seed credentials configured; skipping seed.");
            return;
        }

        var existing = await _userManager.FindByEmailAsync(adminEmail);
        if (existing is not null)
        {
            _logger.LogInformation("Admin user {Email} already exists; skipping seed.", adminEmail);
            return;
        }

        var adminUser = new AppUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = true,
            Role = "Admin"
        };

        var result = await _userManager.CreateAsync(adminUser, adminPassword);
        if (result.Succeeded)
        {
            _logger.LogInformation("Admin user {Email} created successfully.", adminEmail);
        }
        else
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogError("Failed to create admin user {Email}: {Errors}", adminEmail, errors);
            throw new InvalidOperationException($"Admin seed failed: {errors}");
        }
    }
}
