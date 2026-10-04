using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Documents;
using FeedCustomizer.Core.Donations;
using FeedCustomizer.Core.Feedback;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Interface;
using FeedCustomizer.Core.Region;
using FeedCustomizer.Core.Settings;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.WidgetData;
using AppConstants = FeedCustomizer.Core.Constants.Constants;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace FeedCustomizer.ViewModels
{
    /// <summary>
    /// 设置页的视图模型：集中管理外观、高级选项、捐赠与“关于”信息等业务逻辑。
    /// 页面后置代码只保留焦点控制、打开外部链接等与 UI 强相关的交互。
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        private static readonly IAppLog Log = AppLog.For<SettingsViewModel>();
        private readonly ResourceLoader _resourceLoader = new();
        private readonly FeedbackService _feedbackService;
        private readonly ApplicationDocumentService _documents;
        private readonly IAppSettings _settings;
        private readonly DonationService _donations;
        private readonly RegionService _region;
        private readonly ISettingsInteraction _interaction;
        private readonly string _documentLanguageTag;

        // 查询可能早于购买发起；已确认的购买不能被较晚返回的旧许可证快照覆盖。
        private bool _donationPurchaseConfirmed;

        /// <summary>Store 购买与许可证查询所需的窗口句柄，由页面初始化时传入。</summary>
        private IntPtr _windowHandle = IntPtr.Zero;

        /// <summary>构造函数加载初始值时抑制属性变化副作用，避免误写设置或重复应用主题。</summary>
        private bool _isInitializing;

        // =====================
        // 设置项（与页面控件双向绑定）
        // =====================

        /// <summary>主题单选索引：0=跟随系统，1=浅色，2=深色。</summary>
        [ObservableProperty]
        public partial int ThemeIndex { get; set; }

        /// <summary>背景材质单选索引：0=Mica，1=Mica Alt，2=Acrylic。</summary>
        [ObservableProperty]
        public partial int MaterialIndex { get; set; }

        /// <summary>是否自动启用开发者模式。</summary>
        [ObservableProperty]
        public partial bool IsAutoDeveloperModeEnabled { get; set; }

        /// <summary>清理小组件数据期间禁用重复命令，避免连续确认导致用户误以为会并发执行。</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ClearWidgetDataCommand))]
        public partial bool IsClearingWidgetData { get; set; }

        /// <summary>日志导出或邮件启动期间禁用两个入口，避免用户重复创建归档和邮件窗口。</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExportLogsCommand))]
        [NotifyCanExecuteChangedFor(nameof(SendFeedbackCommand))]
        public partial bool IsFeedbackOperationRunning { get; set; }

        /// <summary>是否已购买捐赠者版（控制捐赠按钮与感谢文案的显隐）。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DonateButtonVisibility))]
        [NotifyPropertyChangedFor(nameof(DonationThanksVisibility))]
        public partial bool IsDonorEditionPurchased { get; set; }

        /// <summary>捐赠按钮的可见性：已购买时隐藏。</summary>
        public Visibility DonateButtonVisibility =>
            IsDonorEditionPurchased ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>捐赠感谢文案的可见性：已购买时显示。</summary>
        public Visibility DonationThanksVisibility =>
            IsDonorEditionPurchased ? Visibility.Visible : Visibility.Collapsed;

        // =====================
        // “关于”信息
        // =====================

        /// <summary>应用图标，绑定到“关于”展开器。</summary>
        public BitmapImage AppLogoImage
        {
            get
            {
                try
                {
                    return new(Package.Current.Logo);
                }
                catch (ArgumentException)
                {
                    // 包清单中的 Logo 不是有效的绝对 URI 时，回退到内置图标资源，避免绑定崩溃。
                    return new(new Uri("ms-appx:///Assets/StoreLogo.scale-200.png"));
                }
            }
        }

        /// <summary>应用显示名称。</summary>
        public string AppDisplayName
        {
            get
            {
                try
                {
                    return Package.Current.DisplayName;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "获取应用显示名称失败");
                    return string.Empty;
                }
            }
        }

        /// <summary>应用版本号。</summary>
        public string AppVersion
        {
            get
            {
                try
                {
                    var version = Package.Current.Id.Version;
                    return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "获取应用版本失败");
                    return string.Empty;
                }
            }
        }

        /// <summary>版权前缀（只包含年份）。</summary>
        public string CopyrightPrefix =>
            string.Format(_resourceLoader.GetString("AboutCopyrightPrefix"), DateTime.Now.Year);

        /// <summary>版权开发者姓名。</summary>
        public string DeveloperName => Constants.DeveloperName;

        /// <summary>版权开发者链接。</summary>
        public string DeveloperLink => Constants.DeveloperLink;

        /// <summary>贡献者列表，绑定到“关于”展开器。</summary>
        public IReadOnlyList<Contributor> Contributors => Constants.Contributors;

        /// <summary>Gitee 开源仓库链接。</summary>
        public string GiteeUrl { get; set; } = Constants.GiteeUrl;

        /// <summary>GitHub 开源仓库链接。</summary>
        public string GitHubUrl { get; set; } = Constants.GitHubLink;

        // =====================
        // UI 请求事件（视图模型不依赖具体控件）
        // =====================

        /// <summary>请求页面打开外部链接。</summary>
        public event EventHandler<string>? OpenLinkRequested;

        /// <summary>请求 UI 打开已完成同步和路径验证的应用文档。</summary>
        public event EventHandler<ApplicationDocumentOpenRequestedEventArgs>? DocumentOpenRequested;

        /// <summary>请求 UI 展示应用文档同步或准备失败信息。</summary>
        public event EventHandler<ApplicationDocumentResult>? DocumentPreparationFailed;

        /// <summary>请求显示或隐藏加载遮罩。</summary>
        public event EventHandler<bool>? LoadingOverlayRequested;

        /// <summary>请求页面应用已经保存的主题，ViewModel 不直接访问窗口或控件。</summary>
        public event EventHandler<string>? ThemeChangeRequested;

        /// <summary>请求页面应用已经保存的背景材质，窗口适配由页面完成。</summary>
        public event EventHandler<string>? MaterialChangeRequested;

        /// <summary>请求页面展示不带反馈按钮的普通结果，防止反馈功能自身失败后递归发送反馈。</summary>
        public event EventHandler<SettingsMessageRequestedEventArgs>? MessageRequested;

        /// <summary>请求页面展示可发送反馈的错误弹窗，视图模型不直接依赖具体 ContentDialog。</summary>
        internal event EventHandler<SettingsErrorRequestedEventArgs>? ErrorRequested;

        // =====================
        // 构造函数与初始化
        // =====================

        internal SettingsViewModel(
            FeedbackService feedbackService,
            ApplicationDocumentService documents,
            IAppSettings settings,
            DonationService donations,
            RegionService region,
            ISettingsInteraction interaction,
            string documentLanguageTag)
        {
            _feedbackService = feedbackService;
            _documents = documents;
            _settings = settings;
            _donations = donations;
            _region = region;
            _interaction = interaction;
            _documentLanguageTag = documentLanguageTag;
            _isInitializing = true;
            LoadSettings();
            _isInitializing = false;
        }

        /// <summary>加载持久化的设置项，并映射为页面控件的初始值。</summary>
        private void LoadSettings()
        {
            ThemeIndex = _settings.GetAppTheme() switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            MaterialIndex = _settings.GetAppMaterial() switch
            {
                "MicaAlt" => 1,
                "Acrylic" => 2,
                _ => 0
            };

            IsAutoDeveloperModeEnabled = _settings.GetAutoEnableDeveloperMode();
        }

        /// <summary>
        /// 页面初始化：记录窗口句柄，并在后台刷新捐赠者版购买状态，不阻塞页面展示。
        /// </summary>
        public async Task InitializeAsync(IntPtr windowHandle, CancellationToken cancellationToken = default)
        {
            _windowHandle = windowHandle;
            await RefreshDonationStateAsync(cancellationToken);
        }

        // =====================
        // 属性变化处理（用户修改设置后立即持久化并生效）
        // =====================

        partial void OnThemeIndexChanged(int value)
        {
            if (_isInitializing)
            {
                return;
            }

            string themeSetting = value switch
            {
                1 => "Light",
                2 => "Dark",
                _ => "System"
            };
            _settings.SetAppTheme(themeSetting);
            ThemeChangeRequested?.Invoke(this, themeSetting);
        }

        partial void OnMaterialIndexChanged(int value)
        {
            if (_isInitializing)
            {
                return;
            }

            string materialSetting = value switch
            {
                1 => "MicaAlt",
                2 => "Acrylic",
                _ => "Mica"
            };
            _settings.SetAppMaterial(materialSetting);
            MaterialChangeRequested?.Invoke(this, materialSetting);
        }

        partial void OnIsAutoDeveloperModeEnabledChanged(bool value)
        {
            if (_isInitializing)
            {
                return;
            }

            _settings.SetAutoEnableDeveloperMode(value);
        }

        // =====================
        // 业务逻辑
        // =====================

        /// <summary>
        /// 在后台查询 Store 许可证，判断捐赠者版是否已购买。
        /// 查询期限由应用服务控制；失败时保留已确认状态，不把无法查询解释为未购买。
        /// </summary>
        private async Task RefreshDonationStateAsync(CancellationToken cancellationToken)
        {
            DonationLicenseResult result = await _donations.GetLicenseAsync(_windowHandle, cancellationToken);
            if (result.Status == DonationLicenseStatus.Purchased)
            {
                IsDonorEditionPurchased = true;
            }
            else if (result.Status == DonationLicenseStatus.NotPurchased && !_donationPurchaseConfirmed)
            {
                IsDonorEditionPurchased = false;
            }
            else if (result.Status is DonationLicenseStatus.Failed or DonationLicenseStatus.TimedOut)
            {
                Log.Warning("捐赠者版状态暂不可用，状态={LicenseStatus}，诊断={Diagnostic}", result.Status, result.Diagnostic);
            }
        }

        /// <summary>根据解锁结果拼装带诊断信息的失败提示。</summary>
        private static string BuildRegionPolicyFailureMessage(string baseMessage, string diagnostics)
        {
            return string.IsNullOrWhiteSpace(diagnostics)
                ? baseMessage
                : baseMessage + Environment.NewLine + Environment.NewLine + diagnostics;
        }

        // =====================
        // 命令（绑定到页面按钮）
        // =====================

        /// <summary>打开 Gitee 开源仓库链接。</summary>
        [RelayCommand]
        private void OpenGiteeLink()
        {
            OpenLinkRequested?.Invoke(this, GiteeUrl);
        }

        /// <summary>
        /// 准备并打开随包开源许可声明。文档服务负责版本同步和缺失自愈，
        /// ViewModel 只将成功结果作为 UI 请求转发，避免直接依赖文件或窗口 API。
        /// </summary>
        [RelayCommand]
        private async Task OpenSourceLicensesAsync()
        {
            await OpenApplicationDocumentAsync(ApplicationDocumentKind.OpenSourceLicenses);
        }

        /// <summary>准备并打开随包隐私声明，使用与其他应用文档相同的同步和失败语义。</summary>
        [RelayCommand]
        private async Task OpenPrivacyStatementAsync()
        {
            await OpenApplicationDocumentAsync(ApplicationDocumentKind.Privacy);
        }

        /// <summary>
        /// 统一转换应用文档准备结果为 UI 请求，避免每个文档入口重复同步、成功和失败分支。
        /// </summary>
        private async Task OpenApplicationDocumentAsync(ApplicationDocumentKind kind)
        {
            ApplicationDocumentResult result = await _documents.PrepareAsync(kind, _documentLanguageTag);
            if (result.Succeeded)
            {
                DocumentOpenRequested?.Invoke(
                    this,
                    new ApplicationDocumentOpenRequestedEventArgs(
                        kind,
                        result.FilePath));
                return;
            }

            DocumentPreparationFailed?.Invoke(this, result);
        }

        /// <summary>将全部保留日志打包到下载目录，并展示与资源管理器一致的完整路径。</summary>
        [RelayCommand(CanExecute = nameof(CanRunFeedbackOperation))]
        private async Task ExportLogsAsync()
        {
            IsFeedbackOperationRunning = true;
            try
            {
                LogArchiveResult result = await _feedbackService.ExportLogsAsync();
                if (result.Succeeded)
                {
                    RequestMessage(
                        _resourceLoader.GetString("LogExportSuccessTitle"),
                        string.Format(
                            _resourceLoader.GetString("LogExportSuccessMessageFormat"),
                            result.FullPath));
                    return;
                }

                RequestMessage(
                    _resourceLoader.GetString("LogExportFailureTitle"),
                    _resourceLoader.GetString("LogExportFailureMessage"));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设置页导出日志失败");
                RequestMessage(
                    _resourceLoader.GetString("LogExportFailureTitle"),
                    _resourceLoader.GetString("LogExportFailureMessage"));
            }
            finally
            {
                IsFeedbackOperationRunning = false;
            }
        }

        /// <summary>
        /// 导出日志后启动邮件流程。命令只等待归档完成，不等待同步式 MAPI 返回，
        /// 否则某些客户端会让 AsyncRelayCommand 长期处于执行中并持续禁用按钮。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanRunFeedbackOperation))]
        private async Task SendFeedbackAsync()
        {
            IsFeedbackOperationRunning = true;
            try
            {
                FeedbackPreparationResult preparation = await _feedbackService.PrepareAsync(
                    FeedbackSource.Settings);
                if (!preparation.Succeeded || preparation.PreparedFeedback is null)
                {
                    RequestMessage(
                        _resourceLoader.GetString("LogExportFailureTitle"),
                        _resourceLoader.GetString("LogExportFailureMessage"));
                    return;
                }

                // 后台观察任务内部会捕获全部异常并在必要时请求 UI 提示，不能留下未观察任务异常。
                _ = ObserveFeedbackLaunchAsync(preparation.PreparedFeedback, _windowHandle);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设置页打开反馈邮件失败");
                RequestMessage(
                    _resourceLoader.GetString("FeedbackMailClientFailureTitle"),
                    _resourceLoader.GetString("FeedbackUnexpectedFailureMessage"));
            }
            finally
            {
                IsFeedbackOperationRunning = false;
            }
        }

        /// <summary>
        /// 观察邮件通道最终结果，但不延长设置命令的执行时间。这样邮件客户端即使长期保持
        /// MAPISendMailW 调用，两个设置按钮也能在日志准备完成后恢复。
        /// </summary>
        private async Task ObserveFeedbackLaunchAsync(
            PreparedFeedback preparedFeedback,
            IntPtr ownerWindowHandle)
        {
            try
            {
                FeedbackOperationResult result = await _feedbackService.LaunchPreparedAsync(
                    preparedFeedback,
                    ownerWindowHandle);
                if (result.MailClientLaunched)
                {
                    return;
                }

                RequestMessage(
                    _resourceLoader.GetString("FeedbackMailClientFailureTitle"),
                    string.Format(
                        _resourceLoader.GetString("FeedbackMailClientFailureMessageFormat"),
                        result.Archive.FullPath,
                        AppConstants.FeedbackEmailAddress));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设置页后台观察反馈邮件失败");
                RequestMessage(
                    _resourceLoader.GetString("FeedbackMailClientFailureTitle"),
                    _resourceLoader.GetString("FeedbackUnexpectedFailureMessage"));
            }
        }

        private bool CanRunFeedbackOperation() => !IsFeedbackOperationRunning;

        private void RequestMessage(string title, string message)
        {
            MessageRequested?.Invoke(
                this,
                new SettingsMessageRequestedEventArgs(
                    title,
                    message,
                    _resourceLoader.GetString("DialogOK")));
        }

        /// <summary>打开 GitHub 开源仓库链接。</summary>
        [RelayCommand]
        private void OpenGitHubLink()
        {
            OpenLinkRequested?.Invoke(this, GitHubUrl);
        }

        /// <summary>打开开发者个人主页链接。</summary>
        [RelayCommand]
        private void OpenDeveloperLink(string? url)
        {
            if (!string.IsNullOrEmpty(url))
            {
                OpenLinkRequested?.Invoke(this, url);
            }
        }

        /// <summary>打开贡献者个人主页链接。</summary>
        public void OpenContributorLink(Contributor contributor)
        {
            if (!string.IsNullOrEmpty(contributor.Link))
            {
                OpenLinkRequested?.Invoke(this, contributor.Link);
            }
        }

        /// <summary>
        /// 购买捐赠者版：先确认，再通过 Store 完成购买，成功后更新感谢文案状态。
        /// </summary>
        [RelayCommand]
        private async Task DonateAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!await _interaction.ConfirmDonationAsync(cancellationToken))
                {
                    return;
                }

                DonationPurchaseResult result = await _donations.PurchaseAsync(_windowHandle, cancellationToken);
                if (result.Status is DonationPurchaseStatus.Purchased or DonationPurchaseStatus.AlreadyPurchased)
                {
                    _donationPurchaseConfirmed = true;
                    IsDonorEditionPurchased = true;
                }
                else if (result.Status == DonationPurchaseStatus.Failed)
                {
                    Log.Warning("Store 购买失败，诊断={Diagnostic}", result.Diagnostic);
                    RequestError(
                        _resourceLoader.GetString("DonationErrorTitle"),
                        _resourceLoader.GetString("DonationErrorMessage"),
                        FeedbackSource.Donation);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Log.Debug("设置页结束，捐赠流程已取消");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设置页捐赠流程失败");
                RequestError(
                    _resourceLoader.GetString("DonationErrorTitle"),
                    _resourceLoader.GetString("DonationErrorMessage"),
                    FeedbackSource.Donation);
            }
        }

        /// <summary>
        /// 解除第三方小组件源的地区限制：先确认，再执行提权脚本，并按结果展示反馈。
        /// </summary>
        [RelayCommand]
        private async Task UnlockRegionPolicyAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!await _interaction.ConfirmRegionUnlockAsync(cancellationToken))
                {
                    return;
                }

                LoadingOverlayRequested?.Invoke(this, true);
                RegionPolicyOperationResult result;
                try
                {
                    result = await _region.EnablePolicyAsync(cancellationToken);
                }
                finally
                {
                    LoadingOverlayRequested?.Invoke(this, false);
                }

                switch (result.Status)
                {
                    case RegionPolicyOperationStatus.Success:
                        RequestMessage(
                            _resourceLoader.GetString("RegionPolicySuccessTitle"),
                            _resourceLoader.GetString("RegionPolicySuccessMessage"));
                        break;

                    case RegionPolicyOperationStatus.Cancelled:
                        RequestMessage(
                            _resourceLoader.GetString("RegionPolicyWarningTitle"),
                            _resourceLoader.GetString("RegionPolicyCancelledMessage"));
                        break;

                    case RegionPolicyOperationStatus.PolicyNotFound:
                        RequestError(
                            _resourceLoader.GetString("RegionPolicyFailureTitle"),
                            BuildRegionPolicyFailureMessage(
                                _resourceLoader.GetString("RegionPolicyPolicyNotFoundMessage"), result.Diagnostic),
                            FeedbackSource.RegionPolicy);
                        break;

                    default:
                        RequestError(
                            _resourceLoader.GetString("RegionPolicyFailureTitle"),
                            BuildRegionPolicyFailureMessage(
                                _resourceLoader.GetString("RegionPolicyFailureMessage"), result.Diagnostic),
                            FeedbackSource.RegionPolicy);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Log.Debug("设置页结束，地区解锁流程已取消");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设置页地区解锁流程失败");
                RequestError(
                    _resourceLoader.GetString("RegionPolicyFailureTitle"),
                    _resourceLoader.GetString("RegionPolicyFailureMessage"),
                    FeedbackSource.RegionPolicy);
            }
        }

        /// <summary>清除小组件面板 WebView 的本地 Profile 前显示不可逆范围警告。</summary>
        [RelayCommand(CanExecute = nameof(CanClearWidgetData))]
        private async Task ClearWidgetDataAsync()
        {
            IsClearingWidgetData = true;
            try
            {
                bool confirmed = await DialogService.ShowConfirmAsync(
                    _resourceLoader.GetString("WidgetDataWarningTitle"),
                    _resourceLoader.GetString("WidgetDataWarningMessage"),
                    _resourceLoader.GetString("WidgetDataWarningPrimaryButtonText"),
                    _resourceLoader.GetString("WidgetDataWarningCloseButtonText"));

                if (!confirmed)
                {
                    return;
                }

                LoadingOverlayRequested?.Invoke(this, true);
                WidgetDataClearResult result;
                try
                {
                    result = await WidgetDataReset.Current.ClearAsync();
                }
                finally
                {
                    LoadingOverlayRequested?.Invoke(this, false);
                }

                if (result.Status == WidgetDataClearStatus.Succeeded)
                {
                    await DialogService.ShowMessageAsync(
                        _resourceLoader.GetString("WidgetDataSuccessTitle"),
                        _resourceLoader.GetString("WidgetDataSuccessMessage"),
                        _resourceLoader.GetString("DialogOK"));
                    return;
                }

                RequestError(
                    _resourceLoader.GetString("WidgetDataFailureTitle"),
                    _resourceLoader.GetString(result.Status == WidgetDataClearStatus.Locked
                        ? "WidgetDataLockedMessage"
                        : "WidgetDataFailureMessage"),
                    FeedbackSource.WidgetData);
            }
            finally
            {
                IsClearingWidgetData = false;
            }
        }

        /// <summary>同一设置页只允许一个确认或清理流程运行，跨页面实例由协调器继续串行保护。</summary>
        private bool CanClearWidgetData() => !IsClearingWidgetData;

        private void RequestError(string title, string details, FeedbackSource source)
        {
            ErrorRequested?.Invoke(this, new SettingsErrorRequestedEventArgs(title, details, source));
        }
    }

    /// <summary>设置视图模型向页面请求展示的普通消息。</summary>
    public sealed record SettingsMessageRequestedEventArgs(
        string Title,
        string Message,
        string CloseButtonText);

    /// <summary>设置视图模型向页面请求展示的可反馈错误。</summary>
    internal sealed record SettingsErrorRequestedEventArgs(
        string Title,
        string Details,
        FeedbackSource Source);
}
