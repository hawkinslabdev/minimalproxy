namespace MinimalProxy.Classes;

using Microsoft.EntityFrameworkCore;
using Serilog;

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }
    public DbSet<AuthToken> Tokens { get; set; }

    public void EnsureTablesCreated()
    {
        try
        {
            // Check if the table exists with the correct schema
            var tableExists = Database.ExecuteSqlRaw(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Tokens'") > 0;
                
            if (tableExists)
            {
                // Check if it has the right schema
                var hasTokenSalt = Database.ExecuteSqlRaw(
                    "SELECT COUNT(*) FROM pragma_table_info('Tokens') WHERE name='TokenSalt'") > 0;
                    
                if (hasTokenSalt)
                {
                    // Table exists and has the right schema, no need to recreate
                    Log.Information("Tokens table exists with correct schema");
                    return;
                }
                
                // Table exists but with wrong schema, drop it
                Log.Information("Tokens table exists but with wrong schema, recreating...");
                Database.ExecuteSqlRaw("DROP TABLE IF EXISTS Tokens");
            }
            
            // Create the table with the correct schema
            Database.ExecuteSqlRaw(@"
                CREATE TABLE Tokens (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, 
                    Username TEXT NOT NULL DEFAULT 'legacy',
                    TokenHash TEXT NOT NULL DEFAULT '', 
                    TokenSalt TEXT NOT NULL DEFAULT '',
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    RevokedAt DATETIME NULL
                )");
                
            Log.Information("Created new Tokens table with correct schema");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error ensuring Tokens table is created");
        }
    }
}

public class AuthToken
{
    public int Id { get; set; }
    public required string Username { get; set; } = $"user_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    public required string TokenHash { get; set; } = string.Empty;
    public required string TokenSalt { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; } = null;
    public bool IsActive => RevokedAt == null;
}