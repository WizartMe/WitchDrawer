using WitchDrawer.App.Localization;
using WitchDrawer.Core.Localization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WitchDrawer.App.Infrastructure;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Services;

namespace WitchDrawer.App.ViewModels;

public sealed class UpdateViewModel : LocalizedObservableObject
{
    protected override void OnLanguageChanged()
    {
        if (!IsCheckingUpdate) UpdateStatusText = string.Empty;
    }

    private readonly UpdateService _updateService;
    private readonly IAppLogger _logger;
    private readonly UiOperationState _operations;
    private readonly Func<UpdateCheckResult, Task<bool>>? _confirmUpdateAsync;
    private readonly Func<Task>? _shutdownAfterUpdateAsync;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private string StatusText { set => _operations.StatusText = value; }
    private string _updateStatusText = string.Empty;
    private bool _isCheckingUpdate;
    private bool _updateStarted;
    private CancellationTokenSource? _updateCancellation;

    public UpdateViewModel(UpdateService updateService, IAppLogger logger, UiOperationState operations,
        Func<UpdateCheckResult, Task<bool>>? confirmUpdateAsync = null,
        Func<Task>? shutdownAfterUpdateAsync = null)
    {
        _updateService = updateService;
        _logger = logger;
        _operations = operations;
        _confirmUpdateAsync = confirmUpdateAsync;
        _shutdownAfterUpdateAsync = shutdownAfterUpdateAsync;
        CheckForUpdateCommand = new AsyncRelayCommand(CheckForUpdateAsync, () => !IsCheckingUpdate);
        CancelUpdateCommand = new RelayCommand(() => _updateCancellation?.Cancel(), () => IsCheckingUpdate && !_updateStarted);
    }

    public IAsyncRelayCommand CheckForUpdateCommand { get; }
    public IRelayCommand CancelUpdateCommand { get; }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        private set
        {
            if (SetProperty(ref _isCheckingUpdate, value))
            {
                CheckForUpdateCommand.NotifyCanExecuteChanged();
                CancelUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CurrentVersionText => $"v{GetCurrentVersionText()}";

    private async Task CheckForUpdateAsync()
    {
        // The command owns confirmation, download and shutdown as one operation.
        // Also reject direct ExecuteAsync calls, which can bypass CanExecute.
        if (_updateStarted || !await _updateGate.WaitAsync(0))
        {
            return;
        }

        // Keep cancellation inside the exclusivity gate. A direct duplicate ExecuteAsync
        // must not replace/cancel the token of the operation already in progress.
        using var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        try
        {
            IsCheckingUpdate = true;
            UpdateStatusText = Strings.Get("CheckingForUpdates");

            var currentVersion = GetCurrentVersion();
            var result = await _updateService.CheckForUpdateAsync(currentVersion, cancellationToken);

            if (!result.HasUpdate)
            {
                UpdateStatusText = Strings.Format("UpToDateV", currentVersion.Major, currentVersion.Minor, currentVersion.Build);
                StatusText = UpdateStatusText;
                return;
            }

            var versionText = $"v{result.LatestVersion.Major}.{result.LatestVersion.Minor}.{result.LatestVersion.Build}";
            UpdateStatusText = Strings.Format("VersionIsAvailable", versionText);
            StatusText = UpdateStatusText;
            if (_confirmUpdateAsync is not null && await _confirmUpdateAsync(result).WaitAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteUpdateAsync(result, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateStatusText = Strings.Get("UpdateCanceled");
            StatusText = UpdateStatusText;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Update check failed.");
            UpdateStatusText = Strings.Get("UpdateCheckFailed");
            StatusText = UpdateStatusText;
        }
        finally
        {
            // Once an installer has started, it is waiting for this process to exit.
            // Never allow another installer even if shutdown is delayed or fails.
            if (!_updateStarted) IsCheckingUpdate = false;
            _updateCancellation = null;
            _updateGate.Release();
        }
    }

    private async Task ExecuteUpdateAsync(UpdateCheckResult result, CancellationToken cancellationToken)
    {
        try
        {
            UpdateStatusText = Strings.Get("DownloadingUpdate");

            var progress = new Progress<int>(percent =>
            {
                if (IsCheckingUpdate && !_updateStarted && !cancellationToken.IsCancellationRequested)
                    UpdateStatusText = Strings.Format("DownloadingUpdate2", percent);
            });

            var success = await _updateService.DownloadAndApplyUpdateAsync(
                result.DownloadUrl,
                progress,
                result.ExpectedSha256,
                cancellationToken);

            if (success)
            {
                _updateStarted = true;
                CancelUpdateCommand.NotifyCanExecuteChanged();
                UpdateStatusText = Strings.Get("UpdateDownloadedRestarting");
                StatusText = UpdateStatusText;
                if (_shutdownAfterUpdateAsync is not null)
                    await _shutdownAfterUpdateAsync();
            }
            else
            {
                UpdateStatusText = Strings.Get("UpdateDownloadFailed");
                StatusText = UpdateStatusText;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Update application failed.");
            UpdateStatusText = _updateStarted ? Strings.Get("UpdateReadyQuitWitchDrawerToFinishUpdating") : Strings.Get("UpdateDownloadFailed");
            StatusText = UpdateStatusText;
        }
    }

    // 版本信息来自程序集元数据，运行期不会变化：缓存下来，避免界面绑定与日志导出反复走反射。
    private static readonly Version CurrentAssemblyVersion =
        typeof(UpdateViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);

    private static readonly string? CurrentInformationalVersion =
        typeof(UpdateViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

    /// <summary>
    /// 界面显示用的版本号：发布版形如 1.4.4；本地测试构建在 Directory.Build.props 里带
    /// -test 后缀时一并显示，便于区分正在运行的构建；git 提交号不显示。
    /// </summary>
    internal static string GetCurrentVersionText()
    {
        var version = CurrentAssemblyVersion;
        var numeric = $"{version.Major}.{version.Minor}.{version.Build}";
        if (string.IsNullOrWhiteSpace(CurrentInformationalVersion))
        {
            return numeric;
        }

        // 形如 "1.4.4-test+3f44124"：去掉构建元数据，保留数字版本与后缀。
        var text = CurrentInformationalVersion.Split('+')[0];
        return text.StartsWith(numeric, StringComparison.Ordinal) ? text : numeric;
    }

    internal static Version GetCurrentVersion() => CurrentAssemblyVersion;
}
