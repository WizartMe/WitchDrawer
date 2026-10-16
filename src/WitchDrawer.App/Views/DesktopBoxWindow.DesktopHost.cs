using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WitchDrawer.App.Infrastructure;
using WitchDrawer.Native.Windows;
using static WitchDrawer.Native.Windows.User32Interop;

namespace WitchDrawer.App.Views;

/// <summary>
/// Win32 桌面宿主适配：Z 序沉底、前台所有权、消息钩子。底层行为在 Native 的 DesktopToolWindow。
/// </summary>
public partial class DesktopBoxWindow
{
    internal event Action? DesktopLayerChanged;

    private void NotifyDesktopLayerChanged()
    {
        if (_forceClose || Visibility != Visibility.Visible)
        {
            return;
        }

        try
        {
            DesktopLayerChanged?.Invoke();
        }
        catch (Exception exception)
        {
            ViewModel.Logger.Error(exception, "Failed to queue desktop layer repair.");
        }
    }

    private void SendToBottom()
    {
        if (DesktopWindowLayer.IsEnabled)
        {
            NotifyDesktopLayerChanged();
            return;
        }

        if (!ShouldSendToBottom(_desktopIsForeground))
        {
            return;
        }

        // Shell ownership keeps the box visible through Win+D. Keep it at the
        // bottom of the normal band only while the desktop is not foreground.
        // Moving an owned box during Show Desktop also moves Progman's owner
        // chain and would bring ordinary app windows back above the desktop.
        _nativeWindow?.SendToBottom();
    }

    internal static bool ShouldSendToBottom(bool isDesktopForeground) =>
        !isDesktopForeground;

    public void QueueSendToBottom()
    {
        if (DesktopWindowLayer.IsEnabled)
        {
            NotifyDesktopLayerChanged();
            return;
        }

        SendToBottom();
        Dispatcher.BeginInvoke(new Action(SendToBottom), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 把所有桌面盒压回桌面层。弹窗打开时属主链被 Windows 整体提前，单个盒子沉底不够，
    /// 必须遍历所有盒子窗口统一复位。
    /// </summary>
    internal static void QueueSendToBottomAll()
    {
        if (Application.Current is null)
        {
            return;
        }

        foreach (var window in Application.Current.Windows.OfType<DesktopBoxWindow>())
        {
            window.QueueSendToBottom();
        }
    }

    /// <summary>
    /// 断开弹窗 HWND 的属主关系：之后对弹窗的置顶/沉底不再沿属主链
    /// （盒子→桌面壳→所有盒子）传播。在 Opened 时同步执行，消除窗口期。
    /// </summary>
    private void DetachDrawerPopupOwner()
    {
        if (PresentationSource.FromVisual(DrawerSecondaryPopupRoot) is HwndSource popupSource
            && popupSource.Handle != nint.Zero)
        {
            SetWindowLongPtr(popupSource.Handle, WindowOwnerIndex, 0);
        }
    }

    /// <summary>
    /// 弹窗按"菜单"语义激活：置顶并获取前台。弹窗已断开属主（Opened 时），激活只影响
    /// 弹窗自身——盒子不动。激活后 WPF 原生的 StaysOpen=False 完整生效：
    /// 点击桌面/其他程序/其他盒子都会自动收起，无需额外兜底。
    /// </summary>
    private void BringDrawerPopupToFront()
    {
        if (PresentationSource.FromVisual(DrawerSecondaryPopupRoot) is HwndSource popupSource
            && popupSource.Handle != nint.Zero)
        {
            SetWindowPos(
                popupSource.Handle,
                WindowPositionTopmost,
                0,
                0,
                0,
                0,
                SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosNoActivate);
            SetForegroundWindow(popupSource.Handle);
        }
    }

    public nint NativeHandle => _nativeWindow?.Handle ?? nint.Zero;

    public bool IsNativeWindowAlive => _nativeWindow?.IsAlive == true;

    public bool RefreshDesktopHost()
    {
        if (DesktopWindowLayer.IsEnabled)
        {
            NotifyDesktopLayerChanged();
            return IsNativeWindowAlive;
        }

        if (_nativeWindow?.TryAttachToDesktop() != true)
        {
            return false;
        }

        // 宿主重建/迁移后可能压住盒子（Explorer 重启）：只在这个明确边界补一次条件修复，
        // 不挂 Z 序事件，避免在用户点击过程中搬动窗口。
        _nativeWindow.RepairDesktopHostLayer();
        return true;
    }

    /// <summary>
    /// 桌面宿主 Z 序变化（显示桌面、Explorer 重启、交互结束后的外壳重整）后的层修复。
    /// 不改变前台状态；宿主没有压住盒子时不会产生任何窗口调整。
    /// </summary>
    public bool RepairDesktopHostLayer() => _nativeWindow?.RepairDesktopHostLayer() == true;

    public void SetDesktopForeground(bool isForeground)
    {
        if (DesktopWindowLayer.IsEnabled)
        {
            NotifyDesktopLayerChanged();
            return;
        }

        if (isForeground)
        {
            _nativeWindow?.RefreshDesktopHostForShowDesktop();
        }

        // When Show Desktop is active, leave Explorer's owner-chain Z order
        // untouched. On exit, return the boxes behind ordinary app windows.
        _desktopIsForeground = isForeground;
        SendToBottom();
    }

    private nint WindowMessageHook(
        nint windowHandle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (DesktopDisplayMessages.ChangesLayout(message, wordParameter))
        {
            RequestDisplayLayoutRecovery();
        }

        if (DesktopToolWindow.IsMouseActivationMessage(message))
        {
            // Active window tracking can bypass WS_EX_NOACTIVATE. Always reject
            // mouse activation while preserving input, including on Windows 11.
            if (!DesktopWindowLayer.IsEnabled)
            {
                // Only the legacy path owns the box from Explorer; detach before
                // mouse input so the shell cannot record it as its last active popup.
                _nativeWindow?.SuspendDesktopOwnershipForMouseInput();
            }
            handled = true;
            return DesktopToolWindow.GetMouseActivateWithoutActivationResult();
        }

        if (DesktopWindowLayer.IsEnabled)
        {
            if (DesktopToolWindow.IsMinimizeSystemCommand(message, wordParameter))
            {
                handled = true;
            }
            if (DesktopWindowLayer.IsLayerChangeMessage(message, wordParameter, longParameter))
            {
                NotifyDesktopLayerChanged();
            }
            return nint.Zero;
        }

        if (DesktopToolWindow.IsMouseInteractionCompletionMessage(message))
        {
            QueueRestoreDesktopOwnershipAfterMouseInput();
        }

        if (DesktopToolWindow.IsMinimizeSystemCommand(message, wordParameter))
        {
            // Win+D / Show Desktop normally minimizes top-level windows. A desktop
            // box is desktop furniture, so consume the minimize command.
            handled = true;
        }

        return nint.Zero;
    }

    private void QueueRestoreDesktopOwnershipAfterMouseInput()
    {
        if (DesktopWindowLayer.IsEnabled || _desktopOwnershipRestoreQueued)
        {
            return;
        }

        _desktopOwnershipRestoreQueued = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                _desktopOwnershipRestoreQueued = false;
                if (!_forceClose)
                {
                    _nativeWindow?.RestoreDesktopOwnershipAfterMouseInput(_desktopIsForeground);
                    // 交互结束时刻是外壳把整组盒子插到桌面宿主下面的高发时机，
                    // 此时点击已完成，通知管理器统一抬层，避免盒子被桌面表面盖住。
                    NotifyDesktopLayerChanged();
                    QueueDelayedDesktopLayerRecheck();
                }
            });
    }

    // 交互结束后外壳往往还会再重整一次层，单次通知可能落在它之前；只补一次复检。
    private const int DesktopLayerRecheckDelayMilliseconds = 300;
    private bool _desktopLayerRecheckQueued;

    private void QueueDelayedDesktopLayerRecheck()
    {
        if (DesktopWindowLayer.IsEnabled || _forceClose || _desktopLayerRecheckQueued)
        {
            return;
        }

        _desktopLayerRecheckQueued = true;
        FireAndForget.Run(
            DelayedDesktopLayerRecheckAsync(),
            ViewModel.Logger,
            $"Failed to recheck the desktop layer for box {ViewModel.BoxId:N}.");
    }

    private async Task DelayedDesktopLayerRecheckAsync()
    {
        try
        {
            await Task.Delay(DesktopLayerRecheckDelayMilliseconds).ConfigureAwait(false);
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!_forceClose)
                {
                    NotifyDesktopLayerChanged();
                }
            });
        }
        finally
        {
            _desktopLayerRecheckQueued = false;
        }
    }

    private void OnWindowPreviewMouseUpForDesktopOwnership(
        object sender,
        MouseButtonEventArgs e)
    {
        // WPF fallback for controls that complete input before the HWND hook
        // observes the native button-up message.
        QueueRestoreDesktopOwnershipAfterMouseInput();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (DesktopWindowLayer.IsEnabled)
        {
            if (WindowState == WindowState.Minimized)
            {
                NotifyDesktopLayerChanged();
            }
            return;
        }

        if (_forceClose
            || WindowState != WindowState.Minimized
            || _restoreAfterMinimizeQueued)
        {
            return;
        }

        // Some shell versions minimize via ShowWindow instead of WM_SYSCOMMAND.
        // Restore after the shell's burst of Z-order changes has settled.
        _restoreAfterMinimizeQueued = true;
        FireAndForget.Run(
                RestoreAfterShellMinimizeAsync(),
                ViewModel.Logger,
                $"Failed to restore box window {ViewModel.BoxId:N} after shell minimize.");
    }

    private async Task RestoreAfterShellMinimizeAsync()
    {
        await Task.Delay(120).ConfigureAwait(false);
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _restoreAfterMinimizeQueued = false;
            if (!_forceClose && WindowState == WindowState.Minimized)
            {
                _nativeWindow?.RestoreWithoutActivation();
                // RestoreWithoutActivation no longer changes Z order. Apply exactly
                // one layer operation based on the stabilized desktop state.
                SendToBottom();
            }
        });
    }
}
