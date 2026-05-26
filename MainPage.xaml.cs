using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace MagicCursor;

// COM interface needed for unsafe SoftwareBitmap pixel access
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

public sealed partial class MainPage : Page
{
    private MouseHookService _mouseHook;
    private DispatcherTimer _hideTimer;
    private bool _isMenuActive = false;
    private Random _random = new Random();
    private GeminiService _geminiService;

    // Drag-to-highlight state
    private bool _isDragging = false;
    private double _startX = 0;
    private double _startY = 0;
    private string _capturedText = string.Empty;
    private byte[]? _capturedImageBytes = null;
    private SoftwareBitmap? _capturedBitmap = null;

    // --- Cached OCR engine (avoids re-creation on every capture) ---
    private OcrEngine? _cachedOcrEngine;

    // --- Sparkle throttle: prevent excessive particle spawning ---
    private long _lastSparkleTickMs = 0;
    private const int SparkleIntervalMs = 40;   // ~25 sparkles/sec max
    private const int MaxSparkleParticles = 30; // cap visual tree size

    // --- Pre-compiled regex for inline Markdown (reused per TextBlock) ---
    private static readonly System.Text.RegularExpressions.Regex InlineMarkdownRegex =
        new(@"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // --- Pre-compiled regex for numbered list detection ---
    private static readonly System.Text.RegularExpressions.Regex NumberedListRegex =
        new(@"^\d+[\.\)]\s", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex NumberedListCapture =
        new(@"^(\d+[\.\)])\s(.*)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // --- Cached brushes & fonts (avoid per-element allocation) ---
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush AccentBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x00, 0xBF, 0xFF));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush PurpleBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x7B, 0x68, 0xEE));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush CodeBlockBgBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush CodeTextBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xCC, 0xDD, 0xFF));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush InlineCodeBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0xAA, 0x55));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SeparatorBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SelectionFillBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(0x10, 0x00, 0xBF, 0xFF));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush DefaultSelectionFill =
        new(Microsoft.UI.ColorHelper.FromArgb(0x40, 0x00, 0xBF, 0xFF));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WhiteBrush =
        new(Microsoft.UI.Colors.White);
    private static readonly Microsoft.UI.Xaml.Media.FontFamily MonoFont =
        new("Cascadia Code, Consolas, monospace");

    // Win32 API to get DPI scaling factor for accurate UI positioning
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // Win32 APIs for Screen Capture
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int dx, int dy, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight;
        public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public uint[]? bmiColors;
    }

    const int SRCCOPY = 0x00CC0020;
    const int DIB_RGB_COLORS = 0;

    public MainPage()
    {
        InitializeComponent();

        var config = ConfigService.LoadConfig();
        _geminiService = new GeminiService(config.GeminiApiKey);

        _hideTimer = new DispatcherTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(30);
        _hideTimer.Tick += HideTimer_Tick;

        _mouseHook = new MouseHookService();
        _mouseHook.OnShakeDetected += MouseHook_OnShakeDetected;

        this.Loaded += MainPage_Loaded;
        this.Unloaded += MainPage_Unloaded;

        // Add PointerMoved event handler with handledEventsToo = true
        RootCanvas.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(RootCanvas_PointerMoved), true);
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        _mouseHook.Start();

        var config = ConfigService.LoadConfig();

        // Sync registry startup path in case the app was moved to a new folder
        if (config.RunAtStartup)
        {
            SetStartup(true);
        }

        // If no API key configured on startup, show settings modal immediately
        if (!_geminiService.IsInitialized)
        {
            ShowSettings(isFirstRun: true);
        }
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _mouseHook.Stop();
        _mouseHook.Dispose();
    }

    private void MouseHook_OnShakeDetected(object? sender, ShakeEventArgs e)
    {
        // Don't show menu if response modal or settings is open
        if (ResponseModal.Visibility == Visibility.Visible || SettingsModal.Visibility == Visibility.Visible) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            try 
            {
                _hideTimer.Stop();
                _isMenuActive = true;
                _capturedText = string.Empty;
                CustomQueryInput.Text = string.Empty;
                _capturedBitmap?.Dispose();
                _capturedBitmap = null;
                _capturedImageBytes = null;

                IntPtr hwnd = App.AppWindow.GetHWND();
                double scaleFactor = GetDpiForWindow(hwnd) / 96.0;

                double logicalX = e.X / scaleFactor;
                double logicalY = e.Y / scaleFactor;

                AdjustMenuPosition(logicalX + 15, logicalY + 15);
                
                // Set initial position of the custom cursor exactly on the pointer tip
                Canvas.SetLeft(CustomCursor, logicalX);
                Canvas.SetTop(CustomCursor, logicalY);

                App.AppWindow.SetClickThrough(false);
                
                // The ultimate WinUI 3 trick to completely hide the cursor:
                var invisibleCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
                invisibleCursor.Dispose();
                this.ProtectedCursor = invisibleCursor;

                // Also override cursor for CustomQueryInput so it does not show caret when hovered
                CustomQueryInput.CustomCursor = invisibleCursor;

                CustomCursor.Opacity = 1;

                MagicMenu.IsHitTestVisible = true;
                ShowPopupAnimation.Begin();

                _hideTimer.Start();
            }
            catch (Exception ex)
            {
                string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MagicCursorCrashLog.txt");
                File.AppendAllText(logPath, $"SHAKE DETECTED CRASH: {ex.Message}\n{ex.StackTrace}\n");
            }
        });
    }

    private void AdjustMenuPosition(double x, double y)
    {
        try 
        {
            // Ensure menu stays within RootCanvas bounds
            double menuWidth = 220; // Fixed width from XAML
            double estimatedMenuHeight = 220; 

            double canvasWidth = RootCanvas.ActualWidth;
            double canvasHeight = RootCanvas.ActualHeight;

            // If dimensions are not yet available, use safe defaults or skip adjustment
            if (canvasWidth > 0 && canvasHeight > 0)
            {
                if (x + menuWidth > canvasWidth) x = canvasWidth - menuWidth - 20;
                if (y + estimatedMenuHeight > canvasHeight) y = canvasHeight - estimatedMenuHeight - 20;
            }
            
            if (x < 10) x = 10;
            if (y < 10) y = 10;

            Canvas.SetLeft(MagicMenu, x);
            Canvas.SetTop(MagicMenu, y);
        }
        catch (Exception ex)
        {
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MagicCursorCrashLog.txt");
            File.AppendAllText(logPath, $"AdjustMenuPosition CRASH: {ex.Message}\n{ex.StackTrace}\n");
        }
    }

    private async void CustomQueryInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(CustomQueryInput.Text))
        {
            await RunAI(CustomQueryInput.Text + "\n\nContext from screen: ", "✨ Processing query…");
        }
    }

    private void HideTimer_Tick(object? sender, object e)
    {
        CloseMenu();
    }

    private void CloseMenu()
    {
        _hideTimer.Stop();
        _isMenuActive = false;
        _isDragging = false;
        _capturedBitmap?.Dispose();
        _capturedBitmap = null;
        _capturedImageBytes = null;
        
        HidePopupAnimation.Begin();
        MagicMenu.IsHitTestVisible = false;
        CustomCursor.Opacity = 0;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        ScanningAnimation.Stop();

        // Restore Selection Rectangle properties
        SelectionRectangle.StrokeThickness = 3;
        SelectionRectangle.Fill = DefaultSelectionFill;

        // Restore standard OS cursor by assigning a fresh, valid cursor
        this.ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        CustomQueryInput.CustomCursor = null; // Revert CustomQueryInput cursor back to default

        // Only restore click-through if modal isn't open
        if (ResponseModal.Visibility == Visibility.Collapsed)
        {
            App.AppWindow.SetClickThrough(true);
        }
    }

    private void RootCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMenuActive)
        {
            var point = e.GetCurrentPoint(RootCanvas).Position;
            
            _isDragging = true;
            _startX = point.X;
            _startY = point.Y;

            // Reset rectangle
            Canvas.SetLeft(SelectionRectangle, _startX);
            Canvas.SetTop(SelectionRectangle, _startY);
            SelectionRectangle.Width = 0;
            SelectionRectangle.Height = 0;
            SelectionRectangle.Visibility = Visibility.Visible;

            // Prevent timer from closing menu while they are highlighting
            _hideTimer.Stop();

            // Capture the pointer to ensure we get release events even if they drag outside
            RootCanvas.CapturePointer(e.Pointer);
        }
    }

    private void RootCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isMenuActive)
        {
            var point = e.GetCurrentPoint(RootCanvas).Position;
            
            // Update the custom cursor position to follow the mouse perfectly
            Canvas.SetLeft(CustomCursor, point.X);
            Canvas.SetTop(CustomCursor, point.Y);

            if (_isDragging)
            {
                // Update selection highlight box dimensions
                double x = Math.Min(point.X, _startX);
                double y = Math.Min(point.Y, _startY);
                double w = Math.Abs(point.X - _startX);
                double h = Math.Abs(point.Y - _startY);

                Canvas.SetLeft(SelectionRectangle, x);
                Canvas.SetTop(SelectionRectangle, y);
                SelectionRectangle.Width = w;
                SelectionRectangle.Height = h;
            }
            else
            {
                // Only spawn sparkles if they are NOT dragging (to keep the highlight clean)
                SpawnSparkle(point.X, point.Y);
            }

            if (!_isDragging && ResponseModal.Visibility == Visibility.Collapsed)
            {
                _hideTimer.Stop();
                _hideTimer.Start();
            }
        }
    }

    private async void RootCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            RootCanvas.ReleasePointerCapture(e.Pointer);

            // If it was just a tiny click (no real dragging), assume they wanted to dismiss the menu
            if (SelectionRectangle.Width < 5 && SelectionRectangle.Height < 5)
            {
                CloseMenu();
            }
            else
            {
                // They successfully highlighted a region!
                // Snap the magic menu near the bottom-right of their selection
                var point = e.GetCurrentPoint(RootCanvas).Position;
                AdjustMenuPosition(point.X + 15, point.Y + 15);
                
                // Keep the menu alive so they can click 'Analyze Text'
                _hideTimer.Start();

                // Capture Text
                IntPtr hwnd = App.AppWindow.GetHWND();
                double scaleFactor = GetDpiForWindow(hwnd) / 96.0;
                double selLeft = Canvas.GetLeft(SelectionRectangle);
                double selTop = Canvas.GetTop(SelectionRectangle);
                int sx = (int)(selLeft * scaleFactor);
                int sy = (int)(selTop * scaleFactor);
                int sw = (int)(SelectionRectangle.Width * scaleFactor);
                int sh = (int)(SelectionRectangle.Height * scaleFactor);

                SelectionRectangle.Visibility = Visibility.Collapsed;
                
                await CaptureTextAsync(sx, sy, sw, sh);

                SelectionRectangle.Visibility = Visibility.Visible;
            }
        }
    }

    private byte[]? ScreenCapture(int x, int y, int w, int h)
    {
        IntPtr hdcScreen = IntPtr.Zero, hdcMem = IntPtr.Zero, hBmp = IntPtr.Zero;
        try
        {
            hdcScreen = GetDC(IntPtr.Zero);
            hdcMem    = CreateCompatibleDC(hdcScreen);
            hBmp      = CreateCompatibleBitmap(hdcScreen, w, h);
            SelectObject(hdcMem, hBmp);
            BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, SRCCOPY);

            var bi = new BITMAPINFO();
            bi.bmiHeader.biSize        = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bi.bmiHeader.biWidth       = w;
            bi.bmiHeader.biHeight      = -h; // top-down
            bi.bmiHeader.biPlanes      = 1;
            bi.bmiHeader.biBitCount    = 32;
            bi.bmiHeader.biCompression = 0;

            var pixels = new byte[w * h * 4];
            GetDIBits(hdcMem, hBmp, 0, (uint)h, pixels, ref bi, DIB_RGB_COLORS);
            return pixels;
        }
        finally
        {
            if (hBmp      != IntPtr.Zero) DeleteObject(hBmp);
            if (hdcMem    != IntPtr.Zero) DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private async Task CaptureTextAsync(int sx, int sy, int sw, int sh)
    {
        try
        {
            byte[]? pixels = ScreenCapture(sx, sy, sw, sh);
            if (pixels is null) 
            { 
                _capturedText = "[capture failed]"; 
                _capturedBitmap?.Dispose();
                _capturedBitmap = null;
                _capturedImageBytes = null;
                return; 
            }

            var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, sw, sh, BitmapAlphaMode.Premultiplied);
            bmp.CopyFromBuffer(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(pixels));

            // Keep the bitmap reference for potential on-demand image analysis encoding
            _capturedBitmap?.Dispose();
            _capturedBitmap = bmp;
            _capturedImageBytes = null;

            // Reuse cached OCR engine — TryCreate is expensive (loads language packs)
            _cachedOcrEngine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            if (_cachedOcrEngine is null) 
            { 
                _capturedText = "[OCR unavailable]"; 
                return; 
            }

            var result = await _cachedOcrEngine.RecognizeAsync(bmp);
            _capturedText = string.IsNullOrWhiteSpace(result.Text) ? "[no text found]" : result.Text;
        }
        catch (Exception ex)
        {
            _capturedText = $"[error: {ex.Message}]";
            _capturedBitmap?.Dispose();
            _capturedBitmap = null;
            _capturedImageBytes = null;
        }
    }

    private async Task<byte[]> GetImageBytesAsync(SoftwareBitmap softwareBitmap)
    {
        using (var stream = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }
            return bytes;
        }
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        string prompt = string.IsNullOrWhiteSpace(CustomQueryInput.Text) 
            ? "Analyze this text. Provide highly concise insights." 
            : CustomQueryInput.Text;
            
        await RunAI(prompt + "\n\nContext: ", "🤔 Analyzing…");
    }

    private async void SummarizeButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAI("Provide an extremely concise summary of this text.", "📝 Summarizing…");
    }

    private async Task RunAI(string promptPrefix, string loadingMsg)
    {
        // 1. Disable timer so menu doesn't vanish
        _hideTimer.Stop();

        // 2. Enhance the Selection Rectangle during processing
        SelectionRectangle.Visibility = Visibility.Visible;
        SelectionRectangle.StrokeThickness = 4;
        SelectionRectangle.Fill = SelectionFillBrush;

        // 3. Start processing visual effects
        ScanningAnimation.Begin();
        ProcessingAnimation.Begin();

        // Show the green AI loading indicator and start the pulse animations
        AiLoadingIndicator.Visibility = Visibility.Visible;
        GreenSignalPulseAnimation.Begin();
        AiLoadingCursorAnimation.Begin();

        // 4. Determine if we should treat as image or text (OCR)
        bool treatAsImage = (_capturedBitmap != null || _capturedImageBytes != null) && ShouldTreatAsImage(promptPrefix, _capturedText);
        string response;

        try
        {
            if (treatAsImage)
            {
                // Encode the bitmap to PNG bytes on-demand if we haven't done it yet
                if (_capturedImageBytes == null && _capturedBitmap != null)
                {
                    _capturedImageBytes = await GetImageBytesAsync(_capturedBitmap);
                }

                if (_capturedImageBytes != null)
                {
                    // Formulate prompt specifically for image analysis (strip any hardcoded text context suffix)
                    string userPrompt = promptPrefix;
                    int contextIndex = userPrompt.IndexOf("\n\nContext");
                    if (contextIndex >= 0)
                    {
                        userPrompt = userPrompt.Substring(0, contextIndex);
                    }

                    if (userPrompt.StartsWith("Provide an extremely concise summary of this text."))
                    {
                        userPrompt = "Provide an extremely concise summary of this image.";
                    }

                    response = await _geminiService.AnalyzeTextAsync(userPrompt, _capturedImageBytes, treatAsImage: true);
                }
                else
                {
                    string finalPrompt = promptPrefix + (string.IsNullOrWhiteSpace(_capturedText) ? "[No selection]" : _capturedText);
                    response = await _geminiService.AnalyzeTextAsync(finalPrompt, treatAsImage: false);
                }
            }
            else
            {
                string finalPrompt = promptPrefix + (string.IsNullOrWhiteSpace(_capturedText) ? "[No selection]" : _capturedText);
                response = await _geminiService.AnalyzeTextAsync(finalPrompt, treatAsImage: false);
            }
        }
        catch (Exception ex)
        {
            response = $"❌ **Error calling Gemini API:** {ex.Message}\n\nPlease check your API key settings or your internet connection.";
        }

        // Stop the green AI loading indicator and restore the original cursor stops in code to ensure no state is sticky
        GreenSignalPulseAnimation.Stop();
        AiLoadingCursorAnimation.Stop();
        AiLoadingIndicator.Visibility = Visibility.Collapsed;

        // Reset cursor colors to original blue/turquoise gradient stops
        CursorBrushStop1.Color = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x00, 0xBF, 0xFF);
        CursorBrushStop2.Color = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x00, 0x33, 0xCC);
        SparkleBrushStop2.Color = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x40, 0xE0, 0xD0);

        // 5. Format and Show the response Modal FIRST so CloseMenu knows it is open
        FormatResponseText(response);
        ResponseModal.Visibility = Visibility.Visible;
        App.AppWindow.SetClickThrough(false); // Explicitly ensure click-through is disabled

        // 6. Stop processing effects and hide the magic menu
        ScanningAnimation.Stop();
        ProcessingAnimation.Stop();
        CloseMenu();
    }

    private bool ShouldTreatAsImage(string promptPrefix, string ocrText)
    {
        // Extract the user's custom query (remove context appendix)
        string query = promptPrefix;
        int contextIndex = query.IndexOf("\n\nContext");
        if (contextIndex >= 0)
        {
            query = query.Substring(0, contextIndex);
        }

        // 1. If OCR text is empty, no text found, or failed, treat as image
        if (string.IsNullOrWhiteSpace(ocrText) || 
            ocrText == "[no text found]" || 
            ocrText == "[OCR unavailable]" || 
            ocrText == "[capture failed]")
        {
            return true;
        }

        // 2. Search for visual keywords in user query
        string lowerQuery = query.ToLowerInvariant();
        string[] visualKeywords = new[] { 
            "color", "colour", "describe", "look like", "logo", "diagram", 
            "chart", "graph", "image", "picture", "photo", "drawing", 
            "screenshot", "ui", "layout", "design", "red", "green", "blue", 
            "yellow", "white", "black", "button", "icon", "what is this", "see",
            "active", "selected", "checked", "highlighted", "focus", "enabled", "disabled", "status",
            "font", "size", "styled", "shape", "border", "padding", "margin", "align"
        };
        foreach (var word in visualKeywords)
        {
            if (lowerQuery.Contains(word))
            {
                return true;
            }
        }

        // 3. Word count check: if text is very short (less than 4 words), it is likely a visual element like a button or icon
        int wordCount = ocrText.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 4)
        {
            return true;
        }

        return false;
    }

    private void FormatResponseText(string text)
    {
        ResponseContentPanel.Children.Clear();

        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        bool inCodeBlock = false;
        string codeBlockContent = "";

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // --- Code block toggle ---
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    // End code block — render it
                    var codeBorder = new Border
                    {
                        Background = CodeBlockBgBrush,
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(14, 10, 14, 10),
                        Margin = new Thickness(0, 6, 0, 6)
                    };
                    var codeTb = new TextBlock
                    {
                        Text = codeBlockContent.TrimEnd(),
                        FontFamily = MonoFont,
                        FontSize = 12.5,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        Foreground = CodeTextBrush
                    };
                    codeBorder.Child = codeTb;
                    ResponseContentPanel.Children.Add(codeBorder);
                    codeBlockContent = "";
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeBlockContent += line + "\n";
                continue;
            }

            string trimmed = line.Trim();

            // --- Skip blank lines (add small spacing) ---
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                ResponseContentPanel.Children.Add(new Border { Height = 8 });
                continue;
            }

            // --- Horizontal Rule ---
            if (trimmed == "---" || trimmed == "***" || trimmed == "___")
            {
                var separator = new Border
                {
                    Height = 1,
                    Background = SeparatorBrush,
                    Margin = new Thickness(0, 10, 0, 10)
                };
                ResponseContentPanel.Children.Add(separator);
                continue;
            }

            // --- Heading (## Heading) ---
            if (trimmed.StartsWith("## "))
            {
                var headingTb = new TextBlock
                {
                    FontSize = 17,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 14, 0, 6),
                    Foreground = AccentBrush
                };
                AddFormattedInlines(headingTb, trimmed.Substring(3));
                ResponseContentPanel.Children.Add(headingTb);
                continue;
            }

            if (trimmed.StartsWith("### "))
            {
                var headingTb = new TextBlock
                {
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 4),
                    Foreground = PurpleBrush
                };
                AddFormattedInlines(headingTb, trimmed.Substring(4));
                ResponseContentPanel.Children.Add(headingTb);
                continue;
            }

            // --- Bullet point (•, -, *, or numbered 1. 2. etc.) ---
            bool isBullet = trimmed.StartsWith("• ") || trimmed.StartsWith("- ") || trimmed.StartsWith("* ");
            bool isNumbered = NumberedListRegex.IsMatch(trimmed);

            if (isBullet || isNumbered)
            {
                string bulletChar;
                string content;
                double colWidth = 22;

                if (isBullet)
                {
                    bulletChar = "•";
                    content = trimmed.Substring(2);
                }
                else
                {
                    var match = NumberedListCapture.Match(trimmed);
                    bulletChar = match.Groups[1].Value;
                    content = match.Groups[2].Value;
                    colWidth = 30;
                }

                var bulletGrid = new Grid
                {
                    Margin = new Thickness(8, 3, 0, 3)
                };
                bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(colWidth) });
                bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var bulletTb = new TextBlock
                {
                    Text = bulletChar,
                    FontSize = 13.5,
                    Foreground = AccentBrush,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                Grid.SetColumn(bulletTb, 0);

                var contentTb = new TextBlock
                {
                    FontSize = 13.5,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    LineHeight = 20,
                    Foreground = WhiteBrush
                };
                AddFormattedInlines(contentTb, content);
                Grid.SetColumn(contentTb, 1);

                bulletGrid.Children.Add(bulletTb);
                bulletGrid.Children.Add(contentTb);
                ResponseContentPanel.Children.Add(bulletGrid);
                continue;
            }

            // --- Normal paragraph ---
            var tb = new TextBlock
            {
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                LineHeight = 21,
                Margin = new Thickness(0, 4, 0, 4),
                Foreground = WhiteBrush
            };
            AddFormattedInlines(tb, trimmed);
            ResponseContentPanel.Children.Add(tb);
        }
    }

    /// <summary>
    /// Parses inline Markdown (bold, italic, inline code) and adds formatted Runs to a TextBlock.
    /// </summary>
    private void AddFormattedInlines(TextBlock tb, string text)
    {
        // Process inline formatting: **bold**, *italic*, `code`
        var regex = new System.Text.RegularExpressions.Regex(@"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)");
        int lastIndex = 0;

        foreach (System.Text.RegularExpressions.Match match in regex.Matches(text))
        {
            // Add text before the match
            if (match.Index > lastIndex)
            {
                tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = text.Substring(lastIndex, match.Index - lastIndex)
                });
            }

            if (match.Groups[2].Success) // **bold**
            {
                tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = match.Groups[2].Value,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold
                });
            }
            else if (match.Groups[4].Success) // *italic*
            {
                tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = match.Groups[4].Value,
                    FontStyle = Windows.UI.Text.FontStyle.Italic
                });
            }
            else if (match.Groups[6].Success) // `code`
            {
                var codeRun = new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = " " + match.Groups[6].Value + " ",
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas, monospace"),
                    FontSize = 12
                };
                // We can't set background on Run directly, so we use a subtle visual cue
                codeRun.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0xAA, 0x55));
                tb.Inlines.Add(codeRun);
            }

            lastIndex = match.Index + match.Length;
        }

        // Add remaining text after the last match
        if (lastIndex < text.Length)
        {
            tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = text.Substring(lastIndex)
            });
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var dataPackage = new DataPackage();
        string rawText = "";

        // Walk the ResponseContentPanel and extract text from all children (TextBlocks, Borders, Grids, StackPanels)
        foreach (var child in ResponseContentPanel.Children)
        {
            if (child is TextBlock textBlock)
            {
                string tbText = "";
                foreach (var inline in textBlock.Inlines)
                {
                    if (inline is Microsoft.UI.Xaml.Documents.Run run) tbText += run.Text;
                }
                if (string.IsNullOrEmpty(tbText) && !string.IsNullOrEmpty(textBlock.Text))
                {
                    tbText = textBlock.Text;
                }
                rawText += tbText + Environment.NewLine;
            }
            else if (child is Grid grid)
            {
                string lineText = "";
                // To maintain proper text layout on copy, extract bullet and content text in column order
                var tbs = new List<TextBlock>();
                foreach (var gridChild in grid.Children)
                {
                    if (gridChild is TextBlock tb)
                    {
                        tbs.Add(tb);
                    }
                }
                
                // Sort by Grid.Column to make sure we append bullet first, then content
                tbs.Sort((a, b) => Grid.GetColumn(a).CompareTo(Grid.GetColumn(b)));

                foreach (var tb in tbs)
                {
                    string tbText = "";
                    foreach (var inline in tb.Inlines)
                    {
                        if (inline is Microsoft.UI.Xaml.Documents.Run run) tbText += run.Text;
                    }
                    if (string.IsNullOrEmpty(tbText) && !string.IsNullOrEmpty(tb.Text))
                    {
                        tbText = tb.Text;
                    }

                    if (Grid.GetColumn(tb) == 0)
                    {
                        lineText += tbText + " ";
                    }
                    else
                    {
                        lineText += tbText;
                    }
                }
                rawText += lineText + Environment.NewLine;
            }
            else if (child is StackPanel sp)
            {
                foreach (var spChild in sp.Children)
                {
                    if (spChild is TextBlock tb)
                    {
                        string tbText = "";
                        foreach (var inline in tb.Inlines)
                        {
                            if (inline is Microsoft.UI.Xaml.Documents.Run run) tbText += run.Text;
                        }
                        if (string.IsNullOrEmpty(tbText) && !string.IsNullOrEmpty(tb.Text))
                        {
                            tbText = tb.Text;
                        }
                        rawText += tbText;
                    }
                }
                rawText += Environment.NewLine;
            }
            else if (child is Border border && border.Child is TextBlock codeTb)
            {
                rawText += codeTb.Text + Environment.NewLine;
            }
        }

        dataPackage.SetText(rawText.TrimEnd());
        Clipboard.SetContent(dataPackage);
    }

    private void CloseModalButton_Click(object sender, RoutedEventArgs e)
    {
        ResponseModal.Visibility = Visibility.Collapsed;
        App.AppWindow.SetClickThrough(true);
    }

    private void SpawnSparkle(double startX, double startY)
    {
        long currentTick = Environment.TickCount64;
        if (currentTick - _lastSparkleTickMs < SparkleIntervalMs) return;
        _lastSparkleTickMs = currentTick;

        if (ParticleCanvas.Children.Count >= MaxSparkleParticles) return;

        if (_random.NextDouble() > 0.4) return;

        double size = _random.Next(3, 7);
        var ellipse = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue),
            Opacity = 0.8
        };

        Canvas.SetLeft(ellipse, startX + _random.Next(5, 20));
        Canvas.SetTop(ellipse, startY + _random.Next(5, 20));

        ParticleCanvas.Children.Add(ellipse);

        var storyboard = new Storyboard();
        
        var fallAnimation = new DoubleAnimation
        {
            To = startY + _random.Next(30, 80),
            Duration = TimeSpan.FromMilliseconds(_random.Next(500, 1000)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fallAnimation, ellipse);
        Storyboard.SetTargetProperty(fallAnimation, "(Canvas.Top)");

        var fadeAnimation = new DoubleAnimation
        {
            To = 0,
            Duration = fallAnimation.Duration
        };
        Storyboard.SetTarget(fadeAnimation, ellipse);
        Storyboard.SetTargetProperty(fadeAnimation, "Opacity");

        storyboard.Children.Add(fallAnimation);
        storyboard.Children.Add(fadeAnimation);

        storyboard.Completed += (s, e) =>
        {
            ParticleCanvas.Children.Remove(ellipse);
        };

        storyboard.Begin();
    }

    // --- Settings / API Key UI Logic ---
    private void ShowSettings(bool isFirstRun)
    {
        _hideTimer.Stop(); // Ensure action menu timer is off
        
        // Load current config to populate PasswordBox and CheckBox
        var config = ConfigService.LoadConfig();
        SettingsApiKeyInput.Password = config.GeminiApiKey;
        StartupCheckBox.IsChecked = config.RunAtStartup;
        
        SettingsStatusText.Visibility = Visibility.Collapsed;
        
        if (isFirstRun)
        {
            CancelSettingsButtonText.Text = "Exit App";
        }
        else
        {
            CancelSettingsButtonText.Text = "Cancel";
        }
        
        SettingsModal.Visibility = Visibility.Visible;
        App.AppWindow.SetClickThrough(false); // Enable mouse interaction
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu(); // Hide the action menu
        ShowSettings(isFirstRun: false);
    }

    private void CancelSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var config = ConfigService.LoadConfig();
        
        if (string.IsNullOrWhiteSpace(config.GeminiApiKey))
        {
            // If they cancel/exit on first-run with no key, exit the app
            Application.Current.Exit();
        }
        else
        {
            SettingsModal.Visibility = Visibility.Collapsed;
            App.AppWindow.SetClickThrough(true); // Go back to transparent click-through
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        string newKey = SettingsApiKeyInput.Password.Trim();
        bool runAtStartup = StartupCheckBox.IsChecked == true;
        
        if (string.IsNullOrWhiteSpace(newKey))
        {
            SettingsStatusText.Text = "⚠ Please enter a valid Gemini API key.";
            SettingsStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
            SettingsStatusText.Visibility = Visibility.Visible;
            return;
        }

        // Save using ConfigService
        var config = new ConfigData { GeminiApiKey = newKey, RunAtStartup = runAtStartup };
        ConfigService.SaveConfig(config);
        
        // Update Registry for Windows Startup
        SetStartup(runAtStartup);
        
        // Update GeminiService
        _geminiService.UpdateApiKey(newKey);
        
        // Show success status
        SettingsStatusText.Text = "✨ Settings saved successfully!";
        SettingsStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SpringGreen);
        SettingsStatusText.Visibility = Visibility.Visible;
        
        // Wait 1.2 seconds, then close modal
        await Task.Delay(1200);
        SettingsModal.Visibility = Visibility.Collapsed;
        
        // Restore click-through since settings is closed
        App.AppWindow.SetClickThrough(true);
    }

    private void SetStartup(bool enable)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null)
                {
                    if (enable)
                    {
                        key.SetValue("MagicCursor", $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue("MagicCursor", false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MagicCursorCrashLog.txt");
            File.AppendAllText(logPath, $"SetStartup Error: {ex.Message}\n");
        }
    }
}

public class MagicTextBox : Microsoft.UI.Xaml.Controls.TextBox
{
    public Microsoft.UI.Input.InputCursor? CustomCursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}