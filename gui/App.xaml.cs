using System.Reflection;
using System.Windows;

namespace Tk2Gui
{
    public partial class App : Application
    {
        /// <summary>ウィンドウのタイトルに付けるバージョン (例: v1.1.0.12)</summary>
        public static string VersionText
        {
            get
            {
                System.Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "" : "v" + v;
            }
        }
    }
}
