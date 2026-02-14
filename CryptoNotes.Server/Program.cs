using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CryptoNotes.Server
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();

            // Warn if using the default (insecure) token signing key
            var config = host.Services.GetService(typeof(IConfiguration)) as IConfiguration;
            var tokenKey = config?.GetValue<string>("Security:TokenSigningKey") ?? "";
            if (tokenKey.Contains("CHANGE-THIS") || tokenKey.Length < 32)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("WARNING: Using default or weak TokenSigningKey!");
                Console.WriteLine("Generate a secure key: openssl rand -base64 48");
                Console.WriteLine("Set it in appsettings.Production.json or via environment variable.");
                Console.ResetColor();
            }

            host.Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                    // Bind to localhost only - use a reverse proxy (Apache/Nginx) for external access.
                    // This prevents direct exposure of Kestrel to the internet.
                    // Override with ASPNETCORE_URLS environment variable if needed.
                    webBuilder.UseUrls("http://127.0.0.1:5000");
                });
    }
}
