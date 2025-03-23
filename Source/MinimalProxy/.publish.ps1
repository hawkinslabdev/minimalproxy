# Step 1: Publish the application
dotnet publish C:\Github\minimalproxy\Source\MinimalProxy -c Release -o C:\Github\minimalproxy\Deployment\MinimalProxy_temp

# Step 2: Backup the existing auth.db file if it exists
$authDbPath = "C:\Github\minimalproxy\Deployment\MinimalProxy\auth.db"
$authDbBackupPath = "C:\Github\minimalproxy\Deployment\auth.db.backup"

if (Test-Path $authDbPath) {
    Copy-Item $authDbPath $authDbBackupPath -Force
}

# Step 3: Remove the existing deployment folder (excluding auth.db and .gitignore)
if (Test-Path "C:\Github\minimalproxy\Deployment\MinimalProxy") {
    Get-ChildItem -Path "C:\Github\minimalproxy\Deployment\MinimalProxy" -Recurse | 
    Where-Object { $_.Name -ne "auth.db" -and $_.Name -ne ".gitignore" } | 
    Remove-Item -Recurse -Force
}

# Step 4: Move the published files to the deployment directory
Move-Item -Path "C:\Github\minimalproxy\Deployment\MinimalProxy_temp" -Destination "C:\Github\minimalproxy\Deployment\MinimalProxy"

# Step 5: Restore the auth.db file if it existed
if (Test-Path $authDbBackupPath) {
    Copy-Item $authDbBackupPath "C:\Github\minimalproxy\Deployment\MinimalProxy\auth.db" -Force
    Remove-Item $authDbBackupPath -Force
}

# Step 6: Remove unnecessary development files (excluding .gitignore)
$filesToRemove = @(
    "*.pdb",
    "*.xml",
    "appsettings.Development.json"
)

foreach ($pattern in $filesToRemove) {
    Get-ChildItem -Path "C:\Github\minimalproxy\Deployment\MinimalProxy" -Filter $pattern -Recurse | 
    Where-Object { $_.Name -ne ".gitignore" } | 
    Remove-Item -Force
}

Write-Host "Deployment complete. The application has been published to C:\Github\minimalproxy\Deployment\MinimalProxy with the original auth.db and .gitignore preserved."
