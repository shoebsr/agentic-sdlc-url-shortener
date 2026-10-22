using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UrlShortener.Infrastructure.Persistence;

#nullable disable

namespace UrlShortener.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(ShortenerDbContext))]
    partial class ShortenerDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.13")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

            modelBuilder.Entity("UrlShortener.Domain.ClickEvent", b =>
                {
                    b.Property<Guid>("Id")
                        .HasColumnType("uniqueidentifier");

                    b.Property<DateTimeOffset>("OccurredAt")
                        .HasColumnType("datetimeoffset");

                    b.Property<DateOnly>("OccurredOn")
                        .HasColumnType("date");

                    b.Property<string>("ReferrerHost")
                        .HasMaxLength(255)
                        .HasColumnType("nvarchar(255)");

                    b.Property<Guid>("ShortLinkId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<string>("UserAgent")
                        .HasMaxLength(512)
                        .HasColumnType("nvarchar(512)");

                    b.Property<string>("VisitorHash")
                        .HasMaxLength(64)
                        .IsUnicode(false)
                        .HasColumnType("varchar(64)");

                    b.HasKey("Id");

                    b.HasIndex("ShortLinkId", "OccurredOn");

                    b.ToTable("ClickEvents", (string)null);
                });

            modelBuilder.Entity("UrlShortener.Domain.ShortLink", b =>
                {
                    b.Property<Guid>("Id")
                        .HasColumnType("uniqueidentifier");

                    b.Property<long>("ClickCount")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint")
                        .HasDefaultValue(0L);

                    b.Property<string>("Code")
                        .IsRequired()
                        .HasMaxLength(32)
                        .IsUnicode(false)
                        .HasColumnType("varchar(32)")
                        .UseCollation("Latin1_General_100_BIN2");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("datetimeoffset");

                    b.Property<string>("Destination")
                        .IsRequired()
                        .HasMaxLength(2048)
                        .HasColumnType("nvarchar(2048)");

                    b.Property<DateTimeOffset?>("DisabledAt")
                        .HasColumnType("datetimeoffset");

                    b.Property<DateTimeOffset?>("ExpiresAt")
                        .HasColumnType("datetimeoffset");

                    b.Property<bool>("IsCustomAlias")
                        .HasColumnType("bit");

                    b.Property<string>("ManagementTokenHash")
                        .IsRequired()
                        .HasMaxLength(64)
                        .IsUnicode(false)
                        .HasColumnType("varchar(64)");

                    b.HasKey("Id");

                    b.HasIndex("Code")
                        .IsUnique();

                    b.ToTable("ShortLinks", (string)null);
                });

            modelBuilder.Entity("UrlShortener.Domain.ClickEvent", b =>
                {
                    b.HasOne("UrlShortener.Domain.ShortLink", null)
                        .WithMany()
                        .HasForeignKey("ShortLinkId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();
                });
#pragma warning restore 612, 618
        }
    }
}
