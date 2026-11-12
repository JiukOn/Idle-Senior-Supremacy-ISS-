@echo off
setlocal

echo ============================================
echo  Idle Senior Supremacy - Executar
echo ============================================
echo.

echo Verificando se o Steam esta rodando...
tasklist /fi "imagename eq steam.exe" 2>nul | find /i "steam.exe" >nul
if errorlevel 1 (
    echo [AVISO] Steam nao detectado. O farm de horas/cartas e conquistas exige a Steam aberta.
    echo         Abra o Steam e faca login na sua conta antes de iniciar os farms.
    echo.
)

if exist "src\IdleSenior.UI\bin\Debug\net9.0-windows\IdleSenior.UI.exe" (
    echo Iniciando Idle Senior Supremacy (Debug)...
    start "" "src\IdleSenior.UI\bin\Debug\net9.0-windows\IdleSenior.UI.exe"
    exit /b 0
)

if exist "src\IdleSenior.UI\bin\Release\net9.0-windows\IdleSenior.UI.exe" (
    echo Iniciando Idle Senior Supremacy (Release)...
    start "" "src\IdleSenior.UI\bin\Release\net9.0-windows\IdleSenior.UI.exe"
    exit /b 0
)

where dotnet >nul 2>&1
if not errorlevel 1 (
    echo Binarios pre-compilados nao encontrados. Compilando e executando via dotnet run...
    dotnet run --project src\IdleSenior.UI\IdleSenior.UI.csproj -c Debug
    exit /b 0
)

echo [ERRO] Executavel nao encontrado. Execute build.bat primeiro.
exit /b 1

endlocal
