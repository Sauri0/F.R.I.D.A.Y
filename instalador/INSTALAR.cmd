@echo off
rem Doble clic y listo.
rem
rem Existe este .cmd porque un .ps1 no se ejecuta con doble clic: Windows lo abre en el Bloc de
rem notas. Y porque la politica de ejecucion por defecto bloquea los scripts bajados de internet,
rem asi que se levanta solo para este proceso -no se toca la configuracion del equipo-.

setlocal
set "AQUI=%~dp0"

where pwsh >nul 2>&1
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%AQUI%instalar.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%AQUI%instalar.ps1" %*
)

if errorlevel 1 (
    echo.
    echo   La instalacion no termino. Mira el mensaje de arriba.
    pause
)
endlocal
