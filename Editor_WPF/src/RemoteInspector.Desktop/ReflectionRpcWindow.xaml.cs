using System.Windows;
using RemoteInspector.Core;
namespace RemoteInspector.Desktop;
public partial class ReflectionRpcWindow : Window
{
    public ReflectionRpcView View { get; }
    public ReflectionRpcWindow(InspectorSession session, ReflectionRpcCache cache)
    { InitializeComponent(); View = new ReflectionRpcView(session, cache); Content = View; Closed += (_, _) => View.Dispose(); }
    public new object FindName(string name) => View.FindName(name) ?? base.FindName(name);
}
