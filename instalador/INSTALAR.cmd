@echo off
rem Doble clic y listo. Instala si no lo tenias, y actualiza si ya lo tenias: es el mismo archivo,
rem y volver a correrlo no pisa el nombre, las claves, la memoria ni el modelo de voz.
rem
rem Existe este .cmd porque un .ps1 no se ejecuta con doble clic: Windows lo abre en el Bloc de
rem notas. Y porque la politica de ejecucion por defecto bloquea los scripts bajados de internet,
rem asi que se levanta solo para este proceso -no se toca la configuracion del equipo-.
rem
rem Los argumentos se pasan tal cual, asi que desde una terminal tambien sirve
rem "INSTALAR.cmd -Actualizar": actualiza la aplicacion y la abre, sin preguntar nada.

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
    echo   No se termino. Mira el mensaje de arriba y volve a correrlo cuando quieras.
    pause
)
endlocal
