using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Persistence;

public sealed class ShortenerDbContext(DbContextOptions<ShortenerDbContext> options) : DbContext(options)
{
    public DbSet<ShortLink> ShortLinks => Set<ShortLink>();

    public DbSet<ClickEvent> ClickEvents => Set<ClickEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        if (Database.IsSqlite())
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ShortLink>(link =>
        {
            link.ToTable("ShortLinks");
            link.HasKey(l => l.Id);
            link.Property(l => l.Id).ValueGeneratedNever();

            link.Property(l => l.Code)
                .HasConversion(code => code.Value, value => ShortCode.Create(value).Value)
                .HasMaxLength(ShortCode.MaxLength)
                .IsUnicode(false)
                .UseCollation(Database.IsSqlServer() ? "Latin1_General_100_BIN2" : "BINARY")
                .IsRequired();
            link.HasIndex(l => l.Code).IsUnique();

            link.Property(l => l.Destination)
                .HasConversion(url => url.ToString(), value => DestinationUrl.Create(value).Value)
                .HasMaxLength(DestinationUrl.MaxLength)
                .IsRequired();

            link.Property(l => l.ManagementTokenHash).HasMaxLength(64).IsUnicode(false).IsRequired();
            link.Property(l => l.ClickCount).HasDefaultValue(0L);
            link.Ignore(l => l.IsDisabled);
        });

        modelBuilder.Entity<ClickEvent>(click =>
        {
            click.ToTable("ClickEvents");
            click.HasKey(c => c.Id);
            click.Property(c => c.Id).ValueGeneratedNever();
            click.Property(c => c.ReferrerHost).HasMaxLength(ClickEvent.MaxReferrerLength);
            click.Property(c => c.UserAgent).HasMaxLength(ClickEvent.MaxUserAgentLength);
            click.Property(c => c.VisitorHash).HasMaxLength(64).IsUnicode(false);
            click.HasIndex(c => new { c.ShortLinkId, c.OccurredOn });
            click.HasOne<ShortLink>().WithMany().HasForeignKey(c => c.ShortLinkId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
