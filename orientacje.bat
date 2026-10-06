@echo off
rem Orientacje - uruchamianie bez instalowania czegokolwiek.
rem Przy pierwszym uruchomieniu (lub po zmianie orientacje.cs) kompiluje program
rem wbudowanym w Windows kompilatorem .NET Framework, potem uruchamia orientacje.exe.
setlocal
chcp 65001 >nul
set "EXE=%~dp0orientacje.exe"
set "SRC=%~dp0orientacje.cs"

set "BUDUJ="
if not exist "%EXE%" set "BUDUJ=1"
if exist "%SRC%" if exist "%EXE%" (
  for /f "delims=" %%i in ('dir /b /o:d "%EXE%" "%SRC%"') do set "NAJNOWSZY=%%i"
)
if /i "%NAJNOWSZY%"=="orientacje.cs" set "BUDUJ=1"
if defined BUDUJ call :kompiluj || goto :koniec

"%EXE%" %*
set "RC=%ERRORLEVEL%"
goto :koniec

:kompiluj
if not exist "%SRC%" (
  echo Brak pliku orientacje.cs obok orientacje.bat.
  exit /b 1
)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Nie znaleziono kompilatora .NET Framework 4 ^(csc.exe^).
  exit /b 1
)
echo Kompilacja orientacje.exe ...
"%CSC%" /nologo /optimize+ /codepage:65001 /target:exe /platform:anycpu ^
  /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:"%EXE%" "%SRC%"
if errorlevel 1 (
  echo Kompilacja nie powiodla sie.
  exit /b 1
)
exit /b 0

:koniec
if not defined RC set "RC=1"
rem pauza tylko gdy plik uruchomiono dwuklikiem / przeciagnieciem DXF
echo %CMDCMDLINE% | find /i "%~nx0" >nul && if not defined ORIENT_BEZ_PAUZY pause
exit /b %RC%
