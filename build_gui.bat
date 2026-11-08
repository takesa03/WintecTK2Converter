@echo off
setlocal
cd /d "%~dp0"
rem WPF 版 (gui\) を gui\publish\WintecTK2Converter.exe にビルドします
rem 必要なもの: .NET SDK 10 (dotnet コマンド)。実行には .NET 10 デスクトップ ランタイムが必要です
where dotnet >nul 2>nul
if errorlevel 1 (
  echo dotnet コマンドが見つかりません。.NET SDK 10 をインストールしてください。
  pause
  exit /b 1
)
dotnet publish "gui\Tk2ConverterGui.csproj" -c Release -o "gui\publish" -nologo
if errorlevel 1 (
  echo.
  echo ビルドに失敗しました。上のエラー内容を確認してください。
  pause
  exit /b 1
)
echo.
echo ビルド成功: gui\publish\WintecTK2Converter.exe を作成しました。
pause
