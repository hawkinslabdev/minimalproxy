@echo off
setlocal

REM Set BASEDIR to the current directory
set "BASEDIR=%CD%"

REM Create directories
mkdir "%BASEDIR%\environments"
mkdir "%BASEDIR%\endpoints"
mkdir "%BASEDIR%\endpoints\Accounts"
mkdir "%BASEDIR%\endpoints\Items"

REM Create settings.json
echo { > "%BASEDIR%\environments\settings.json"
echo     "Environment": { >> "%BASEDIR%\environments\settings.json"
echo         "ServerName": "VM2K22" >> "%BASEDIR%\environments\settings.json"
echo     } >> "%BASEDIR%\environments\settings.json"
echo } >> "%BASEDIR%\environments\settings.json"

REM Create entity.json for Accounts
echo { > "%BASEDIR%\endpoints\Accounts\entity.json"
echo     "Url": "http://localhost:8020/api/accounts", >> "%BASEDIR%\endpoints\Accounts\entity.json"
echo     "Methods": ["GET", "POST", "PUT", "DELETE"] >> "%BASEDIR%\endpoints\Accounts\entity.json"
echo } >> "%BASEDIR%\endpoints\Accounts\entity.json"

REM Create entity.json for Items
echo { > "%BASEDIR%\endpoints\Items\entity.json"
echo     "Url": "http://localhost:8020/api/items", >> "%BASEDIR%\endpoints\Items\entity.json"
echo     "Methods": ["GET", "POST", "PUT", "DELETE"] >> "%BASEDIR%\endpoints\Items\entity.json"
echo } >> "%BASEDIR%\endpoints\Items\entity.json"

echo Setup completed successfully!
pause
