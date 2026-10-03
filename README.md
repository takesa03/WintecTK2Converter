# WintecTK2Converter

Wintec WBT-201 などの GPS データロガーの `.tk2` ログを、**日付を正しく補正**して
NMEA / Google Earth 用 KMZ に変換する Windows 用ツールです（C# 単一ファイル、追加インストール不要）。

TimeMachineX で変換すると日付が約19.7年（1024週）ずれる問題は、GPS 週番号ロールオーバーによるものです。
本ツールは `.tk2` を直接読み、日付を +1024 週して補正します。

## 主な機能
- `.tk2` → NMEA（GGA/RMC、チェックサム付き）
- Google Earth Pro 用 KMZ（時間再生の軌跡、速度色分け、1km ポスト、凡例、走行アニメーションツアー）
- 記録の前後カット、設定は `tk2nmea.ini`

## ビルド
`build.bat` をダブルクリック（Windows 標準の .NET Framework の C# コンパイラを使用）。`tk2nmea.exe` ができます。

## 使い方
`.tk2` ファイルまたはフォルダを `tk2nmea.exe` にドラッグ＆ドロップ。詳細は [docs/USAGE.txt](docs/USAGE.txt)、
ファイル形式の解析は [docs/tk2_format_spec.md](docs/tk2_format_spec.md) を参照してください。

## ライセンス
MIT License
