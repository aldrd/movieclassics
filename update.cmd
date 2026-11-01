@echo off
rem Daily update: regenerate docs/data from IMDb + TMDB and publish to GitHub Pages.
rem Scheduled by Task Scheduler ("MovieClassics update", every day at 09:00,
rem waking the computer from sleep; a run missed while it was off starts
rem as soon as it is on).
setlocal
cd /d "%~dp0"
if not exist logs mkdir logs
set LOG=logs\update.log

echo ===== %date% %time% ===== >> "%LOG%"

rem The task wakes the computer; the network may need a while after that.
set TRIES=0
:waitnet
curl -s -o NUL --max-time 10 --head https://datasets.imdbws.com/ && goto online
set /a TRIES+=1
if %TRIES% geq 18 (
    echo No network, nothing published. >> "%LOG%"
    exit /b 1
)
rem (timeout.exe needs a console; the headless task has none)
ping -n 11 127.0.0.1 > NUL
goto waitnet
:online
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
