using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 输入法候选框位置修正：候选框位置默认由输入法自行决定，在桌面盒子这类工具窗口里常常压在
/// 正在输入的文本上。组字开始时把候选框显式放到光标矩形的下方（“候选框在打字下面”），
/// 组字窗口本身仍由输入法放在光标处，不做改动。
/// </summary>
internal static class ImeCandidatePlacement
{
    private const int WmImeStartComposition = 0x010D;
    private const int CandidateFormStylePoint = 0x0004;   // CFS_CANDIDATEPOS
    private const int FirstCandidateListIndex = 0;
    private const int CandidateGapPixels = 4;

    /// <summary>把候选框位置修正挂到窗口上；句柄还没创建时等 SourceInitialized。</summary>
    internal static void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook(Hook);
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource created)
            {
                created.AddHook(Hook);
            }
        };
    }

    private static nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmImeStartComposition)
        {
            PlaceCandidateBelowCaret(hwnd);
        }

        return nint.Zero;
    }

    private static void PlaceCandidateBelowCaret(nint hwnd)
    {
        if (Keyboard.FocusedElement is not TextBox editor)
        {
            return;
        }

        var point = ResolveCandidatePoint(editor);
        if (point is null)
        {
            return;
        }

        var inputContext = ImmGetContext(hwnd);
        if (inputContext == nint.Zero)
        {
            return;
        }

        try
        {
            var form = new CANDIDATEFORM
            {
                dwIndex = FirstCandidateListIndex,
                dwStyle = CandidateFormStylePoint,
                ptCurrentPos = point.Value,
            };
            ImmSetCandidateWindow(inputContext, ref form);
        }
        finally
        {
            ImmReleaseContext(hwnd, inputContext);
        }
    }

    private static POINT? ResolveCandidatePoint(TextBox editor)
    {
        var caretIndex = editor.CaretIndex;
        var rect = editor.GetRectFromCharacterIndex(caretIndex, trailingEdge: false);
        if (rect.IsEmpty && caretIndex > 0)
        {
            rect = editor.GetRectFromCharacterIndex(caretIndex - 1, trailingEdge: true);
        }

        if (rect.IsEmpty)
        {
            rect = new Rect(0, editor.ActualHeight, 0, 0);
        }

        var screen = editor.PointToScreen(new Point(rect.Left, rect.Bottom));
        return new POINT
        {
            x = (int)Math.Round(screen.X),
            y = (int)Math.Round(screen.Y) + CandidateGapPixels,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CANDIDATEFORM
    {
        public int dwIndex;
        public int dwStyle;
        public POINT ptCurrentPos;
        public RECT rcArea;
    }

    [DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint windowHandle);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(nint windowHandle, nint inputContext);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetCandidateWindow(nint inputContext, ref CANDIDATEFORM candidate);
}
