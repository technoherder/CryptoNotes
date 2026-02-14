using Microsoft.EntityFrameworkCore;
using CryptoNotes.Server.Models;

namespace CryptoNotes.Server.Data
{
    public class ServerDbContext : DbContext
    {
        public ServerDbContext(DbContextOptions<ServerDbContext> options)
            : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Message> Messages { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Username)
                .IsUnique();

            modelBuilder.Entity<Message>()
                .HasIndex(m => m.RecipientUsername);

            modelBuilder.Entity<Message>()
                .HasIndex(m => m.SenderUsername);
        }
    }
}
