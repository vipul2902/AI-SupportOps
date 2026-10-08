using AISupportOps.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AISupportOps.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` tooling to generate migrations. Generating a migration
/// never opens a connection, so no real credentials are needed here.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=design-time-only", npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new NoTenant(), TimeProvider.System);
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
    }
}
