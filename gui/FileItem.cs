using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Tk2Nmea;

namespace Tk2Gui
{
    /// <summary>一覧に並ぶ 1 つの .tk2 ファイル。</summary>
    public class FileItem : INotifyPropertyChanged
    {
        string _range = "読み込み中...";
        string _start = "";
        string _end = "";
        string _status = "";

        public string Path { get; private set; }
        public string Name { get { return System.IO.Path.GetFileName(Path); } }

        /// <summary>読み込み済みの記録 (読み込み失敗なら null)。</summary>
        internal Track Track { get; set; }

        /// <summary>要約 (記録範囲・停止区間)。選択時に画面右下へ表示する。</summary>
        public string Summary { get; set; }

        public string Range { get { return _range; } set { _range = value; Notify("Range"); } }

        /// <summary>カット開始時刻 (現地時刻)。空ならカットしない。</summary>
        public string Start { get { return _start; } set { _start = value ?? ""; Notify("Start"); } }

        /// <summary>カット終了時刻 (現地時刻)。空ならカットしない。</summary>
        public string End { get { return _end; } set { _end = value ?? ""; Notify("End"); } }

        public string Status { get { return _status; } set { _status = value; Notify("Status"); } }

        public FileItem(string path)
        {
            Path = path;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Notify(string name)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>ファイルを読み込み、記録範囲と要約を設定する (別スレッドから呼んでよい)。</summary>
        internal void Load(Options opt)
        {
            Track t = Program.LoadTrack(Path, opt);
            Track = t;
            Record[] r = t.Recs;
            CultureInfo inv = CultureInfo.InvariantCulture;
            Range = string.Format("{0} ～ {1}  ({2} 点)",
                (r[0].Time + t.Tz).ToString("yyyy-MM-dd HH:mm:ss", inv),
                (r[r.Length - 1].Time + t.Tz).ToString("HH:mm:ss", inv), r.Length);
            Summary = string.Join(Environment.NewLine, Program.SummaryLines(t, int.MaxValue));
        }
    }
}
