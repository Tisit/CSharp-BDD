@echo off
rem WARNING (2026-10-03): starting this collection crashed Windows (bugcheck 0x3B in
rem dxgkrnl!McGenEventWrite_EtwWriteTransfer, process dwm.exe: an ETW event write in the
rem graphics kernel faulted as VTune 2024.2 started up). Earlier runs (2026-09-24) worked;
rem the failure is intermittent. Do not run with VTune 2024.2: update oneAPI VTune first,
rem and save all work before running. Analysis: vtune\crash_analysis.txt.
echo This script crashed Windows on 2026-10-03 (see the comment at the top). Press Ctrl+C to abort.
pause
rem Hardware Top-down profile of one n=21 dense 12-bit sweep (the n=24 campaign's
rem storage mode, at a size that no longer fits in cache; ~1-2 min, ~0.5 GB).
rem Run from an ADMINISTRATOR command prompt (right-click > Run as administrator).
rem v2 (2026-10-03): profiles the SIMD/lazy 12-bit paths; writes *_v2 files so the
rem 2026-09-24 profile (summary_b12.txt etc.) stays for before/after comparison.
setlocal
cd /d "%~dp0"
set V="C:\Program Files (x86)\Intel\oneAPI\vtune\latest\bin64\vtune.exe"
set R=%~dp0vtune\ue21_b12_v2
set O=%~dp0vtune
if exist "%R%" rmdir /s /q "%R%"
if not exist "%O%" mkdir "%O%"

%V% -collect uarch-exploration -mrte-mode=auto -result-dir "%R%" -- "%~dp0bin\Release\net10.0\sapaths.exe" 21 --mod 4093 --bits 12
%V% -report summary -result-dir "%R%" -format text -report-output "%O%\summary_b12_v2.txt"
%V% -report hotspots -result-dir "%R%" -group-by function -format csv -csv-delimiter tab -report-output "%O%\functions_b12_v2.tsv"
for %%F in (UpdateBlockRight12 LeftNN12 LeftSwap12 LeftEmit12 UpdateBlockStraddle12 EscapeAll UnpackTops) do (
  %V% -report hw-events -result-dir "%R%" -source-object "function=Engine`2::%%F" -group-by address -format csv -csv-delimiter tab -report-output "%O%\asm_b12_v2_%%F.tsv"
)
for %%F in (Unpack PackReduce AddRange) do (
  %V% -report hw-events -result-dir "%R%" -source-object "function=Packed12Simd::%%F" -group-by address -format csv -csv-delimiter tab -report-output "%O%\asm_b12_v2_simd_%%F.tsv"
)
echo.
echo Done. Reports are in %O% (*_v2 files)
pause
