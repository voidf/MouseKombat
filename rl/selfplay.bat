@echo off
REM Build the sim (Release) then run warm-started self-play.
REM Usage:  selfplay.bat [total_steps] [init_ckpt] [out_name]
REM   e.g.  selfplay.bat 2000000 checkpoints\ppo_hamster_selfplay_v2.zip ppo_hamster_selfplay_v3
REM   The learner and its opponent are each drawn from the WHOLE roster every episode
REM   (mirrors included), so there is no per-character switch any more.
REM   BEFORE a fresh campaign: move checkpoints\pool aside. Opponents are sampled from the
REM   20 highest-numbered snapshots, so a pool left over from a longer run would keep the
REM   new run's own snapshots out of the sampling window entirely.
cd /d "%~dp0.."
echo === building MouseKombat.Sim (Release) ===
dotnet build MouseKombat.Sim\MouseKombat.Sim.csproj -c Release
if errorlevel 1 ( echo. & echo BUILD FAILED & pause & exit /b 1 )
echo.
echo === self-play (roster-wide, mirrors included) ===
python rl\selfplay.py %1 %2 %3
echo.
echo Export the result:  python rl\export_onnx.py ^<out_name^> ^<out_name^>
pause
