using System;
using System.Diagnostics;
using System.IO;

namespace Tk2Gui
{
    /// <summary>
    /// 重ねる表示の、画面のチェックで切り替える設定 (動画素材の書き出し・走行動画の時刻合わせ・動画で比較で共通)。
    /// どこかで変えると、開いているほかの画面のチェックと表示にもすぐ反映する
    /// </summary>
    public static class OverlayPrefs
    {
        const string GraphKey = "Overlay.Graph";
        static volatile bool _graph = Settings.Get(GraphKey) == "1";   // 描く側 (別スレッド) から読む

        /// <summary>速度のグラフ ([graph]) を出すか</summary>
        public static bool Graph { get { return _graph; } }

        public static event Action Changed;

        static void SetGraph(bool on)
        {
            if (_graph == on) return;
            _graph = on;
            Settings.Set(GraphKey, on ? "1" : "0");
            try { Settings.Save(); } catch (Exception) { }
            Action h = Changed;
            if (h != null) h();
        }

        /// <summary>チェックボックスを共通の設定につなぐ。changed は、この画面の表示を描き直す処理 (ほかの画面で変えたときも呼ぶ)</summary>
        public static void BindGraph(System.Windows.Controls.CheckBox cb, Action changed)
        {
            bool sync = false;
            cb.IsChecked = _graph;
            System.Windows.RoutedEventHandler h = (s, e) => { if (!sync) SetGraph(cb.IsChecked == true); };
            cb.Checked += h; cb.Unchecked += h;
            Action onChanged = () =>
            {
                sync = true;
                try { cb.IsChecked = _graph; } finally { sync = false; }
                if (changed != null) changed();
            };
            Changed += onChanged;
            cb.Unloaded += (s, e) => { Changed -= onChanged; };
        }
    }

    /// <summary>走行動画の画面で共通に使うもの</summary>
    public static class VideoKit
    {
        /// <summary>動画素材の書き出しと同じレイアウト (書き出し画面で最後に選んだもの、無ければ既定)</summary>
        public static OverlayLayout LoadLayout()
        {
            string p = Settings.Get("Overlay.Layout");
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) p = OverlayLayout.DefaultPath;
            return OverlayLayout.Load(p);
        }

        public static OverlayOptions Options(OverlayLayout l)
        {
            OverlayOptions o = new OverlayOptions();
            if (l != null)
            {
                o.LapHoldSec = l["laptime"].Num("hold", 3);
                o.SectorHoldSec = l["sector"].Num("hold", 3);
                o.GSmoothSec = l["gmeter"].Num("smooth", 1.0);
                o.SpeedSmoothSec = l["speedometer"].Num("smooth", 2.0);
            }
            return o;
        }

        /// <summary>周回の短い名前 (S6-L3、S2-H1、S6-OUT など)</summary>
        public static string Label(SessionRecord s, LapRecord r)
        {
            string k = r.IsGymRun ? "H" + r.LapNo : r.Type == LapType.Normal ? "L" + r.LapNo : r.Type.ToString().ToUpperInvariant();
            return "S" + s.No + "-" + k;
        }

        public static string Clock(double sec)
        {
            if (sec < 0) return "-" + Clock(-sec);
            int m = (int)(sec / 60);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00.000}", m, sec - m * 60);
        }

        /// <summary>ログの通算秒 → 日本時間の時刻</summary>
        public static string Local(double t)
        {
            return new DateTime(2000, 1, 1).AddSeconds(t).AddHours(9).ToString("HH:mm:ss.fff");
        }
    }

    /// <summary>再生の時計。Play 中は経過時間 × 速度で進む</summary>
    public sealed class PlayClock
    {
        readonly Stopwatch _sw = new Stopwatch();
        double _base;
        public double Speed = 1;
        public bool Playing { get { return _sw.IsRunning; } }
        public double Now { get { return _base + _sw.Elapsed.TotalSeconds * Speed; } }
        public void Set(double t) { _base = t; if (_sw.IsRunning) _sw.Restart(); else _sw.Reset(); }
        public void Play() { if (!_sw.IsRunning) { _sw.Restart(); } }
        public void Pause() { if (_sw.IsRunning) { _base = Now; _sw.Reset(); } }
        public void SetSpeed(double s) { double t = Now; Speed = s; Set(t); }
    }
}
