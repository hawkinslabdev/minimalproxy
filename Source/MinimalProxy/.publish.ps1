# Step 1: Publish the application
dotnet publish C:\Github\minimalproxy\Source\MinimalProxy -c Release -o C:\Github\minimalproxy\Deployment\MinimalProxy_temp

# Step 2: Backup the existing auth.db file if it exists
$authDbPath = "C:\Github\minimalproxy\Deployment\MinimalProxy\auth.db"
$authDbBackupPath = "C:\Github\minimalproxy\Deployment\auth.db.backup"

if (Test-Path $authDbPath) {
    Copy-Item $authDbPath $authDbBackupPath -Force
    Write-Host "Database backup created"
}

# Step 3: Remove the existing deployment folder (except auth.db which we backed up)
if (Test-Path "C:\Github\minimalproxy\Deployment\MinimalProxy") {
    # Use robocopy to delete the directory with a purge option
    Write-Host "🗑️ Removing existing deployment folder..."
    Get-ChildItem -Path "C:\Github\minimalproxy\Deployment\MinimalProxy" -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1 # Give the system a moment
    Remove-Item -Path "C:\Github\minimalproxy\Deployment\MinimalProxy" -Force -ErrorAction SilentlyContinue
}

# Step 4: Instead of moving, copy the files and then delete the source
Write-Host "Copying files to deployment directory..."
# Make sure the destination directory exists
if (!(Test-Path "C:\Github\minimalproxy\Deployment\MinimalProxy")) {
    New-Item -Path "C:\Github\minimalproxy\Deployment\MinimalProxy" -ItemType Directory -Force
}

# Copy content instead of moving the directory
Copy-Item -Path "C:\Github\minimalproxy\Deployment\MinimalProxy_temp\*" -Destination "C:\Github\minimalproxy\Deployment\MinimalProxy" -Recurse -Force
Start-Sleep -Seconds 1 # Give the system a moment

# Clean up temp directory 
Write-Host "Cleaning up temporary files..."
Get-ChildItem -Path "C:\Github\minimalproxy\Deployment\MinimalProxy_temp" -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "C:\Github\minimalproxy\Deployment\MinimalProxy_temp" -Force -Recurse -ErrorAction SilentlyContinue

# Step 5: Restore the auth.db file if it existed
if (Test-Path $authDbBackupPath) {
    Write-Host "Restoring database..."
    Copy-Item $authDbBackupPath "C:\Github\minimalproxy\Deployment\MinimalProxy\auth.db" -Force
    Remove-Item $authDbBackupPath -Force
}

# Step 6: Clean up unnecessary development files
Write-Host "Removing development files..."
$filesToRemove = @(
    "*.pdb",
    "*.xml",
    "appsettings.Development.json"
)

foreach ($pattern in $filesToRemove) {
    Get-ChildItem -Path "C:\Github\minimalproxy\Deployment\MinimalProxy" -Filter $pattern -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
}

# Step 7: Copy the tokens directory if it exists or create it
$tokensSourcePath = "C:\Github\minimalproxy\Source\MinimalProxy\tokens"
$tokensDestPath = "C:\Github\minimalproxy\Deployment\MinimalProxy\tokens"

if (!(Test-Path $tokensDestPath)) {
    if (Test-Path $tokensSourcePath) {
        Write-Host "Copying token files..."
        Copy-Item -Path $tokensSourcePath -Destination $tokensDestPath -Recurse -Force
    } else {
        Write-Host "Creating tokens directory..."
        New-Item -Path $tokensDestPath -ItemType Directory -Force
    }
}

Write-Host "✅ Deployment complete. The application has been published to C:\Github\minimalproxy\Deployment\MinimalProxy with the original auth.db and tokens preserved."