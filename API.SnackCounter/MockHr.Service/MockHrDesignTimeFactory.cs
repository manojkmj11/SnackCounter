using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MockHr.Service;

public sealed class MockHrDesignTimeFactory : IDesignTimeDbContextFactory<MockHrDbContext>
{
    public MockHrDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MockHrDbContext>()
            .UseSqlServer("Server=localhost\\SQLEXPRESS;Database=SnackCounterMockHr;Trusted_Connection=True;Encrypt=False;TrustServerCertificate=True")
            .Options;
        return new MockHrDbContext(options);
    }
}
