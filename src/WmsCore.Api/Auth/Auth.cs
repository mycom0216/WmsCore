using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WmsCore.Api.Data;
using WmsCore.Api.Domain;

namespace WmsCore.Api.Auth;

public class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "WmsCore";
    public int ExpireMinutes { get; set; } = 60;
}

/// <summary>PBKDF2-SHA256, 600,000회(OWASP 권장), 사용자마다 다른 솔트. 형식: iterations.salt.hash</summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iter)) return false;
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected); // 시간 차 공격 방지
    }
}

public record LoginRequest([Required] string UserName, [Required] string Password);

[ApiController]
[Route("api/auth")]
public class AuthController(WmsDbContext db, Microsoft.Extensions.Options.IOptions<JwtOptions> opt, TimeProvider clock) : ControllerBase
{
    // 존재하지 않는 계정에도 같은 비용의 해시 검증을 해서 "계정 존재 여부"가 응답 시간으로 새지 않게 한다.
    private static readonly string DummyHash = PasswordHasher.Hash("dummy-password");

    [HttpPost("login")]
    public async Task<ActionResult<object>> Login(LoginRequest req, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserName == req.UserName, ct);
        var ok = PasswordHasher.Verify(req.Password, user?.PasswordHash ?? DummyHash) && user is not null;
        if (!ok) return Problem(statusCode: 401, title: "로그인 실패", detail: "아이디 또는 비밀번호가 올바르지 않습니다.");

        var o = opt.Value;
        var expires = clock.GetUtcNow().UtcDateTime.AddMinutes(o.ExpireMinutes);
        var token = new JwtSecurityToken(
            o.Issuer, o.Issuer,
            [new Claim(ClaimTypes.Name, user!.UserName), new Claim(ClaimTypes.Role, user.Role)],
            expires: expires,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)), SecurityAlgorithms.HmacSha256));
        return new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), expiresAt = expires, role = user.Role };
    }
}
