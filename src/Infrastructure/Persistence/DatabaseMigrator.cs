using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AISupportOps.Infrastructure.Persistence;

public static partial class DatabaseMigrator
{
    /// <summary>
    /// Applies pending EF Core migrations. Used for local development and tests;
    /// production applies migrations as an explicit deployment step.
    /// </summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrator));

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        LogApplying(logger, pending.Count, pending);
        await db.Database.MigrateAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {MigrationCount} pending migrations: {Migrations}")]
    private static partial void LogApplying(ILogger logger, int migrationCount, IReadOnlyList<string> migrations);
}
