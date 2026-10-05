using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace BiliGet;

public partial class App : Application
{
    private Window? _window;
    private static Mutex? _mutex;
    private const string MutexName = "OTAKUsVget_SingleInstance_9F3A71";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 已有一个实例在运行：把它的窗口带到前台，然后退出本次启动
            var h = FindWindow(null, "OTAKUsVget");
            if (h != IntPtr.Zero)
            {
                ShowWindow(h, 9);            // SW_RESTORE
                SetForegroundWindow(h);
            }
            Environment.Exit(0);
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
