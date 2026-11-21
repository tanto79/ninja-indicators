@echo off
cd /d "%~dp0"

where py >nul 2>nul
if %errorlevel%==0 (set "PY=py") else (set "PY=python")

%PY% -c "import pandas, pyarrow, pytz, tqdm" >nul 2>nul
if not %errorlevel%==0 (
    echo First run: installing required packages, please wait...
    %PY% -m pip install pandas pyarrow pytz tqdm
)

echo.
echo Running the Level Role Study on all tick data in this folder.
echo This can take 10-30+ minutes. Do not close this window.
echo.

%PY% run_study.py

echo.
echo ============================================================
echo DONE. Results are in the results_v2 folder:
echo   results_v2\tables\*.csv        - all answer tables
echo   FINDINGS.md                    - plain-English summary
echo ============================================================
pause
