# Wintec TK2 ファイルフォーマット解析結果（サンプル 20070210_070806.tk2 より）

全体: 1024バイトのヘッダ + 16バイト固定長レコード × N。すべてリトルエンディアン。
サンプル: 71600 = 1024 + 16 × 4411。NMEA出力の4411点と一致。

## ヘッダ (0x000–0x3FF)
| offset | 型 | 内容 | サンプル値 |
|---|---|---|---|
| 0 | char[12] | マジック "WintecLogTk2" | |
| 16 | float×3 | バージョン類?（推定） | 1.0, 1.305, 1.1 |
| 30 | char[] | 機種名 | "WBT201A" |
| 50 | char[] | ユーザー名 | "（ユーザー名）" |
| 70 | char[] | シリアル? | "YYMM1234567890" |
| 110 | char[] | ファイル作成(変換)日時 PCローカル | "2026_09_26_18:17:03" |
| 430–431 | u8,u8 | 不明 / タイムゾーン(+9)?（推定） | 0x01, 0x09 |
| 433 | char[] | トラック開始日時(ローカル, ISO風) ※ロールオーバーで誤り | "2007-02-10T07:08:06Z+09:00" |
| 460 | u32 | レコード数 | 4411 |
| 464 | u32 | 所要時間[秒] | 5411 (=1:30:11) |
| 468 | u32 | 総距離[m]?（推定） | 80161 |
| 472 | u32 | トラック数?（推定） | 1 |

## レコード (16 bytes, offset 1024 + 16*i)
| offset | 型 | 内容 |
|---|---|---|
| 0 | u16 | フラグ: 1=トラック開始, 2=トラック終了, 0=通常（他ビットはPOI等の可能性） |
| 2 | u32 | 日時(UTC)ビットパック: 秒 bit0-5 / 分 6-11 / 時 12-16 / 日 17-21 / 月 22-25 / 年-2000 26-31 |
| 6 | i32 | 緯度 ×1e-7 度（南は負） |
| 10 | i32 | 経度 ×1e-7 度（西は負） |
| 14 | i16 | 高度 [m] |

速度(ノット)・進行方向はファイルに無く、TimeMachineXが前後点から計算してNMEAに出力している。
全4411点で緯度・経度・高度がNMEA出力と一致することを確認済み。

## 日付ずれの原因
GPS週番号ロールオーバー（1024週 = 7168日）。2007-02-09 + 7168日 = 2026-09-25（UTC）。
→ 修正は「日付に 7168 日を加算」でよい（時刻はそのまま）。

## 注意: RMCの日付はUTC
開始時刻は UTC 2026-09-25 22:08:06 = JST 2026-09-26 07:08:06。
NMEA規格では GPRMC の日付はUTCなので正しくは 250926。
現状の _mod.nmea は 260926（JST日付）にしている。UTC 15:00 を跨ぐログでは日付の扱いに注意。

## TimeMachineX の NMEA 出力仕様（逆解析）
1点につき2行、CRLF改行:
- `$GPRMC,hhmmss,A,ddmm.mmmmmm,N,dddmm.mmmmmm,E,速度(kn,%.1f),方位(%.1f),ddmmyy,,,,*CS`
- `$GPGGA,hhmmss,ddmm.mmmmmm,N,dddmm.mmmmmm,E,1,,,高度(整数),M,,M,,*CS`
- 速度: 直前の点とのハバーサイン距離（地球半径 6378137 m）÷ 時間差、ノット、小数1桁四捨五入。先頭点は 0.0。
- 方位: 大圏初期方位を 0–360 に正規化して整数に切り捨て。先頭点は 0.0、直前と同一座標なら 360.0。
- 緯度経度の分は6桁。TimeMachineX は倍精度演算の切り捨てで末尾が1小さくなることがある（最大 1e-6 分 ≈ 2mm）。

## 変換ツール tk2nmea.exe
保存場所: D:\Original\Claude\WintecTK2_Converter
- tk2nmea_cs\ : C# 版（本命）。Windows 標準の .NET Framework 4.x csc.exe で build.bat からビルド（C# 5 文法）。
- tk2nmea\ : 最初に作った Go 版（参照用）。
- tk2 をドラッグ&ドロップ → 同じフォルダに `YYYYMMDD_HHMMSS.nmea`（補正後の現地開始時刻）を出力。
- 2019-04-07 より前の日付に 1024 週単位で自動加算。チェックサムは再計算。
- 既定は RMC 日付を UTC で出力。`-localdate` で現地日付（_mod.nmea と同じ形式）。`-weeks N` / `-o DIR` / `-nopause`。
- 検証: Go 版の -localdate 出力を _mod.nmea と全行照合 → 日付・時刻・高度・方位・チェックサム一致。
  差は緯度経度末尾1桁（上記の切り捨て誤差、ツール側が正確）と速度15点の ±0.1kn のみ。
- C# 版（ユーザー環境でビルド）の出力は Go 版の出力とバイト単位で完全一致（2026-09-26 確認）。

## TimeMachineX の KMZ (GE_*.kmz) の構造（逆解析）
KMZ 内に KML 1 個。フォルダ: Track / Way_Point / Track_Point / Speed_Track(Km) / Km_Posts / Mile_Posts。
- Speed_Track(Km): 速度 [km/h] = floor(ハバーサイン距離 / 時間差 × 3.6)。20km/h 刻みの 7 帯
  （0-19 ffd355ba, 20-39 ffff0000, 40-59 ffffce87, 60-79 ff00ff7f, 80-99 ff00ffff, 100-119 ff007fff, 120- ff0000ff、KML の aabbggrr）。
  帯が変わる点で LineString を区切り、区間の終点 = 次区間の始点（線をつなぐため 1 点重複）。幅 5。
- Km_Posts: 累積距離（ハバーサイン, 6378137m）が 1km の倍数になる位置を前後 2 点の緯度経度で直線補間。名前 "N Km"。
- Mile_Posts も同様（1 mile ごと）。Track_Point は全点の Placemark（時刻・速度・方位・累積距離の説明付き）。

## KMZ/KML 出力（C# 版）
NMEA と同じ名前で `YYYYMMDD_HHMMSS.kmz` を出力（`-kml` で KML、`-nokml` で抑止）。KMZ = doc.kml + legend.png（凡例, System.Drawing で描画）。
- スタート / ゴール目印、走行軌跡 (gx:Track, 時間スライダー再生・線は透明・速度[km/h]を ExtendedData に格納)。
- Speed_Track(Km), Km_Posts は上記 TimeMachineX と同じ計算。検証: 参照実装で TimeMachineX の出力と比較し、
  Speed_Track 361 区間すべて色・点数が一致、Km_Posts 80 本の位置差は最大 0.18 m。
- 区間カット: 開始/終了の現地時刻を指定（ドロップ時に対話入力、または -start / -end）。
  カットは NMEA と KMZ の両方に適用し、距離・Km ポストはカット後のスタートから数える。
  入力の補助として、記録が 60 秒以上途切れた「停止区間」を一覧表示する。
- 座標は 1e-7 度の整数から10進度7桁を整数演算で作成。
- C# 版（ユーザー環境でビルド）の KMZ を参照実装と照合: Speed_Track 359 区間・Km_Posts 79 本とも一致（2026-09-26）。

## 見た目の設定（tk2nmea.ini, exe と同じフォルダ。コマンドライン -kmicon 等が優先）
- Speed_Track: speedstep [km/h]（既定 5）ごとに帯を分け、speedmax（既定 120）以上は最高色。
  色は TimeMachineX の 7 色（紫 186,85,211 / 青 0,0,255 / 水色 135,206,255 / 緑 127,255,0 / 黄 255,255,0 / 橙 255,127,0 / 赤 255,0,0）を
  0～speedmax に等間隔で置き線形補間。speedstep=20, speedmax=120 で TimeMachineX と同一。
  凡例は帯 8 個以下なら 1 帯 1 行、それ以上はカラーバー + 目盛り。
- Km_Posts: kmicon（pin, flag, star, circle, square, diamond, triangle, target, donut, dot, paddle = Google Earth 標準の白アイコン、
  URL、または png ファイル → KMZ に同梱）、kmcolor（名前 / #RRGGBB / aabbggrr）で IconStyle と LabelStyle に色付け、
  kmscale（既定 1.3）、kmlabel（既定 1.2）。見本 kmicon_samples.kmz。

## 高さの表示（altitude）
- Google Earth の地形（DEM）には橋・高架が無いため、clampToGround だと橋の上の走行が海面に描かれる。
- 既定 altitude=gps: 全要素（gx:Track, Speed_Track, Km_Posts, スタート/ゴール）を altitudeMode=absolute、
  高さ = GPS 高度の時間移動平均（altsmooth 秒, 既定 7）+ altoffset [m]。extrude=1 で地面まで幕。
- サンプルの GPS 高度: 名港中央大橋付近で 46→76→58 m と橋の形を捉えるが、平地の高架でも 40 m 台など全体に 10～20 m 高め
  （WBT-201 の垂直精度の限界）。altoffset で調整。
- altitude=ground で groundoffset > 0 のとき、全要素を altitudeMode=relativeToGround・高さ = groundoffset [m] にして地表から浮かせる（gx:Track・Speed_Track・Km_Posts・スタート/ゴール・ツアーのカメラ注視点）。GPS 高度は使わないので、橋の上でも地形（海面）からの高さのまま。
- 地形データ（国土地理院 DEM 等）と組み合わせた自動補正は未実装。

## 走行アニメーション（gx:Tour, tour=1 既定）
- tourstep 秒（既定 2）ごとに gx:FlyTo(smooth, duration = tourstep / tourspeed)。最初の 1 つは bounce 3 秒。
- 各 FlyTo の LookAt: 位置 = その時刻の車（前後の記録点から直線補間）、heading = 前後 5 秒の位置から求めた進行方向
  （5 m 未満の移動なら直前の向き。tourheading=north で 0 固定）、tilt = tourtilt、range = tourrange、
  gx:TimeSpan [スタート, その時刻] → 通過済みの要素だけ表示、gx:Track のアイコンがその時刻の位置に出る。
- Speed_Track はツアー時は記録点ごと（通常 1 秒）に区切り、各区間に TimeSpan begin = 区間終点の時刻 − tourlead 秒（既定 0: 車が通過した点まで描く。先端は最大 1 点ぶん車の後ろ。+1 にすると先端が車の前へ出る）。
  （2026-10-03: 以前は 3 点ごと・begin = 終点で最大約 2 秒遅れ → 記録点ごと・begin = 始点にしたら車より先行 → begin = 終点 + tourlead で調整可能に）
- tourseg = N (>1) のとき、記録点の間を N 等分した小区間ごとに Speed_Track を分け、各小区間の TimeSpan begin = その終点の時刻（小数秒 .fff 付き）− tourlead。線が 1/N 秒刻みで伸びる（記録点の間は直線補間, 色は元の区間と同じ）。小数秒の時刻を Google Earth が解釈できない場合は tourseg=1 に戻す。
- trackline=1（単色の線モード）: 走行軌跡 (gx:Track) 自身に LineStyle（trackcolor, trackwidth）を付けて表示する。Google Earth がタイムスパンに合わせて線を車の位置（補間済み）まで描くため滑らか。Speed_Track(Km) フォルダは visibility=0（非表示。チェックすれば静的な色分けとして表示可）で、ツアー用の分割・TimeSpan は付けない。ツアーのカメラ・Km ポスト・スタート/ゴールの時間表示は従来どおり。
- carname: gx:Track の Placemark 名 = マーカー（track.png）の下に出るラベル。LabelStyle の scale = carlabel。空なら名前は「走行軌跡 (時間再生)」でラベルは出さない（scale 0）。
- カメラの向き: 前後 tourheadwin 秒（既定 5）の位置から進行方向を求め、tourheadtau 秒（既定 0 = すぐ向く）の遅れで追従（kf = 1 − exp(−tourstep / tau)、角度は直前の向きから最短で回す）。heading は 360 をまたいでも連続した値（361 など）で出力し、北をまたぐときの一回転を防ぐ。
  Km ポストは通過時刻、スタート/ゴールはその時刻から表示。
- 動画化: Google Earth Pro「ツール → ムービーメーカー」でツアーを録画。
