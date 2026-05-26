using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using System.Runtime.InteropServices;
using System;
using System.IO;
using WinUIEx;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MagicCursor;

public sealed partial class MainWindow : WindowEx
{
    // --- Win32 P/Invoke Definitions for Click-Through ---
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    public MainWindow()
    {
        InitializeComponent();

        // WinUIEx makes the window transparent via XAML Backdrop.
        // Now we just need to make it full screen, click-through, and TopMost.
        SetupOverlayWindow();

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private void SetupOverlayWindow()
    {
        var hWnd = this.GetWindowHandle();

        // Apply Win32 Extended Styles to make it click-through, layered, and a tool window (hides from Alt+Tab)
        int initialStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, initialStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);

        // Force the window to be TopMost
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

        // Maximize the window to cover the screen
        this.Maximize();
    }

    public IntPtr GetHWND()
    {
        return this.GetWindowHandle();
    }

    public void SetClickThrough(bool isClickThrough)
    {
        var hWnd = this.GetWindowHandle();
        int extendedStyle = GetWindowLong(hWnd, GWL_EXSTYLE);

        if (isClickThrough)
        {
            SetWindowLong(hWnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
        }
        else
        {
            SetWindowLong(hWnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
        }
    }
}