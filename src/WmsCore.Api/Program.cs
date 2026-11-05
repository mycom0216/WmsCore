using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WmsCore.Api.Auth;
using WmsCore.Api.Common;
using WmsCore.Api.Data;
using WmsCore.Api.Domain;
using WmsCore.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// DB: 개발은 SQLite(설치 불필요), 운영은 SQL Server (Database:Provider 로 전환)
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connection = builder.Configuration.GetConnectionString("Default") ?? "Data Source=wms.db";
builder.Services.AddDbContext<WmsDbContext>(o =>
{
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)) o.UseSqlServer(connection);
    else o.UseSqlite(connection);
});

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new();
if (jwt.Key.Length < 32) throw new InvalidOperationException("Jwt:Key는 32자 이상이어야 합니다. 환경 변수 Jwt__Key로 지정하세요.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Issuer,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ClockSkew = TimeSpan.FromSeconds(30),
    });
builder.Services.AddAuthorization();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<StockService>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks().AddDbContextCheck<WmsDbContext>();

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment()) app.MapOpenApi(); // /openapi/v1.json
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (db.Database.IsSqlite()) await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;"); // 읽기와 쓰기가 서로 막지 않게
    await Seeder.SeedAsync(db, app.Configuration);
}

app.Run();

public static class Seeder
{
    /// <summary>데모 계정 생성. 운영에서는 Seed__AdminPassword 등 환경 변수로 바꾼다.</summary>
    public static async Task SeedAsync(WmsDbContext db, IConfiguration cfg)
    {
        if (await db.Users.AnyAsync()) return;
        db.Users.Add(new AppUser { UserName = "admin", Role = Roles.Admin, PasswordHash = PasswordHasher.Hash(cfg["Seed:AdminPassword"] ?? "Admin123!") });
        db.Users.Add(new AppUser { UserName = "operator", Role = Roles.Operator, PasswordHash = PasswordHasher.Hash(cfg["Seed:OperatorPassword"] ?? "Operator123!") });
        await db.SaveChangesAsync();
    }
}

public partial class Program;
