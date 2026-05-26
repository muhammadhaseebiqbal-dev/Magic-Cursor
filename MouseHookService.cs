using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MagicCursor;

public class ShakeEventArgs : EventArgs
{
    public int X { get; set; }
    public int Y { get; set; }
}

public class MouseHookService : IDisposable
{
    // --- Win32 API Definitions ---
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    // --- Hook State ---
    private IntPtr _hookID = IntPtr.Zero;
    private LowLevelMouseProc _proc; // Keep reference to prevent GC

    // --- Shake Detection State ---
    private int _lastX = -1;
    private long _lastTime = 0;
    private int _direction = 0; // 1 for right, -1 for left
    private int _turnCount = 0;
    private long _firstTurnTime = 0;

    // Shake threshold configurations
    private const int MovementThreshold = 15; // Minimum pixels moved to count as intentional direction
    private const int RequiredTurns = 4; // How many rapid back-and-forth turns make a shake
    private const long ShakeTimeWindowMs = 600; // Time window to complete the turns

    public event EventHandler<ShakeEventArgs>? OnShakeDetected;

    public MouseHookService()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        _hookID = SetHook(_proc);
    }

    public void Stop()
    {
        if (_hookID != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookID);
            _hookID = IntPtr.Zero;
        }
    }

    private IntPtr SetHook(LowLevelMouseProc proc)
    {
        using (Process curProcess = Process.GetCurrentProcess())
        using (ProcessModule? curModule = curProcess.MainModule)
        {
            if (curModule?.ModuleName != null)
            {
                return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }
        return IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
        {
            MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            AnalyzeMovementForShake(hookStruct.pt.x, hookStruct.pt.y, hookStruct.time);
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    private void AnalyzeMovementForShake(int currentX, int currentY, uint timeMs)
    {
        long currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (_lastX == -1)
        {
            _lastX = currentX;
            _lastTime = currentTime;
            return;
        }

        int deltaX = currentX - _lastX;

        // Check if movement is significant enough to register a direction
        if (Math.Abs(deltaX) > MovementThreshold)
        {
            int currentDirection = Math.Sign(deltaX);

            // Did the direction change? (e.g. was moving left, now moving right)
            if (_direction != 0 && currentDirection != _direction)
            {
                if (_turnCount == 0)
                {
                    _firstTurnTime = currentTime;
                }

                _turnCount++;

                // If took too long between first turn and current turn, reset
                if (currentTime - _firstTurnTime > ShakeTimeWindowMs)
                {
                    _turnCount = 1; // Count this as the new first turn
                    _firstTurnTime = currentTime;
                }
                else if (_turnCount >= RequiredTurns)
                {
                    // SHAKE DETECTED! Pass the coordinates where it happened.
                    OnShakeDetected?.Invoke(this, new ShakeEventArgs { X = currentX, Y = currentY });
                    
                    // Reset to avoid multiple triggers
                    _turnCount = 0;
                    _lastX = -1; // Force full reset
                    return;
                }
            }

            _direction = currentDirection;
            _lastX = currentX;
            _lastTime = currentTime;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
