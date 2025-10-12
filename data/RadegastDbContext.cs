using Microsoft.EntityFrameworkCore;
using RadegastWeb.Models;

namespace RadegastWeb.Data
{
    public class RadegastDbContext : DbContext
    {
        public RadegastDbContext(DbContextOptions<RadegastDbContext> options) : base(options)
        {
        }

        public DbSet<Account> Accounts { get; set; }
        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<DisplayName> DisplayNames { get; set; }
        public DbSet<Notice> Notices { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure Account entity
            modelBuilder.Entity<Account>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
                entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Password).IsRequired();
                entity.Property(e => e.DisplayName).HasMaxLength(200);
                entity.Property(e => e.GridUrl).IsRequired();
                entity.Property(e => e.Status).HasMaxLength(50);
                entity.Property(e => e.CurrentRegion).HasMaxLength(200);
                entity.Property(e => e.AvatarUuid).HasMaxLength(36);
            });

            // Configure ChatMessage entity
            modelBuilder.Entity<ChatMessage>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.AccountId).IsRequired();
                entity.Property(e => e.SenderName).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Message).IsRequired();
                entity.Property(e => e.ChatType).HasMaxLength(50);
                entity.Property(e => e.RegionName).HasMaxLength(200);
                entity.Property(e => e.SenderId).HasMaxLength(36);
                entity.Property(e => e.TargetId).HasMaxLength(36);
                entity.Property(e => e.SessionId).HasMaxLength(100);
                entity.Property(e => e.SessionName).HasMaxLength(200);

                // Configure foreign key relationship explicitly
                entity.HasOne(e => e.Account)
                      .WithMany(a => a.ChatMessages)
                      .HasForeignKey(e => e.AccountId)
                      .IsRequired()
                      .OnDelete(DeleteBehavior.Cascade);

                // Create indexes for better query performance
                entity.HasIndex(e => new { e.AccountId, e.SessionId, e.Timestamp })
                      .HasDatabaseName("IX_ChatMessage_Account_Session_Time");
                entity.HasIndex(e => new { e.AccountId, e.ChatType, e.Timestamp })
                      .HasDatabaseName("IX_ChatMessage_Account_Type_Time");
                entity.HasIndex(e => e.SessionId)
                      .HasDatabaseName("IX_ChatMessage_SessionId");
            });

            // Configure DisplayName entity
            modelBuilder.Entity<DisplayName>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.AvatarId).IsRequired().HasMaxLength(36);
                entity.Property(e => e.DisplayNameValue).IsRequired().HasMaxLength(200);
                entity.Property(e => e.UserName).IsRequired().HasMaxLength(100);
                entity.Property(e => e.LegacyFirstName).IsRequired().HasMaxLength(100);
                entity.Property(e => e.LegacyLastName).IsRequired().HasMaxLength(100);
                entity.Property(e => e.IsDefaultDisplayName).IsRequired();
                entity.Property(e => e.NextUpdate).IsRequired();
                entity.Property(e => e.LastUpdated).IsRequired();
                entity.Property(e => e.CachedAt).IsRequired();

                // Configure foreign key relationship
                entity.HasOne(e => e.Account)
                      .WithMany()
                      .HasForeignKey(e => e.AccountId)
                      .IsRequired()
                      .OnDelete(DeleteBehavior.Cascade);

                // Create unique index for avatar per account
                entity.HasIndex(e => new { e.AccountId, e.AvatarId })
                      .IsUnique()
                      .HasDatabaseName("IX_DisplayName_Account_Avatar");

                // Create index for cache expiry cleanup
                entity.HasIndex(e => e.CachedAt)
                      .HasDatabaseName("IX_DisplayName_CachedAt");
            });

            // Configure Notice entity
            modelBuilder.Entity<Notice>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.AccountId).IsRequired();
                entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
                entity.Property(e => e.Message).IsRequired();
                entity.Property(e => e.FromName).IsRequired().HasMaxLength(200);
                entity.Property(e => e.FromId).IsRequired().HasMaxLength(36);
                entity.Property(e => e.GroupId).HasMaxLength(36);
                entity.Property(e => e.GroupName).HasMaxLength(200);
                entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
                entity.Property(e => e.AttachmentName).HasMaxLength(200);
                entity.Property(e => e.AttachmentType).HasMaxLength(100);

                // Configure foreign key relationship
                entity.HasOne(e => e.Account)
                      .WithMany(a => a.Notices)
                      .HasForeignKey(e => e.AccountId)
                      .IsRequired()
                      .OnDelete(DeleteBehavior.Cascade);

                // Create indexes for better query performance
                entity.HasIndex(e => new { e.AccountId, e.Timestamp })
                      .HasDatabaseName("IX_Notice_Account_Time");
                entity.HasIndex(e => new { e.AccountId, e.Type, e.Timestamp })
                      .HasDatabaseName("IX_Notice_Account_Type_Time");
                entity.HasIndex(e => new { e.AccountId, e.IsRead, e.Timestamp })
                      .HasDatabaseName("IX_Notice_Account_Read_Time");
            });
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                // Fallback configuration if not configured in Program.cs
                var dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dataDirectory))
                {
                    Directory.CreateDirectory(dataDirectory);
                }
                
                var dbPath = Path.Combine(dataDirectory, "radegast.db");
                optionsBuilder.UseSqlite($"Data Source={dbPath}");
            }
        }
    }
}