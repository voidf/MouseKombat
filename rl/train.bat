@echo off
REM Build the sim (Release) then train a PPO policy vs the state-machine AI.
REM Double-click to run. Pass total timesteps as an argument, else defaults to 200000.
cd /d "%~dp0.."
echo === building MouseKombat.Sim (Release) ===
dotnet build MouseKombat.Sim\MouseKombat.Sim.csproj -c Release
if errorlevel 1 ( echo. & echo BUILD FAILED & pause & exit /b 1 )
echo.
echo === training (PPO) ===
python rl\train.py %1
echo.
echo Export to ONNX with:  python rl\export_onnx.py
pause
