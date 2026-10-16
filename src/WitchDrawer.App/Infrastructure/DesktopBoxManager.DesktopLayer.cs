using System.Windows;
using System.Windows.Threading;
using WitchDrawer.Native.Windows;

namespace WitchDrawer.App.Infrastructure;

public sealed partial class DesktopBoxManager
{
    private DesktopLayerMonitor? _desktopLayerMonitor;
    private DesktopLayerUpdateQueue? _desktopLayerUpdates;
    private nint _desktopLayerHost;
    private bool _maintainingDesktopLayer;

    private void InitializeDesktopLayer()
    {
        _desktopLayerUpdates = new DesktopLayerUpdateQueue(
            Dispatcher.CurrentDispatcher, MaintainDesktopLayer,
            exception => _logger.Error(exception, "Failed to process desktop layer event."));
        _desktopLayerMonitor = new DesktopLayerMonitor();
        _desktopLayerMonitor.LayerChanged += QueueDesktopLayerUpdate;
    }

    private void QueueDesktopLayerUpdate() => _desktopLayerUpdates?.Request();

    internal static T[] SnapshotDesktopLayerWindows<T>(
        IEnumerable<T> windows, Func<T, Visibility> visibility, Func<T, bool> isAlive) =>
        windows.Where(window => visibility(window) == Visibility.Visible && isAlive(window)).ToArray();

    private void MaintainDesktopLayer()
    {
        // Both the queue and the explicit startup call run on the UI thread.
        // Do not inspect WPF state or the dictionary in a native callback.
        if (!DesktopWindowLayer.IsEnabled || _closing || _maintainingDesktopLayer)
        {
            return;
        }

        var handles = SnapshotDesktopLayerWindows(
                _windows.Values, window => window.Visibility, window => window.IsNativeWindowAlive)
            .Select(window => window.NativeHandle).ToArray();
        if (handles.Length == 0)
        {
            return;
        }

        _maintainingDesktopLayer = true;
        try
        {
            var previousHost = _desktopLayerHost;
            _desktopLayerHost = DesktopWindowLayer.Maintain(_desktopLayerHost, handles);
            if (_desktopLayerHost != 0 && _desktopLayerHost != previousHost)
            {
                _logger.Info($"Event-driven Windows 11 desktop layer: host=0x{_desktopLayerHost:X}.");
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to maintain desktop layer.");
        }
        finally
        {
            _maintainingDesktopLayer = false;
        }
    }

    /// <summary>
    /// 壳属主路径只建立合并队列，不安装 WinEvent 钩子：事件驱动抬层会在用户点击过程中搬动
    /// 窗口（实测每秒一次、每次四个盒子），打断点击并造成闪烁。队列只在明确时机触发。
    /// </summary>
    private void InitializeDesktopLayerQueue(Action maintain)
    {
        _desktopLayerUpdates = new DesktopLayerUpdateQueue(
            Dispatcher.CurrentDispatcher, maintain,
            exception => _logger.Error(exception, "Failed to process desktop layer event."));
    }

    /// <summary>
    /// 壳属主路径的层修复：外壳把盒子（连同同属主的兄弟盒子）插到桌面宿主下面时，
    /// 把可见盒子放回宿主上方。由盒子在交互结束等明确时机触发，宿主没压住盒子时不调整。
    /// </summary>
    private void MaintainOwnedDesktopLayer()
    {
        if (_closing)
        {
            return;
        }

        var raised = 0;
        foreach (var window in SnapshotDesktopLayerWindows(
                     _windows.Values, window => window.Visibility, window => window.IsNativeWindowAlive))
        {
            if (window.RepairDesktopHostLayer())
            {
                raised++;
            }
        }

        if (raised == 0)
        {
            return;
        }

        _logger.Info($"Desktop layer repair: raised {raised} box(es) above the desktop host.");
    }

    private void DisposeDesktopLayer()
    {
        if (_desktopLayerMonitor is not null)
        {
            _desktopLayerMonitor.LayerChanged -= QueueDesktopLayerUpdate;
            _desktopLayerMonitor.Dispose();
        }
        _desktopLayerUpdates?.Dispose();
    }
}
