@echo off
REM Build the combat-logic library (Release) for the Python RL trainer, then run the parity tests.
REM Double-click to run. The GAME itself does NOT need this — Godot builds everything on play.
cd /d "%~dp0.."
echo === building MouseKombat.Sim (Release) for RL ===
dotnet build MouseKombat.Sim\MouseKombat.Sim.csproj -c Release
if errorlevel 1 ( echo. & echo BUILD FAILED & pause & exit /b 1 )
echo.
echo === running sim parity tests ===
dotnet run --project MouseKombat.Sim.Tests -c Release
echo.
echo Done. Sim DLL: MouseKombat.Sim\bin\Release\net8.0\MouseKombat.Sim.dll
pause
