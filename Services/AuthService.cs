using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using RepairPartsPro.Models;

namespace RepairPartsPro.Services;

public sealed class AuthService
{
    private readonly string _jwtKey;
    private readonly string _jwtIssuer;
    private readonly string _jwtAudience;
    private readonly int _jwtExpiryDays;

    public AuthService(IConfiguration configuration)
    {
        _jwtKey = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is required. Set it via environment variable or user secrets.");
        _jwtIssuer = configuration["Jwt:Issuer"] ?? "RepairPartsPro";
        _jwtAudience = configuration["Jwt:Audience"] ?? "RepairPartsProUsers";
        _jwtExpiryDays = int.TryParse(configuration["Jwt:ExpiryDays"], out var days) ? days : 7;

        if (_jwtKey.Length < 32)
            throw new InvalidOperationException("Jwt:Key must be at least 32 characters for HMAC-SHA256.");
    }

    public string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);

        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('.', 2);
        if (parts.Length != 2) return false;

        byte[] salt, expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expectedHash = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public string GenerateToken(AppUser user, bool rememberMe = false)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiryDays = rememberMe ? 30 : _jwtExpiryDays;

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _jwtIssuer,
            audience: _jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(expiryDays),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static (bool Valid, string Error) ValidateRegistrationInput(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || !email.Contains('.'))
            return (false, "A valid email address is required.");

        if (email.Length > 254)
            return (false, "Email address is too long.");

        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return (false, "Password must be at least 8 characters.");

        if (password.Length > 128)
            return (false, "Password must be 128 characters or fewer.");

        return (true, string.Empty);
    }
}
