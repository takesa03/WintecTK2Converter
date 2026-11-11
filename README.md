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
  - ベスト（★ 全体 / ※ セッション内。ラップ・区間・最高速）を赤の太字で強調。遅い周回（スピンなど）はベストから除外
  - セッションの手動編集: 周回表の右クリックで、分割位置の指定と結合（停止の判定とずれるとき）。編集はログごとに保存
  - 周回の比較再生: 選んだ複数の周回をコース上で同時に再生し、速度と基準との差（秒）を比較
  - 保存した CSV を、ログなしでそのまま表示（読み取り専用）
  - 5Hz など高頻度の NMEA（他社ロガー）にも対応（ロガーが記録した速度を最高速・停止の判定に使用）

## ビルド
### WPF 版（画面付き）
`build_gui.bat` をダブルクリック（.NET SDK 10 が必要）。`gui\publish\WintecTK2Converter.exe` ができます。実行には .NET 10 デスクトップ ランタイムが必要です。

### コンソール版
`build.bat` をダブルクリック（Windows 標準の .NET Framework の C# コンパイラを使用）。`tk2nmea.exe` ができます。

## 使い方
- WPF 版: `WintecTK2Converter.exe` を起動し、`.tk2` ファイルまたはフォルダをウィンドウへドラッグ＆ドロップ（exe へのドロップでも開けます）。
- コンソール版: `.tk2` ファイルまたはフォルダを `tk2nmea.exe` にドラッグ＆ドロップ。詳細は [docs/USAGE.txt](docs/USAGE.txt)、
ファイル形式の解析は [docs/tk2_format_spec.md](docs/tk2_format_spec.md) を参照してください。

## バージョン
WPF 版は `Major.Minor.Build.Revision`（例: `v1.1.0.2`）。各ウィンドウのタイトルに表示します。
Major.Minor.Build は `gui/Tk2ConverterGui.csproj` の `VersionMajorMinorBuild` で手で管理し、Revision はビルドのたびに 1 つ増えます（`gui/revision.txt`）。
リリースには Git のタグ（`v1.1.0` など）を付けます。

## ライセンス
MIT License
