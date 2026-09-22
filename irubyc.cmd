@echo off
rem IronRuby's ahead-of-time compiler on Windows. The same thing as `ir.cmd -S irubyc`.
rem Needs the .NET SDK; see irubyc.sh and `irubyc --help`.
"%~dp0ir.cmd" -S irubyc %*
