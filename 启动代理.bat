@echo off
rem Thin wrapper: the real launcher is Start-Agent.bat.
rem Reason: a Chinese file name inside the elevated relaunch command gets
rem mangled by the console code page, so the ASCII-named launcher is used.
call "%~dp0Start-Agent.bat" %*
