@echo off
setlocal
cd /d "%~dp0"
rem Windows 標準の .NET Framework 4.x C# コンパイラで tk2nmea.exe をビルドします
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo C# コンパイラ csc.exe が見つかりません。.NET Framework 4.5 以降が必要です。
  pause
  exit /b 1
)
"%CSC%" /nologo /optimize+ /codepage:65001 /target:exe /r:System.Drawing.dll /r:System.IO.Compression.dll /out:tk2nmea.exe tk2nmea.cs
if errorlevel 1 (
  echo.
  echo ビルドに失敗しました。上のエラー内容を確認してください。
  pause
  exit /b 1
)
echo.
echo ビルド成功: tk2nmea.exe を作成しました。
pause
