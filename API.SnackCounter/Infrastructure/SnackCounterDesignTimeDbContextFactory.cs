using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace SnackCounter.Infrastructure;

public sealed class SnackCounterDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SnackCounterDbContext>
{
    public SnackCounterDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("SnackCounter")
            ?? throw new InvalidOperationException("ConnectionStrings:SnackCounter is required for EF migrations.");
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new SnackCounterDbContext(options);
    }
}
