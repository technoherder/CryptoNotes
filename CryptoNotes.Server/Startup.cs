using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoNotes.Server.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CryptoNotes.Server
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContext<ServerDbContext>(options =>
                options.UseSqlite("Data Source=cryptonotes.db"));

            services.AddControllers(options =>
            {
                options.MaxModelBindingCollectionSize = 100;
            });

            // Configure max request body size
            services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
            {
                options.Limits.MaxRequestBodySize = 65536; // 64KB
            });

            services.AddSingleton<IConfiguration>(Configuration);

            // Register background service for message expiry cleanup
            services.AddHostedService<MessageExpiryService>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                // In production: HTTPS redirect and HSTS
                app.UseHsts();
                app.UseHttpsRedirection();
            }

            // Security headers
            app.Use(async (context, next) =>
            {
                context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
                context.Response.Headers.Add("X-Frame-Options", "DENY");
                context.Response.Headers.Add("X-XSS-Protection", "1; mode=block");
                context.Response.Headers.Add("Referrer-Policy", "no-referrer");
                context.Response.Headers.Add("Cache-Control", "no-store");

                // Admin page needs inline styles; API endpoints use strict CSP
                var csp = context.Request.Path.StartsWithSegments("/admin")
                    ? "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'"
                    : "default-src 'none'; frame-ancestors 'none'";
                context.Response.Headers.Add("Content-Security-Policy", csp);

                context.Response.Headers.Add("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
                // Remove server identification header
                context.Response.Headers.Remove("Server");
                await next();
            });

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();

                // Health check endpoint for monitoring
                endpoints.MapGet("/health", async context =>
                {
                    try
                    {
                        using (var scope = context.RequestServices.CreateScope())
                        {
                            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
                            // Quick DB connectivity check
                            await db.Database.ExecuteSqlRawAsync("SELECT 1");
                        }
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"status\":\"healthy\"}");
                    }
                    catch
                    {
                        context.Response.StatusCode = 503;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"status\":\"unhealthy\"}");
                    }
                });
            });

            // Ensure database is created
            using (var scope = app.ApplicationServices.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
                db.Database.EnsureCreated();
            }
        }
    }

    /// <summary>
    /// Background service that periodically deletes expired messages.
    /// Messages older than the configured expiry period (default 30 days) are permanently removed.
    /// The server only stores encrypted ciphertext, but removing old messages limits exposure.
    /// </summary>
    public class MessageExpiryService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IConfiguration _config;

        public MessageExpiryService(IServiceProvider services, IConfiguration config)
        {
            _services = services;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    int expiryDays = _config.GetValue<int>("Security:MessageExpiryDays", 30);
                    if (expiryDays > 0)
                    {
                        var cutoff = DateTime.UtcNow.AddDays(-expiryDays);

                        using (var scope = _services.CreateScope())
                        {
                            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
                            var expired = await db.Messages
                                .Where(m => m.SentAt < cutoff)
                                .ToListAsync(stoppingToken);

                            if (expired.Any())
                            {
                                db.Messages.RemoveRange(expired);
                                await db.SaveChangesAsync(stoppingToken);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Log and continue - don't crash the service
                }

                // Run cleanup every 6 hours
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
        }
    }
}
