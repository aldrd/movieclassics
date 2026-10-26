@echo off
rem Daily update: regenerate docs/data from IMDb + TMDB and publish to GitHub Pages.
rem Scheduled by Task Scheduler ("MovieClassics update", every day at 09:00).
setlocal
cd /d "%~dp0"
if not exist logs mkdir logs
set LOG=logs\update.log

echo ===== %date% %time% ===== >> "%LOG%"
dotnet run -c Release >> "%LOG%" 2>&1
if errorlevel 1 (
    echo Generator failed, nothing published. >> "%LOG%"
    exit /b 1
)

git add docs/data >> "%LOG%" 2>&1
git diff --cached --quiet
if not errorlevel 1 (
    echo No data changes. >> "%LOG%"
    exit /b 0
)
git commit -m "Daily data update %date%" >> "%LOG%" 2>&1
git push >> "%LOG%" 2>&1
echo Done. >> "%LOG%"
