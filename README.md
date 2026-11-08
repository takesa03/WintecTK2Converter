# WintecTK2Converter

Wintec WBT-201 などの GPS データロガーの `.tk2` ログを、**日付を正しく補正**して
NMEA / Google Earth 用 KMZ に変換する Windows 用ツールです（コンソール版は C# 単一ファイル、追加インストール不要。WPF 版の画面もあります）。

TimeMachineX で変換すると日付が約19.7年（1024週）ずれる問題は、GPS 週番号ロールオーバーによるものです。
本ツールは `.tk2` を直接読み、日付を +1024 週して補正します。

## 主な機能
- `.tk2` → NMEA（GGA/RMC、チェックサム付き）
- Google Earth Pro 用 KMZ（時間再生の軌跡、速度色分け、1km ポスト、凡例、走行アニメーションツアー）
- 記録の前後カット、設定は `tk2nmea.ini`
- WPF 版（gui/）: 画面にファイルをドラッグ＆ドロップ → 記録範囲・停止区間を確認 → カット時刻や出力形式を選んで変換
- コース定義の作成（WPF 版）: 周回ログを平均化してコースの中心線・コントロールライン・セクターを作り、Google Earth に重ねて確認・保存
- ラップ・セクタータイム（WPF 版）: コース定義と走行ログから、ラップ・セクタータイムを計算。アウトラップ・ピットイン（ドライバー交代）でセッションを分けて表示し、CSV に保存

## ビルド
### WPF 版（画面付き）
`build_gui.bat` をダブルクリック（.NET SDK 10 が必要）。`gui\publish\WintecTK2Converter.exe` ができます。実行には .NET 10 デスクトップ ランタイムが必要です。

### コンソール版
`build.bat` をダブルクリック（Windows 標準の .NET Framework の C# コンパイラを使用）。`tk2nmea.exe` ができます。

## 使い方
- WPF 版: `WintecTK2Converter.exe` を起動し、`.tk2` ファイルまたはフォルダをウィンドウへドラッグ＆ドロップ（exe へのドロップでも開けます）。
- コンソール版: `.tk2` ファイルまたはフォルダを `tk2nmea.exe` にドラッグ＆ドロップ。詳細は [docs/USAGE.txt](docs/USAGE.txt)、
ファイル形式の解析は [docs/tk2_format_spec.md](docs/tk2_format_spec.md) を参照してください。

## ライセンス
MIT License
