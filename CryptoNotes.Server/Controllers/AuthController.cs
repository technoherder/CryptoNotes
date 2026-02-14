using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CryptoNotes.Server.Data;
using CryptoNotes.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoNotes.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ServerDbContext _db;

        public AuthController(ServerDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Register a new user with their PGP public key.
        /// The server stores the public key so other users can discover it for E2E encryption.
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var existing = await _db.Users
                .FirstOrDefaultAsync(u => u.Username == request.Username);

            if (existing != null)
                return Conflict(new { error = "Username already taken" });

            var user = new User
            {
                Username = request.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
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

        /// <summary>
        /// Log in with username and password to obtain an auth token.
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Username == request.Username);

            if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
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

        /// <summary>
        /// Generate a simple HMAC-based token: username:timestamp:signature
        /// In production, use JWT with proper key management.
        /// </summary>
        private static string GenerateToken(string username)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var payload = $"{username}:{timestamp}";
            using (var hmac = new HMACSHA256(TokenKey))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                var signature = Convert.ToBase64String(hash);
                return Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{payload}:{signature}"));
            }
        }

        // Shared key for token signing. In production, load from secure config.
        internal static readonly byte[] TokenKey = Encoding.UTF8.GetBytes(
            "CryptoNotes-Server-Secret-Key-Change-In-Production-2024!");

        /// <summary>
        /// Validates a token and returns the username, or null if invalid.
        /// Tokens expire after 30 days.
        /// </summary>
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

                // Check expiry (30 days)
                if (long.TryParse(timestamp, out var ts))
                {
                    var issued = DateTimeOffset.FromUnixTimeSeconds(ts);
                    if (DateTimeOffset.UtcNow - issued > TimeSpan.FromDays(30))
                        return null;
                }
                else return null;

                // Verify signature
                var payload = $"{username}:{timestamp}";
                using (var hmac = new HMACSHA256(TokenKey))
                {
                    var expectedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                    var expectedSig = Convert.ToBase64String(expectedHash);
                    if (signature != expectedSig) return null;
                }

                return username;
            }
            catch
            {
                return null;
            }
        }
    }
}
