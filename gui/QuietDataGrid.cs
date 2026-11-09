using System.Collections.Generic;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Tk2Gui
{
    /// <summary>
    /// 中身 (行・セル) を UI オートメーション (画面読み上げや画面の部品を監視するソフトが使う仕組み) に見せない DataGrid。
    /// 通常の DataGrid は、結果を入れ替えるたびに全行・全セルの部品が作られて外部ソフトに通知され、
    /// 監視しているソフトがあると画面が数十秒「応答なし」になることがあるため。表の操作 (キーボード・マウス) は変わらない。
    /// </summary>
    public class QuietDataGrid : DataGrid
    {
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new QuietPeer(this);
        }

        sealed class QuietPeer : FrameworkElementAutomationPeer
        {
            public QuietPeer(DataGrid owner) : base(owner) { }
            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.Table; }
            protected override string GetClassNameCore() { return "DataGrid"; }
            protected override List<AutomationPeer> GetChildrenCore() { return new List<AutomationPeer>(); }
        }
    }
}
