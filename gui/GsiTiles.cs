using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Tk2Gui
{
    /// <summary>
    /// 国土地理院の地理院タイル「全国最新写真 (シームレス)」の読み込み (比較再生・コース定義の画面で共用)。
    /// 一度取ってきた写真は %LOCALAPPDATA%\WintecTK2Converter\tiles に保存して使い回す。
    /// </summary>
    public static class GsiTiles
    {
        public const string Credit = "出典: 国土地理院 (地理院タイル・全国最新写真 (シームレス))";
        const string TileUrl = "https://cyberjapandata.gsi.go.jp/xyz/seamlessphoto/{0}/{1}/{2}.jpg";
        static readonly string TileCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WintecTK2Converter", "tiles", "seamlessphoto");
        static readonly HttpClient Http = MakeHttp();
        static readonly SemaphoreSlim Downloads = new SemaphoreSlim(4);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static HttpClient MakeHttp()
        {
            HttpClient c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WintecTK2Converter/1.0");
            return c;
        }

        public static int TileX(double lon, int z) { return (int)Math.Floor((lon + 180) / 360 * (1 << z)); }
        public static int TileY(double lat, int z)
        {
            double r = lat * Math.PI / 180;
            return (int)Math.Floor((1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * (1 << z));
        }
        public static double TileLon(int x, int z) { return (double)x / (1 << z) * 360 - 180; }
        public static double TileLat(int y, int z) { return Math.Atan(Math.Sinh(Math.PI * (1 - 2.0 * y / (1 << z)))) * 180 / Math.PI; }

        /// <summary>画面の 1 px が mpp [m] のときに合うズームレベル (10〜18)。</summary>
        public static int ZoomFor(double lat, double mpp)
        {
            int z = (int)Math.Round(Math.Log(156543.034 * Math.Cos(lat * Math.PI / 180) / mpp, 2));
            return Math.Max(10, Math.Min(18, z));
        }

        /// <summary>
        /// 写真を 1 枚読む。保存してあればそれを使い、なければ国土地理院から取ってきて保存する。
        /// stillNeeded が false を返したら、取りに行かない (拡大率が変わってもう要らないなど)。
        /// 写真がない場所 (海など)・通信できないときは null。
        /// </summary>
        public static async Task<BitmapSource> LoadAsync(int z, int x, int y, Func<bool> stillNeeded)
        {
            string path = Path.Combine(TileCache, z.ToString(Inv), x.ToString(Inv), y.ToString(Inv) + ".jpg");
            try
            {
                byte[] data;
                if (File.Exists(path)) data = await Task.Run(() => File.ReadAllBytes(path));
                else
                {
                    await Downloads.WaitAsync();
                    try
                    {
                        if (stillNeeded != null && !stillNeeded()) return null;
                        data = await Http.GetByteArrayAsync(string.Format(Inv, TileUrl, z, x, y));
                    }
                    finally { Downloads.Release(); }
                    string dir = Path.GetDirectoryName(path);
                    byte[] d = data;
                    await Task.Run(() => { Directory.CreateDirectory(dir); File.WriteAllBytes(path, d); });
                }
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = new MemoryStream(data);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
