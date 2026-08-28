using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Diagnostics;
using System.Linq;
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
        private readonly ResourceLoader _resourceLoader = new();

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
                    Debug.WriteLine($"获取应用显示名失败: {ex.Message}");
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
                    Debug.WriteLine($"获取应用版本失败: {ex.Message}");
                    return string.Empty;
                }
            }
        }

        /// <summary>版权前缀（只包含年份）。</summary>
        public string CopyrightPrefix =>
            string.Format(_resourceLoader.GetString("AboutCopyrightPrefix"), DateTime.Now.Year);

        /// <summary>发布者显示名。</summary>
        public static string Developer => Package.Current.PublisherDisplayName;

        // 为 XAML 绑定提供单独的开发者名与链接（支持两个合作者）。
        public string Developer1Name => (Constants.DeveloperNames != null && Constants.DeveloperNames.Length > 0) ? Constants.DeveloperNames[0] : Developer;
        public string Developer2Name => (Constants.DeveloperNames != null && Constants.DeveloperNames.Length > 1) ? Constants.DeveloperNames[1] : string.Empty;
        public string Developer1Link => Constants.DeveloperStoreLinks[0];
        public string Developer2Link => (Constants.DeveloperStoreLinks != null && Constants.DeveloperStoreLinks.Length > 1 && !string.IsNullOrEmpty(Constants.DeveloperStoreLinks[1])) ? Constants.DeveloperStoreLinks[1] : string.Empty;

        /// <summary>开源仓库链接。</summary>
        public string OpenSourceUrl { get; set; } = Constants.OpenSourceLink;

        /// <summary>合并显示名，供版权与其它位置使用。</summary>
        public string DeveloperStoreDisplayName => string.Join(" / ", new[] { Developer1Name, Developer2Name }.Where(s => !string.IsNullOrEmpty(s)));

        // =====================
        // UI 请求事件（视图模型不依赖具体控件）
        // =====================

        /// <summary>请求页面打开外部链接。</summary>
        public event EventHandler<string>? OpenLinkRequested;

        // =====================
        // 构造函数与初始化
        // =====================

        public SettingsViewModel()
        {
            _isInitializing = true;
            LoadSettings();
            _isInitializing = false;
        }

        /// <summary>加载持久化的设置项，并映射为页面控件的初始值。</summary>
        private void LoadSettings()
        {
            ThemeIndex = SettingsLoader.GetAppTheme() switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            MaterialIndex = SettingsLoader.GetAppMaterial() switch
            {
                "MicaAlt" => 1,
                "Acrylic" => 2,
                _ => 0
            };

            IsAutoDeveloperModeEnabled = SettingsLoader.GetAutoEnableDeveloperMode();
        }

        /// <summary>
        /// 页面初始化：记录窗口句柄，并在后台刷新捐赠者版购买状态，不阻塞页面展示。
        /// </summary>
        public async Task InitializeAsync(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
            await RefreshDonationStateAsync();
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
            var theme = value switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            SettingsLoader.SetAppTheme(themeSetting);
            AppThemeManager.CurrentTheme = theme;
            if (App.MainWindow?.Content is FrameworkElement root)
            {
                root.RequestedTheme = theme;
            }
            AppThemeManager.UpdateTitleBarColors();
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
            SettingsLoader.SetAppMaterial(materialSetting);
            AppThemeManager.CurrentMaterial = materialSetting switch
            {
                "MicaAlt" => BackgroundMaterial.MicaAlt,
                "Acrylic" => BackgroundMaterial.Acrylic,
                _ => BackgroundMaterial.Mica
            };
            AppThemeManager.ApplyMaterial();
        }

        partial void OnIsAutoDeveloperModeEnabledChanged(bool value)
        {
            if (_isInitializing)
            {
                return;
            }

            SettingsLoader.SetAutoEnableDeveloperMode(value);
        }

        // =====================
        // 业务逻辑
        // =====================

        /// <summary>
        /// 在后台查询 Store 许可证，判断捐赠者版是否已购买。
        /// 设置最长等待 5 秒，避免许可证服务无响应时拖慢页面。
        /// </summary>
        private async Task RefreshDonationStateAsync()
        {
            var checkTask = DonationService.IsDonorEditionPurchasedAsync(_windowHandle);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var completedTask = await Task.WhenAny(checkTask, timeoutTask);

            bool purchased =
                completedTask == checkTask &&
                checkTask.Status == TaskStatus.RanToCompletion &&
                checkTask.Result;

            IsDonorEditionPurchased = purchased;
        }

        /// <summary>根据解锁结果拼装带诊断信息的失败提示。</summary>
        private static string BuildRegionPolicyFailureMessage(string baseMessage)
        {
            string? diagnostics = RegionPolicyService.LastDiagnostics;
            return string.IsNullOrWhiteSpace(diagnostics)
                ? baseMessage
                : baseMessage + Environment.NewLine + Environment.NewLine + diagnostics;
        }

        // =====================
        // 命令（绑定到页面按钮）
        // =====================

        /// <summary>打开开源仓库链接。</summary>
        [RelayCommand]
        private void OpenSourceLink()
        {
            OpenLinkRequested?.Invoke(this, OpenSourceUrl);
        }

        /// <summary>打开开发者商店链接。</summary>
        [RelayCommand]
        private void OpenDeveloperLink(string? url)
        {
            if (!string.IsNullOrEmpty(url))
            {
                OpenLinkRequested?.Invoke(this, url);
            }
        }

        /// <summary>
        /// 购买捐赠者版：先确认，再通过 Store 完成购买，成功后更新感谢文案状态。
        /// </summary>
        [RelayCommand]
        private async Task DonateAsync()
        {
            bool confirmed = await DialogService.ShowConfirmAsync(
                _resourceLoader.GetString("DonationConfirmTitle"),
                _resourceLoader.GetString("DonationConfirmMessage"),
                _resourceLoader.GetString("DonationConfirmPrimaryButtonText"),
                _resourceLoader.GetString("DonationConfirmCloseButtonText"));

            if (!confirmed)
            {
                return;
            }

            DonationPurchaseResult result = await DonationService.PurchaseDonorEditionAsync(_windowHandle);

            if (result is DonationPurchaseResult.Purchased or DonationPurchaseResult.AlreadyPurchased)
            {
                IsDonorEditionPurchased = true;
            }
            else if (result == DonationPurchaseResult.Failed)
            {
                await DialogService.ShowMessageAsync(
                    _resourceLoader.GetString("DonationErrorTitle"),
                    _resourceLoader.GetString("DonationErrorMessage"),
                    _resourceLoader.GetString("DialogOK"));
            }
        }

        /// <summary>
        /// 解除第三方小组件源的地区限制：先确认，再执行提权脚本，并按结果展示反馈。
        /// </summary>
        [RelayCommand]
        private async Task UnlockRegionPolicyAsync()
        {
            bool confirmed = await DialogService.ShowConfirmAsync(
                _resourceLoader.GetString("RegionPolicyWarningTitle"),
                _resourceLoader.GetString("RegionPolicyWarningMessage"),
                _resourceLoader.GetString("RegionPolicyWarningPrimaryButtonText"),
                _resourceLoader.GetString("DonationConfirmCloseButtonText"));

            if (!confirmed)
            {
                return;
            }

            RegionPolicyOperationResult result = await RegionPolicyService.EnableThirdPartyWidgetFeedAsync();

            switch (result)
            {
                case RegionPolicyOperationResult.Success:
                    await DialogService.ShowMessageAsync(
                        _resourceLoader.GetString("RegionPolicySuccessTitle"),
                        _resourceLoader.GetString("RegionPolicySuccessMessage"),
                        _resourceLoader.GetString("DialogOK"));
                    break;

                case RegionPolicyOperationResult.Cancelled:
                    await DialogService.ShowMessageAsync(
                        _resourceLoader.GetString("RegionPolicyWarningTitle"),
                        _resourceLoader.GetString("RegionPolicyCancelledMessage"),
                        _resourceLoader.GetString("DialogOK"));
                    break;

                case RegionPolicyOperationResult.PolicyNotFound:
                    await DialogService.ShowMessageAsync(
                        _resourceLoader.GetString("RegionPolicyFailureTitle"),
                        BuildRegionPolicyFailureMessage(_resourceLoader.GetString("RegionPolicyPolicyNotFoundMessage")),
                        _resourceLoader.GetString("DialogOK"));
                    break;

                default:
                    await DialogService.ShowMessageAsync(
                        _resourceLoader.GetString("RegionPolicyFailureTitle"),
                        BuildRegionPolicyFailureMessage(_resourceLoader.GetString("RegionPolicyFailureMessage")),
                        _resourceLoader.GetString("DialogOK"));
                    break;
            }
        }
    }
}
