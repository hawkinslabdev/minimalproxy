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
            // First check if the table exists
            var tableExists = Database.ExecuteSqlRaw(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Tokens'") > 0;

            if (tableExists)
            {
                // Check if we need to migrate to the new schema
                var hasSaltColumn = Database.ExecuteSqlRaw(
                    "SELECT COUNT(*) FROM pragma_table_info('Tokens') WHERE name='TokenSalt'") > 0;
                    
                if (!hasSaltColumn)
                {
                    // We need to recreate the table with the new schema
                    MigrateToNewSchema();
                }
            }
            else
            {
                // Create the table with the new schema
                Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS Tokens (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT, 
                        Username TEXT NOT NULL,
                        TokenHash TEXT NOT NULL, 
                        TokenSalt TEXT NOT NULL,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        RevokedAt DATETIME NULL
                    )");
                Log.Information("Created Tokens table with new schema");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error ensuring Tokens table is created");
        }
    }

    private void MigrateToNewSchema()
    {
        Log.Information("Migrating Tokens table to new schema...");
        
        try
        {
            // Create a backup of the old table
            Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Tokens_Backup AS SELECT * FROM Tokens");
            Log.Information("Created backup of Tokens table");
            
            // Create a new table with the correct schema
            Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS Tokens_New (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, 
                    Username TEXT NOT NULL,
                    TokenHash TEXT NOT NULL, 
                    TokenSalt TEXT NOT NULL,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    RevokedAt DATETIME NULL
                )");
                
            // We can't migrate the actual tokens since the hashing would be different
            // but we can preserve the usernames if they exist
            try
            {
                var hasUsername = Database.ExecuteSqlRaw(
                    "SELECT COUNT(*) FROM pragma_table_info('Tokens') WHERE name='Username'") > 0;
                    
                if (hasUsername)
                {
                    Database.ExecuteSqlRaw(@"
                        INSERT INTO Tokens_New (Username, TokenHash, TokenSalt, CreatedAt)
                        SELECT Username, '', '', CURRENT_TIMESTAMP FROM Tokens
                        GROUP BY Username
                    ");
                    Log.Information("Preserved usernames from old table");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not migrate usernames from old table");
            }
            
            // Swap tables
            Database.ExecuteSqlRaw("DROP TABLE Tokens");
            Database.ExecuteSqlRaw("ALTER TABLE Tokens_New RENAME TO Tokens");
            
            Log.Information("Successfully migrated to new token schema");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error migrating to new token schema");
            
            // Attempt to restore from backup if available
            try
            {
                Database.ExecuteSqlRaw("DROP TABLE IF EXISTS Tokens");
                Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Tokens AS SELECT * FROM Tokens_Backup");
                Log.Information("Restored Tokens table from backup due to migration failure");
            }
            catch (Exception restoreEx)
            {
                Log.Error(restoreEx, "Failed to restore Tokens table from backup");
            }
        }
    }
}

public class AuthToken
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string TokenHash { get; set; }
    public required string TokenSalt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; } = null;
    public bool IsActive => RevokedAt == null;
}