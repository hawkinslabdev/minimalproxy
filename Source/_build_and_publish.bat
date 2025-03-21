@echo off

REM Stop IIS
iisreset /stop 

REM Set deployment path
set "DEPLOY_PATH=C:\Repository\MinimalProxy\Deployment\MinimalProxy"

REM Temporarily move auth.db if it exists
if exist "%DEPLOY_PATH%\auth.db" (
    move /Y "%DEPLOY_PATH%\auth.db" "%TEMP%\auth.db"
)

REM Remove the deployment folder
rmdir /s /q "%DEPLOY_PATH%"

REM Recreate the deployment folder
mkdir "%DEPLOY_PATH%"

REM Restore auth.db
if exist "%TEMP%\auth.db" (
    move /Y "%TEMP%\auth.db" "%DEPLOY_PATH%\auth.db"
)

REM Publish the project
dotnet publish -c Release -o "%DEPLOY_PATH%"

REM Copy environments and endpoints
xcopy C:\Repository\MinimalProxy\Source\MinimalProxy\environments "%DEPLOY_PATH%\environments" /E /I /Y
xcopy C:\Repository\MinimalProxy\Source\MinimalProxy\endpoints "%DEPLOY_PATH%\endpoints" /E /I /Y

REM Start IIS
iisreset /start

REM Open deployment folder
start "" "%DEPLOY_PATH%"
