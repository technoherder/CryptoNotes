using System;
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
    public class MessagesController : ControllerBase
    {
        private readonly ServerDbContext _db;

        public MessagesController(ServerDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Send an encrypted message. The server only stores the PGP-encrypted blob.
        /// It never has access to the plaintext.
        /// </summary>
        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            var senderUsername = GetAuthenticatedUser();
            if (senderUsername == null)
                return Unauthorized(new { error = "Invalid or missing token" });

            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Verify recipient exists
            var recipient = await _db.Users
                .FirstOrDefaultAsync(u => u.Username == request.RecipientUsername);

            if (recipient == null)
                return NotFound(new { error = "Recipient not found" });

            var message = new Message
            {
                SenderUsername = senderUsername,
                RecipientUsername = request.RecipientUsername,
                EncryptedContent = request.EncryptedContent,
                SentAt = DateTime.UtcNow,
                Delivered = false
            };

            _db.Messages.Add(message);
            await _db.SaveChangesAsync();

            return Ok(new { id = message.Id, sentAt = message.SentAt.ToString("o") });
        }

        /// <summary>
        /// Fetch undelivered messages for the authenticated user.
        /// Messages are marked as delivered after fetching.
        /// </summary>
        [HttpGet("receive")]
        public async Task<IActionResult> ReceiveMessages()
        {
            var username = GetAuthenticatedUser();
            if (username == null)
                return Unauthorized(new { error = "Invalid or missing token" });

            var messages = await _db.Messages
                .Where(m => m.RecipientUsername == username && !m.Delivered)
                .OrderBy(m => m.SentAt)
                .ToListAsync();

            // Mark as delivered
            foreach (var msg in messages)
                msg.Delivered = true;

            await _db.SaveChangesAsync();

            var response = messages.Select(m => new MessageResponse
            {
                Id = m.Id,
                SenderUsername = m.SenderUsername,
                EncryptedContent = m.EncryptedContent,
                SentAt = m.SentAt.ToString("o")
            }).ToList();

            return Ok(response);
        }

        /// <summary>
        /// Get conversation history with a specific user.
        /// Returns both sent and received encrypted messages.
        /// </summary>
        [HttpGet("conversation/{otherUsername}")]
        public async Task<IActionResult> GetConversation(
            string otherUsername,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 50)
        {
            var username = GetAuthenticatedUser();
            if (username == null)
                return Unauthorized(new { error = "Invalid or missing token" });

            var messages = await _db.Messages
                .Where(m =>
                    (m.SenderUsername == username && m.RecipientUsername == otherUsername) ||
                    (m.SenderUsername == otherUsername && m.RecipientUsername == username))
                .OrderByDescending(m => m.SentAt)
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Mark received messages as delivered
            foreach (var msg in messages.Where(m => m.RecipientUsername == username && !m.Delivered))
                msg.Delivered = true;

            await _db.SaveChangesAsync();

            var response = messages
                .OrderBy(m => m.SentAt)
                .Select(m => new MessageResponse
                {
                    Id = m.Id,
                    SenderUsername = m.SenderUsername,
                    EncryptedContent = m.EncryptedContent,
                    SentAt = m.SentAt.ToString("o")
                }).ToList();

            return Ok(response);
        }

        /// <summary>
        /// Get list of users the authenticated user has conversations with.
        /// </summary>
        [HttpGet("conversations")]
        public async Task<IActionResult> GetConversations()
        {
            var username = GetAuthenticatedUser();
            if (username == null)
                return Unauthorized(new { error = "Invalid or missing token" });

            var sentTo = await _db.Messages
                .Where(m => m.SenderUsername == username)
                .Select(m => m.RecipientUsername)
                .Distinct()
                .ToListAsync();

            var receivedFrom = await _db.Messages
                .Where(m => m.RecipientUsername == username)
                .Select(m => m.SenderUsername)
                .Distinct()
                .ToListAsync();

            var allContacts = sentTo.Union(receivedFrom).Distinct().ToList();

            // Get unread counts per contact
            var result = new List<object>();
            foreach (var contact in allContacts)
            {
                var unreadCount = await _db.Messages
                    .CountAsync(m => m.SenderUsername == contact
                        && m.RecipientUsername == username
                        && !m.Delivered);

                var lastMessage = await _db.Messages
                    .Where(m =>
                        (m.SenderUsername == username && m.RecipientUsername == contact) ||
                        (m.SenderUsername == contact && m.RecipientUsername == username))
                    .OrderByDescending(m => m.SentAt)
                    .FirstOrDefaultAsync();

                result.Add(new
                {
                    username = contact,
                    unreadCount,
                    lastMessageAt = lastMessage?.SentAt.ToString("o")
                });
            }

            return Ok(result.OrderByDescending(r =>
                ((dynamic)r).lastMessageAt));
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
