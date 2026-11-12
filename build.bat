@echo off
setlocal enabledelayedexpansion

echo ============================================================
echo   Idle Senior Supremacy (ISS) - Build ^& Package (Release)
echo ============================================================
echo.

set "ROOT_DIR=%~dp0"
set "SLN_PATH=%ROOT_DIR%IdleSeniorSupremacy.sln"
set "UI_PROJ=%ROOT_DIR%src\IdleSenior.UI\IdleSenior.UI.csproj"
set "PUBLISH_ROOT=%ROOT_DIR%publish\IdleSeniorSupremacy-win-x86"
set "CONFIG_DIR=%PUBLISH_ROOT%\config"
set "RUNTIMES_DIR=%PUBLISH_ROOT%\runtimes"
set "ZIP_PATH=%ROOT_DIR%publish\IdleSeniorSupremacy-win-x86.zip"

echo [1/5] Compilando solucao completa (Release)...
dotnet build "%SLN_PATH%" -c Release /p:TreatWarningsAsErrors=true
if errorlevel 1 (
    echo [ERRO] Falha ao compilar a solucao.
    exit /b 1
)

echo.
echo [2/5] Limpando pasta de publicacao anterior...
if exist "%PUBLISH_ROOT%" rmdir /s /q "%PUBLISH_ROOT%"
if exist "%ZIP_PATH%" del /f /q "%ZIP_PATH%"
mkdir "%CONFIG_DIR%"

echo.
echo [3/5] Publicando IdleSenior.UI em config\ (win-x86 self-contained)...
dotnet publish "%UI_PROJ%" -c Release -r win-x86 --self-contained true -o "%CONFIG_DIR%" /p:PublishSingleFile=false
if errorlevel 1 (
    echo [ERRO] Falha ao publicar IdleSenior.UI.
    exit /b 1
)

echo.
echo [4/5] Organizando pastas (runtimes\ e config\) + workers e DLLs...
xcopy /y /i /q "%ROOT_DIR%src\IdleSenior.Worker\bin\Release\net9.0\*.*" "%CONFIG_DIR%\" >nul
xcopy /y /i /q "%ROOT_DIR%src\IdleSenior.AchievementWorker\bin\Release\net9.0\*.*" "%CONFIG_DIR%\" >nul
if exist "%ROOT_DIR%src\NativeLibs\x86\steam_api.dll" copy /y "%ROOT_DIR%src\NativeLibs\x86\steam_api.dll" "%CONFIG_DIR%\" >nul
if exist "%ROOT_DIR%src\NativeLibs\x64\steam_api64.dll" copy /y "%ROOT_DIR%src\NativeLibs\x64\steam_api64.dll" "%CONFIG_DIR%\" >nul
if exist "%ROOT_DIR%assets\iss-icon.ico" copy /y "%ROOT_DIR%assets\iss-icon.ico" "%CONFIG_DIR%\iss-icon.ico" >nul
if exist "%CONFIG_DIR%\runtimes" (
    xcopy /e /y /i /q "%CONFIG_DIR%\runtimes\*" "%RUNTIMES_DIR%\" >nul
)
if exist "assets\iss-icon.ico" (
    copy /y "assets\iss-icon.ico" "%CONFIG_DIR%\iss-icon.ico" >nul
)

echo.
echo [5/5] Criando atalho ISS.lnk e gerando pacote ZIP...
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%PUBLISH_ROOT%\ISS.lnk'); $s.TargetPath = '%CONFIG_DIR%\IdleSenior.UI.exe'; $s.WorkingDirectory = '%CONFIG_DIR%'; $s.IconLocation = '%CONFIG_DIR%\IdleSenior.UI.exe,0'; $s.Save()"
powershell -NoProfile -Command "Compress-Archive -Path '%PUBLISH_ROOT%\*' -DestinationPath '%ZIP_PATH%' -Force"
if errorlevel 1 (
    echo [AVISO] Nao foi possivel gerar o .zip automaticamente, mas a pasta publish esta pronta.
) else (
    echo Pacote ZIP gerado em: %ZIP_PATH%
)

echo.
echo ============================================================
echo   Build concluido com sucesso!
echo   Pasta: %PUBLISH_ROOT%
echo   Atalho: %PUBLISH_ROOT%\ISS.lnk
echo ============================================================
exit /b 0
