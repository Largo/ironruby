@echo off
rem Runs the built ir with the prism front end and the bundled standard library.
rem The Windows twin of ir.sh; see it for why each switch is here.
rem
rem   ir.cmd -e "puts RUBY_DESCRIPTION"
rem   set RUBY_EXE=%CD%\ir.cmd
rem
setlocal
set "IR_ROOT=%~dp0"
if "%IR_ROOT:~-1%"=="\" set "IR_ROOT=%IR_ROOT:~0,-1%"

rem IR_CONFIG=Release runs the optimized build instead. Debug stays the default,
rem exactly as in ir.sh.
if "%IR_CONFIG%"=="" set "IR_CONFIG=Debug"

rem IR_TFM picks the target framework out of the multi-targeted build (net8.0;net10.0),
rem exactly as in ir.sh. net8.0 stays the default.
if "%IR_TFM%"=="" set "IR_TFM=net8.0"

rem A tree built on Windows puts ir.exe straight in %IR_TFM%; one cross-published from
rem Linux with -r win-x64 puts it in %IR_TFM%\win-x64. Take whichever exists.
set "IR_BIN=%IR_ROOT%\Src\Console\bin\%IR_CONFIG%\%IR_TFM%\win-x64\ir.exe"
if not exist "%IR_BIN%" set "IR_BIN=%IR_ROOT%\Src\Console\bin\%IR_CONFIG%\%IR_TFM%\ir.exe"
if not exist "%IR_BIN%" (
  echo ir.cmd: no %IR_CONFIG% build at %IR_BIN% 1>&2
  echo ir.cmd: build it with: dotnet build Src\Console\Ruby.Console.csproj -c %IR_CONFIG% -r win-x64 --self-contained false 1>&2
  exit /b 127
)

set "S=%IR_ROOT%\Src\StdLib"

rem -S searches RUBYPATH before PATH, so `ir.cmd -S irb` runs the irb shipped here.
if "%RUBYPATH%"=="" (set "RUBYPATH=%S%\bin") else (set "RUBYPATH=%S%\bin;%RUBYPATH%")

rem -X:StdLib is split on Path.PathSeparator, which is ';' on Windows - a ':' list would
rem be cut apart at every drive letter.
"%IR_BIN%" -X:UsePrism "-X:StdLib=%S%\ironruby;%S%\ruby\4.0;%S%\ruby\1.9.1" %*
exit /b %ERRORLEVEL%
