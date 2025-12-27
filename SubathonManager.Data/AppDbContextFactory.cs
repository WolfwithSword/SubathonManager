using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SubathonManager.Data
{
    // to allow migrations add cmdline to work
    [ExcludeFromCodeCoverage]
    public class AppDbContextFactory
        : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>();

            options.UseSqlite("Data Source=data/design.db");

            return new AppDbContext(options.Options);
        }
    }
}
