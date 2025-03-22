namespace MinimalProxy.Classes;

using Microsoft.EntityFrameworkCore;

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }
    public DbSet<AuthToken> Tokens { get; set; }

    public void EnsureTablesCreated()
    {
        Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Tokens (Id INTEGER PRIMARY KEY AUTOINCREMENT, Token TEXT NOT NULL UNIQUE, CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP)");
    }
}

public class AuthToken
{
    public int Id { get; set; }
    public required string Token { get; set; }
}