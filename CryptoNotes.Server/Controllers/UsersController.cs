using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoNotes.Server.Data;
using CryptoNotes.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoNotes.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly ServerDbContext _db;

        public UsersController(ServerDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Search for users by username prefix. Returns public keys for E2E encryption.
        /// Requires authentication.
        /// </summary>
        [HttpGet("search")]
        public async Task<IActionResult> SearchUsers([FromQuery] string query)
        {
            var username = GetAuthenticatedUser();
            if (username == null)
                return Unauthorized(new { error = "Invalid or missing token" });

            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                return BadRequest(new { error = "Search query must be at least 2 characters" });

            var users = await _db.Users
                .Where(u => u.Username.Contains(query) && u.Username != username)
                .Select(u => new UserInfo
                {
                    Username = u.Username,
                    PublicKey = u.PublicKey
                })
                .Take(20)
                .ToListAsync();

            return Ok(users);
        }

        /// <summary>
        /// Get a specific user's public key by username.
        /// </summary>
        [HttpGet("{targetUsername}/publickey")]
        public async Task<IActionResult> GetPublicKey(string targetUsername)
        {
            var username = GetAuthenticatedUser();
            if (username == null)
                return Unauthorized(new { error = "Invalid or missing token" });

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Username == targetUsername);

            if (user == null)
                return NotFound(new { error = "User not found" });

            return Ok(new UserInfo
            {
                Username = user.Username,
                PublicKey = user.PublicKey
            });
        }

        private string GetAuthenticatedUser()
        {
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (authHeader == null || !authHeader.StartsWith("Bearer "))
                return null;

            var token = authHeader.Substring("Bearer ".Length);
            return AuthController.ValidateToken(token);
        }
    }
}
