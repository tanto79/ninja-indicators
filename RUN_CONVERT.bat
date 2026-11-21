@echo off
cd /d "%~dp0"

where py >nul 2>nul
if %errorlevel%==0 (set "PY=py") else (set "PY=python")

%PY% -c "import pandas, pyarrow" >nul 2>nul
if not %errorlevel%==0 (
    echo First run: installing required packages, please wait...
    %PY% -m pip install pandas pyarrow
)

echo.
echo Converting tick text file(s) from data\ticks into date= day folders.
echo This does NOT run the analysis - it only converts to parquet.
echo.

%PY% convert_only.py

echo.
echo ============================================================
echo DONE. New day folders are in this repo.
echo Upload the repo to GitHub with git, then ask me to run the analysis.
echo To analyze on this PC instead: double-click RUN_STUDY.bat
echo ============================================================
pause
