using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CryptoNotes.Server.Data;
using CryptoNotes.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CryptoNotes.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ServerDbContext _db;
        private static IConfiguration _config;

        // In-memory rate limiting: tracks login attempts per IP
        private static readonly ConcurrentDictionary<string, RateLimitEntry> _loginAttempts
            = new ConcurrentDictionary<string, RateLimitEntry>();

        public AuthController(ServerDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Rate limit registration
            if (IsRateLimited())
                return StatusCode(429, new { error = "Too many attempts. Try again later." });

            // Validate password strength
            int minPwdLen = _config.GetValue<int>("Security:MinPasswordLength", 8);
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length < minPwdLen)
                return BadRequest(new { error = $"Password must be at least {minPwdLen} characters" });

            // Validate username format
            if (!IsValidUsername(request.Username))
                return BadRequest(new { error = "Username must be 3-50 alphanumeric characters, hyphens, or underscores" });

            // Validate public key looks like a PGP key
            if (string.IsNullOrWhiteSpace(request.PublicKey) ||
                !request.PublicKey.Contains("BEGIN PGP PUBLIC KEY"))
                return BadRequest(new { error = "Invalid PGP public key format" });

            var existing = await _db.Users
                .FirstOrDefaultAsync(u => u.Username == request.Username);

            if (existing != null)
            {
                // Prevent username enumeration via timing: always hash a password
                BCrypt.Net.BCrypt.HashPassword("dummy-password-for-timing");
                return Conflict(new { error = "Username already taken" });
            }

            var user = new User
            {
                Username = request.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, 12),
                PublicKey = request.PublicKey,
                CreatedAt = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            var token = GenerateToken(user.Username);
            return Ok(new LoginResponse
            {
                Token = token,
                Username = user.Username
            });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Rate limit login attempts
            if (IsRateLimited())
                return StatusCode(429, new { error = "Too many login attempts. Try again later." });

            RecordAttempt();

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Username == request.Username);

            if (user == null)
            {
                // Prevent username enumeration: always verify a hash
                BCrypt.Net.BCrypt.Verify("dummy", BCrypt.Net.BCrypt.HashPassword("dummy"));
                return Unauthorized(new { error = "Invalid username or password" });
            }

            if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return Unauthorized(new { error = "Invalid username or password" });

            user.LastSeen = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var token = GenerateToken(user.Username);
            return Ok(new LoginResponse
            {
                Token = token,
                Username = user.Username
            });
        }

        private string GenerateToken(string username)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var payload = $"{username}:{timestamp}";
            using (var hmac = new HMACSHA256(GetTokenKey()))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                var signature = Convert.ToBase64String(hash);
                return Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{payload}:{signature}"));
            }
        }

        private static byte[] GetTokenKey()
        {
            var key = _config?.GetValue<string>("Security:TokenSigningKey");
            if (string.IsNullOrEmpty(key))
                throw new InvalidOperationException(
                    "Security:TokenSigningKey is not configured. Set it in appsettings.json.");
            return Encoding.UTF8.GetBytes(key);
        }

        internal static string ValidateToken(string token)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                var parts = decoded.Split(':');
                if (parts.Length != 3) return null;

                var username = parts[0];
                var timestamp = parts[1];
                var signature = parts[2];

                // Check expiry (configurable, default 24 hours)
                int expiryHours = _config?.GetValue<int>("Security:TokenExpiryHours", 24) ?? 24;
                if (long.TryParse(timestamp, out var ts))
                {
                    var issued = DateTimeOffset.FromUnixTimeSeconds(ts);
                    if (DateTimeOffset.UtcNow - issued > TimeSpan.FromHours(expiryHours))
                        return null;
                }
                else return null;

                // Constant-time signature verification
                var payload = $"{username}:{timestamp}";
                using (var hmac = new HMACSHA256(GetTokenKey()))
                {
                    var expectedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                    var expectedSig = Convert.ToBase64String(expectedHash);

                    // Constant-time comparison
                    if (expectedSig.Length != signature.Length) return null;
                    int diff = 0;
                    for (int i = 0; i < expectedSig.Length; i++)
                        diff |= expectedSig[i] ^ signature[i];
                    if (diff != 0) return null;
                }

                return username;
            }
            catch
            {
                return null;
            }
        }

        #region Rate Limiting

        private bool IsRateLimited()
        {
            var ip = GetClientIp();
            int maxAttempts = _config?.GetValue<int>("Security:MaxLoginAttemptsPerMinute", 5) ?? 5;

            if (_loginAttempts.TryGetValue(ip, out var entry))
            {
                // Clean up old entries
                if (DateTime.UtcNow - entry.WindowStart > TimeSpan.FromMinutes(1))
                {
                    entry.Count = 0;
                    entry.WindowStart = DateTime.UtcNow;
                    return false;
                }

                return entry.Count >= maxAttempts;
            }

            return false;
        }

        private void RecordAttempt()
        {
            var ip = GetClientIp();
            _loginAttempts.AddOrUpdate(ip,
                _ => new RateLimitEntry { Count = 1, WindowStart = DateTime.UtcNow },
                (_, existing) =>
                {
                    if (DateTime.UtcNow - existing.WindowStart > TimeSpan.FromMinutes(1))
                    {
                        existing.Count = 1;
                        existing.WindowStart = DateTime.UtcNow;
                    }
                    else
                    {
                        existing.Count++;
                    }
                    return existing;
                });
        }

        private string GetClientIp()
        {
            return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        private static bool IsValidUsername(string username)
        {
            if (string.IsNullOrEmpty(username) || username.Length < 3 || username.Length > 50)
                return false;
            foreach (var c in username)
            {
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                    return false;
            }
            return true;
        }

        private class RateLimitEntry
        {
            public int Count { get; set; }
            public DateTime WindowStart { get; set; }
        }

        #endregion
    }
}
