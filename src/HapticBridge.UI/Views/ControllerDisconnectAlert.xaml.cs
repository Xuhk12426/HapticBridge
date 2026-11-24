using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace HapticBridge.UI.Views;

public partial class ControllerDisconnectAlert : Window
{
    private readonly System.Drawing.Rectangle _workingArea;
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromSeconds(9) };

    public ControllerDisconnectAlert(string detail)
    {
        _workingArea = Forms.Screen.FromHandle(GetForegroundWindow()).WorkingArea;
        InitializeComponent();
        DetailText.Text = detail;
        Loaded += PositionAndAnnounce;
        _dismiss.Tick += (_, _) => Close();
        Closed += (_, _) => _dismiss.Stop();
    }

    private void PositionAndAnnounce(object sender, RoutedEventArgs args)
    {
        Loaded -= PositionAndAnnounce;
        nint window = new WindowInteropHelper(this).Handle;
        const int extendedStyle = -20;
        const int noActivate = 0x08000000;
        nint style = GetWindowLongPtrW(window, extendedStyle);
        SetWindowLongPtrW(window, extendedStyle, style | noActivate);

        if (GetWindowRect(window, out var rectangle))
        {
            const int margin = 16;
            const uint preserveSizeAndOrderWithoutActivation = 0x0015;
            int left = _workingArea.Right - (rectangle.Right - rectangle.Left) - margin;
            int top = _workingArea.Bottom - (rectangle.Bottom - rectangle.Top) - margin;
            SetWindowPos(window, 0, left, top, 0, 0, preserveSizeAndOrderWithoutActivation);
        }

        _dismiss.Start();
        SystemSounds.Exclamation.Play();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
