@echo off
setlocal
chcp 65001 >nul
title ForgeSkin 前端

cd /d "%~dp0ForgeSkin\Editor"
if errorlevel 1 (
  echo [错误] 找不到 ForgeSkin\Editor 目录。
  pause
  exit /b 1
)

where pnpm.cmd >nul 2>nul
if errorlevel 1 (
  echo [错误] 没找到 pnpm，请先安装 pnpm 或把它加进 PATH。
  pause
  exit /b 1
)

if not exist "node_modules" (
  echo 正在安装前端依赖...
  call pnpm install --frozen-lockfile
  if errorlevel 1 goto failed
)

rem Electron 二进制下载走国内镜像，GitHub 直连太慢。
set ELECTRON_MIRROR=https://npmmirror.com/mirrors/electron/
call pnpm start
if errorlevel 1 goto failed
exit /b 0

:failed
echo.
echo 前端启动失败，请看上面的报错。
pause
exit /b 1
