# Step 1: Publish the application
dotnet publish C:\Github\minimalproxy\Source\MinimalProxy -c Release -o C:\Github\minimalproxy\Deployment\MinimalProxy_temp

# Step 2: Backup the existing auth.db file if it exists
$authDbPath = "C:\Github\minimalproxy\Deployment\MinimalProxy\auth.db"
$authDbBackupPath = "C:\Github\minimalproxy\Deployment\auth.db.backup"

if (Test-Path $authDbPath) {
    Copy-Item $authDbPath $authDbBackupPath -Force
}

# Step 3: Remove the existing deployment folder (except auth.db which we backed up)
if (Test-Path "C:\Github\minimalproxy\Deployment\MinimalProxy") {
    Remove-Item -Path "C:\Github\minimalproxy\Deployment\MinimalProxy" -Recurse -Force
}

# Step 4: Move the temp published files to the deployment directory
Move-Item -Path "C:\Github\minimalproxy\Deployment\MinimalProxy_temp" -Destination "C:\Github\minimalproxy\Deployment\MinimalProxy"

# Step 5: Restore the auth.db file if it existed
if (Test-Path $authDbBackupPath) {
    Copy-Item $authDbBackupPath "C:\Github\minimalproxy\Deployment\MinimalProxy\auth.db" -Force
    Remove-Item $authDbBackupPath -Force
}

# Step 6: Clean up unnecessary development files
$filesToRemove = @(
    "*.pdb",
    "*.xml",
    "appsettings.Development.json"
)

foreach ($pattern in $filesToRemove) {
    Get-ChildItem -Path "C:\Github\minimalproxy\Deployment\MinimalProxy" -Filter $pattern -Recurse | Remove-Item -Force
}

Write-Host "Deployment complete. The application has been published to C:\Github\minimalproxy\Deployment\MinimalProxy with the original auth.db preserved."