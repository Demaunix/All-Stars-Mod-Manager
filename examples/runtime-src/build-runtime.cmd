@echo off
rem Builds example-runtime.dll (32-bit, no C runtime, no entry point) with the Microsoft C++ build tools.
rem Run it from an "x86 Native Tools Command Prompt", or let it find Visual Studio Build Tools 2022.
rem Usage: build-runtime.cmd <output folder>     (intermediate files go to <output folder>\obj)
setlocal
if "%~1"=="" (echo usage: build-runtime.cmd ^<output folder^> & exit /b 64)
where cl >nul 2>nul || call "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsamd64_x86.bat" >nul || exit /b 1
if not exist "%~1\obj" mkdir "%~1\obj"
cl /nologo /O1 /GS- /Gs9999999 /Zl /c /Fo"%~1\obj\runtime.obj" "%~dp0runtime.c" || exit /b 1
link /nologo /DLL /NOENTRY /NODEFAULTLIB /MACHINE:X86 /Brepro /DEF:"%~dp0exports.def" /IMPLIB:"%~1\obj\example-runtime.lib" /OUT:"%~1\example-runtime.dll" "%~1\obj\runtime.obj" || exit /b 1
echo built %~1\example-runtime.dll
