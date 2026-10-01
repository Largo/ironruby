@echo off
rem IronRuby Notepad. Double-click it, drop a file on it, or run: notepad.cmd file.txt
rem
rem ir.exe is a console program, so Windows gives it a console window. "start /b" leaves
rem that console to ir.exe alone once this batch file has ended, and notepad.rb then
rem lets go of it, which closes the window. To keep the console for Ruby's output, run
rem ..\..\ir.cmd notepad.rb instead. The build is chosen as ir.cmd chooses it.
setlocal
set "IR_ROOT=%~dp0..\.."
if "%IR_CONFIG%"=="" if not exist "%IR_ROOT%\Src\Console\bin\Debug\" if exist "%IR_ROOT%\Src\Console\bin\Release\" set "IR_CONFIG=Release"
if "%IR_CONFIG%"=="" set "IR_CONFIG=Debug"
if "%IR_TFM%"=="" set "IR_TFM=net8.0"
set "IR_BIN=%IR_ROOT%\Src\Console\bin\%IR_CONFIG%\%IR_TFM%\win-x64\ir.exe"
if not exist "%IR_BIN%" set "IR_BIN=%IR_ROOT%\Src\Console\bin\%IR_CONFIG%\%IR_TFM%\ir.exe"
set "S=%IR_ROOT%\Src\StdLib"
start "" /b "%IR_BIN%" -X:UsePrism "-X:StdLib=%S%\ironruby;%S%\ruby\4.0;%S%\ruby\1.9.1" "%~dp0notepad.rb" %*
