using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace SchoolFlow.Web.Data;

/// <summary>Registers the right context for the configured engine and brings the schema up to date at startup.</summary>
public static class DatabaseSetup
{
    /// <param name="contentRoot">The site's folder; the default SQLite file lives in its App_Data sub-folder.</param>
    public static void AddSchoolFlowDatabase(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        var provider = config["Database:Provider"] ?? "Sqlite";
        var connectionString = config["Database:ConnectionString"];
        if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Database:ConnectionString is required when Database:Provider is SqlServer.");
            // Retry brief outages (a failover, a dropped connection) instead of failing the request.
            services.AddDbContext<AppDbContext, SqlServerAppDbContext>(o =>
                o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(maxRetryCount: 3)));
        }
        else if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                // An absolute path: under IIS the process's working folder is not the site's folder.
                var folder = Path.Combine(contentRoot, "App_Data");
                Directory.CreateDirectory(folder);
                connectionString = "Data Source=" + Path.Combine(folder, "schoolflow.db");
            }
            services.AddDbContext<AppDbContext, SqliteAppDbContext>(o => o.UseSqlite(connectionString));
        }
        else
        {
            throw new InvalidOperationException($"Database:Provider '{provider}' is not supported. Use Sqlite or SqlServer.");
        }
    }

    /// <summary>
    /// Applies any migrations the database has not had yet. Safe to run on every start: an up-to-date
    /// database is left alone, and a new one is created from scratch.
    /// </summary>
    public static void Migrate(AppDbContext db, ILogger logger)
    {
        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count == 0) return;

        // A database made before migrations existed has the tables but no migration history, and
        // cannot be upgraded automatically. Say so plainly rather than failing on "table already exists".
        if (!db.Database.GetAppliedMigrations().Any() && HasTables(db))
            throw new InvalidOperationException(
                "This database was created by an early development build that had no migrations, so it cannot be upgraded. " +
                "Delete it (for SQLite, the schoolflow.db file) or point Database:ConnectionString at a new, empty database, then start again.");

        logger.LogInformation("Applying {Count} database migration(s): {Names}", pending.Count, string.Join(", ", pending));
        db.Database.Migrate();
    }

    /// <summary>Whether the app's tables are already there. Asks the database's catalogue, so nothing is logged as an error.</summary>
    private static bool HasTables(AppDbContext db)
    {
        if (!db.GetService<IRelationalDatabaseCreator>().Exists()) return false; // nothing there yet: Migrate will create it
        var sql = db.Database.IsSqlite()
            ? "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'Workflows'"
            : "SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Workflows'";
        return db.Database.SqlQueryRaw<int>(sql).AsEnumerable().First() > 0;
    }
}

// Used only by the "dotnet ef" command-line tool to build migrations; never at run time, and
// neither factory connects to a database.

public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteAppDbContext>
{
    public SqliteAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerAppDbContext>
{
    public SqlServerAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerAppDbContext>()
            .UseSqlServer("Server=design-time;Database=SchoolFlow;Trusted_Connection=True;TrustServerCertificate=True").Options);
}
