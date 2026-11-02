@echo off
setlocal
set "JAVA_EXE=%~dp0..\JavaRuntime\bin\java.exe"
if not exist "%JAVA_EXE%" (
  echo Java runtime not found: %JAVA_EXE% 1>&2
  exit /b 2
)
set "VLC_VERBOSE=-1"
"%JAVA_EXE%" -Xmx1024m -Djna.nosys=true -jar "%~dp0ffdec.jar" %*
exit /b %ERRORLEVEL%
