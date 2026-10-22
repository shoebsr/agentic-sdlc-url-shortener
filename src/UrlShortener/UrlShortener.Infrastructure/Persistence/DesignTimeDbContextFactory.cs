using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UrlShortener.Infrastructure.Persistence;

internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ShortenerDbContext>
{
    public ShortenerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ShortenerDbContext>()
            .UseSqlServer("Server=localhost;Database=UrlShortener;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new ShortenerDbContext(options);
    }
}
