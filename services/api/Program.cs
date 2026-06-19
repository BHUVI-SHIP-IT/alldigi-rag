using System.Text;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Qdrant.Client;
using StackExchange.Redis;
using RagBackend.Api.Data;
using RagBackend.Api.Middleware;
using RagBackend.Api.Models;
using RagBackend.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Controllers & JSON ──────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// ── Memory cache (for rate limiting) ─────────────────────────────────────────
builder.Services.AddMemoryCache();

// ── PostgreSQL + EF Core + ASP.NET Identity ──────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("Default")
    ?? builder.Configuration["ConnectionStrings:Default"]
    ?? throw new InvalidOperationException("Connection string not configured");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connStr));

builder.Services.AddIdentityCore<AppUser>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;
    options.Password.RequiredUniqueChars = 1;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// bcrypt cost factor ≥ 10 (ASP.NET Identity default IterationCount maps to bcrypt rounds)
builder.Services.Configure<PasswordHasherOptions>(o => o.IterationCount = 10);

// ── JWT Authentication ────────────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret not configured");
var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwtKey,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            NameClaimType = "email",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// ── Redis ─────────────────────────────────────────────────────────────────────
var redisConnStr = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisConnStr));

// ── MinIO (AWS S3 SDK) ────────────────────────────────────────────────────────
builder.Services.AddSingleton<IAmazonS3>(_ =>
{
    var endpoint = builder.Configuration["Minio:Endpoint"] ?? "minio:9000";
    var accessKey = builder.Configuration["Minio:AccessKey"] ?? "";
    var secretKey = builder.Configuration["Minio:SecretKey"] ?? "";
    var config = new AmazonS3Config
    {
        ServiceURL = $"http://{endpoint}",
        ForcePathStyle = true
    };
    return new AmazonS3Client(accessKey, secretKey, config);
});

// ── Qdrant ────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<QdrantClient>(_ =>
{
    var host = builder.Configuration["Qdrant:Host"] ?? "qdrant";
    var port = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6333");
    return new QdrantClient(host, port);
});

// ── HTTP Clients ──────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<EmbeddingClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Embedding:BaseUrl"] ?? "http://embedding:8000");
});

builder.Services.AddHttpClient<LlmClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Llm:BaseUrl"] ?? "http://llm:8081");
});

// ── Application Services ──────────────────────────────────────────────────────
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SeedService>();
builder.Services.AddScoped<MinioService>();
builder.Services.AddScoped<QdrantService>();
builder.Services.AddScoped<CacheService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<RagService>();
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionService>());

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Startup: migrate, seed, ensure infra ─────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    var seed = scope.ServiceProvider.GetRequiredService<SeedService>();
    await seed.SeedAsync();

    var minio = scope.ServiceProvider.GetRequiredService<MinioService>();
    await minio.EnsureBucketAsync();

    var qdrant = scope.ServiceProvider.GetRequiredService<QdrantService>();
    await qdrant.EnsureCollectionAsync();
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<InvalidJwtRateLimitMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

await app.RunAsync();
