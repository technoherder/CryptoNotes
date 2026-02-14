using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CryptoNotes.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CryptoNotes.Server.Controllers
{
    [Route("admin")]
    public class AdminController : Controller
    {
        private readonly ServerDbContext _db;
        private readonly IConfiguration _config;
        private static readonly DateTime ServerStartTime = DateTime.UtcNow;

        public AdminController(ServerDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        /// <summary>
        /// Checks whether the connecting IP is allowed to access the admin panel.
        /// Localhost (127.0.0.1 / ::1) is always permitted.
        /// Additional IPs can be allowlisted in appsettings.json under Admin:AllowedIPs.
        /// CIDR notation is supported (e.g. "192.168.1.0/24").
        /// </summary>
        private bool IsAllowedIp()
        {
            var remote = HttpContext.Connection.RemoteIpAddress;
            if (remote == null) return false;

            // Normalize IPv6-mapped IPv4
            var checkIp = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;

            // Localhost is always allowed
            if (IPAddress.IsLoopback(checkIp)) return true;

            // Check configured allowlist
            var allowedEntries = _config.GetSection("Admin:AllowedIPs").Get<string[]>();
            if (allowedEntries == null || allowedEntries.Length == 0)
                return false;

            foreach (var entry in allowedEntries)
            {
                var trimmed = entry?.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // CIDR notation (e.g. "10.0.0.0/8")
                if (trimmed.Contains("/"))
                {
                    if (IsInCidr(checkIp, trimmed))
                        return true;
                }
                else
                {
                    // Exact IP match
                    if (IPAddress.TryParse(trimmed, out var allowed) && checkIp.Equals(allowed))
                        return true;
                }
            }

            return false;
        }

        private static bool IsInCidr(IPAddress ip, string cidr)
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2) return false;
            if (!IPAddress.TryParse(parts[0], out var network)) return false;
            if (!int.TryParse(parts[1], out var prefixLen)) return false;

            var networkBytes = network.GetAddressBytes();
            var ipBytes = ip.GetAddressBytes();
            if (networkBytes.Length != ipBytes.Length) return false;

            int fullBytes = prefixLen / 8;
            int remainingBits = prefixLen % 8;

            for (int i = 0; i < fullBytes && i < networkBytes.Length; i++)
            {
                if (networkBytes[i] != ipBytes[i]) return false;
            }

            if (remainingBits > 0 && fullBytes < networkBytes.Length)
            {
                int mask = 0xFF << (8 - remainingBits);
                if ((networkBytes[fullBytes] & mask) != (ipBytes[fullBytes] & mask))
                    return false;
            }

            return true;
        }

        [HttpGet]
        [Produces("text/html")]
        public async Task<IActionResult> Index()
        {
            if (!IsAllowedIp())
            {
                var remote = HttpContext.Connection.RemoteIpAddress;
                return StatusCode(403,
                    $"Forbidden: Your IP ({remote}) is not in the admin allowlist.\n" +
                    "Add your IP to Admin:AllowedIPs in appsettings.json to gain access.");
            }

            var totalUsers = await _db.Users.CountAsync();
            var totalMessages = await _db.Messages.CountAsync();
            var deliveredMessages = await _db.Messages.CountAsync(m => m.Delivered);
            var pendingMessages = totalMessages - deliveredMessages;

            var uptime = DateTime.UtcNow - ServerStartTime;
            var uptimeStr = $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";

            var recentUsers = await _db.Users
                .OrderByDescending(u => u.CreatedAt)
                .Take(10)
                .Select(u => new { u.Username, u.CreatedAt, u.LastSeen })
                .ToListAsync();

            var messagesToday = await _db.Messages
                .CountAsync(m => m.SentAt >= DateTime.UtcNow.Date);

            var messagesThisWeek = await _db.Messages
                .CountAsync(m => m.SentAt >= DateTime.UtcNow.AddDays(-7));

            var activeConversations = await _db.Messages
                .Where(m => m.SentAt >= DateTime.UtcNow.AddDays(-7))
                .Select(m => new { m.SenderUsername, m.RecipientUsername })
                .Distinct()
                .CountAsync();

            var expiryDays = _config.GetValue<int>("Security:MessageExpiryDays", 30);
            var maxBodySize = _config.GetValue<long>("Security:MaxMessageSizeBytes", 65536);

            // Build recent users rows
            var userRows = "";
            foreach (var u in recentUsers)
            {
                var lastSeen = (DateTime.UtcNow - u.LastSeen).TotalMinutes < 5
                    ? "<span class='online'>ONLINE</span>"
                    : u.LastSeen.ToString("yyyy-MM-dd HH:mm") + " UTC";
                userRows += $@"
                    <tr>
                        <td>{WebUtility.HtmlEncode(u.Username)}</td>
                        <td>{u.CreatedAt:yyyy-MM-dd HH:mm} UTC</td>
                        <td>{lastSeen}</td>
                    </tr>";
            }

            var html = $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1'>
    <title>CryptoNotes Admin</title>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{
            background: #0a0a0a; color: #33ff33;
            font-family: 'Courier New', monospace; font-size: 14px;
            padding: 20px; line-height: 1.6;
        }}
        .header {{
            border-bottom: 1px solid #1a8c1a; padding-bottom: 10px; margin-bottom: 20px;
        }}
        .header pre {{
            color: #33ff33; font-size: 11px; line-height: 1.2;
        }}
        .header .sub {{ color: #1a8c1a; font-size: 12px; margin-top: 5px; }}
        .section {{
            border: 1px solid #1a8c1a; margin-bottom: 15px; padding: 12px;
            background: #111111;
        }}
        .section-title {{
            color: #ffb000; font-size: 14px; margin-bottom: 8px;
        }}
        .stat-grid {{
            display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 10px;
        }}
        .stat {{
            border: 1px solid #1a1a1a; padding: 10px; background: #0a0a0a;
        }}
        .stat .label {{ color: #1a8c1a; font-size: 12px; }}
        .stat .value {{ color: #33ff33; font-size: 22px; margin-top: 4px; }}
        .stat .value.amber {{ color: #ffb000; }}
        .stat .value.cyan {{ color: #00e5ff; }}
        .stat .value.red {{ color: #ff3333; }}
        table {{
            width: 100%; border-collapse: collapse; margin-top: 8px;
        }}
        th {{
            text-align: left; color: #ffb000; border-bottom: 1px solid #1a8c1a;
            padding: 4px 8px; font-size: 12px;
        }}
        td {{
            padding: 4px 8px; color: #cccccc; border-bottom: 1px solid #1a1a1a;
            font-size: 13px;
        }}
        .online {{ color: #33ff33; }}
        .warn {{ color: #ff3333; font-size: 12px; margin-top: 10px; }}
        .footer {{
            margin-top: 20px; color: #555555; font-size: 11px;
            border-top: 1px solid #1a1a1a; padding-top: 10px;
        }}
        .refresh {{ color: #1a8c1a; text-decoration: none; }}
        .refresh:hover {{ color: #33ff33; }}
    </style>
</head>
<body>
    <div class='header'>
        <pre>
  ___               _        _  _     _
 / __|_ _ _  _ _ __| |_ ___ | \| |___| |_ ___ ___
| (__| '_| || | '_ \  _/ _ \| .` / _ \  _/ -_|_-&lt;
 \___|_|  \_, | .__/\__\___/|_|\_\___/\__\___/__/
          |__/|_|</pre>
        <div class='sub'>*** ADMIN CONSOLE *** | IP allowlisted | <a class='refresh' href='/admin'>/refresh</a></div>
    </div>

    <div class='section'>
        <div class='section-title'>*** Server Overview</div>
        <div class='stat-grid'>
            <div class='stat'>
                <div class='label'>UPTIME</div>
                <div class='value amber'>{uptimeStr}</div>
            </div>
            <div class='stat'>
                <div class='label'>REGISTERED USERS</div>
                <div class='value'>{totalUsers}</div>
            </div>
            <div class='stat'>
                <div class='label'>TOTAL MESSAGES</div>
                <div class='value cyan'>{totalMessages}</div>
            </div>
            <div class='stat'>
                <div class='label'>PENDING DELIVERY</div>
                <div class='value red'>{pendingMessages}</div>
            </div>
        </div>
    </div>

    <div class='section'>
        <div class='section-title'>*** Message Activity</div>
        <div class='stat-grid'>
            <div class='stat'>
                <div class='label'>MESSAGES TODAY</div>
                <div class='value'>{messagesToday}</div>
            </div>
            <div class='stat'>
                <div class='label'>MESSAGES THIS WEEK</div>
                <div class='value cyan'>{messagesThisWeek}</div>
            </div>
            <div class='stat'>
                <div class='label'>ACTIVE CONVERSATIONS (7d)</div>
                <div class='value amber'>{activeConversations}</div>
            </div>
            <div class='stat'>
                <div class='label'>DELIVERED</div>
                <div class='value'>{deliveredMessages}</div>
            </div>
        </div>
    </div>

    <div class='section'>
        <div class='section-title'>*** Configuration</div>
        <div class='stat-grid'>
            <div class='stat'>
                <div class='label'>MESSAGE EXPIRY</div>
                <div class='value amber'>{expiryDays} days</div>
            </div>
            <div class='stat'>
                <div class='label'>MAX MESSAGE SIZE</div>
                <div class='value'>{maxBodySize / 1024} KB</div>
            </div>
            <div class='stat'>
                <div class='label'>SERVER TIME (UTC)</div>
                <div class='value cyan'>{DateTime.UtcNow:HH:mm:ss}</div>
            </div>
        </div>
    </div>

    <div class='section'>
        <div class='section-title'>*** Recent Registrations (last 10)</div>
        <table>
            <tr><th>NICK</th><th>REGISTERED</th><th>LAST SEEN</th></tr>
            {userRows}
        </table>
        {(totalUsers == 0 ? "<div style='color:#1a8c1a;padding:8px'>*** No users registered yet.</div>" : "")}
    </div>

    <div class='warn'>*** WARNING: This admin panel has no authentication. Access is restricted to localhost and IPs listed in Admin:AllowedIPs.</div>

    <div class='footer'>
        *** CryptoNotes Server Admin | E2E Encrypted Messaging Relay | All message content is PGP-encrypted ciphertext
    </div>
</body>
</html>";

            return Content(html, "text/html");
        }
    }
}
