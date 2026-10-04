using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Documents;
using FeedCustomizer.Core.Feedback;
using FeedCustomizer.Core.Interface;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.Presentation.Dialogs;
using FeedCustomizer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Threading;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// 设置页：只保留与 UI 强相关的代码（导航参数处理、控件焦点、打开外部链接），
    /// 业务逻辑统一放在 SettingsViewModel 中。
    /// </summary>
    public sealed partial class SettingsPage : Page, IWindowCloseAware
    {
        private static readonly IAppLog Log = AppLog.For<SettingsPage>();

        /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
        public SettingsViewModel ViewModel { get; }

        /// <summary>导航到设置页时是否请求聚焦“解除地区限制”按钮。</summary>
        private bool _focusRegionPolicyButton;

        private CancellationTokenSource _pageLifetime = new();

        public SettingsPage()
        {
            FeedbackService feedbackService = App.MainWindow?.Feedback
                ?? throw new InvalidOperationException("主窗口反馈服务尚未初始化。");
            MainWindow window = App.MainWindow
                ?? throw new InvalidOperationException("主窗口文档服务尚未初始化。");
            ViewModel = new SettingsViewModel(
                feedbackService,
                window.Documents,
                window.Settings,
                window.Donations,
                window.Region,
                new SettingsInteraction(),
                new ResourceLoader().GetString("LanguageTag"));
            InitializeComponent();

            // 订阅视图模型发出的 UI 请求，让视图模型不依赖具体控件。
            ViewModel.OpenLinkRequested += OnOpenLinkRequested;
            ViewModel.DocumentOpenRequested += OnDocumentOpenRequested;
            ViewModel.DocumentPreparationFailed += OnDocumentPreparationFailed;
            ViewModel.LoadingOverlayRequested += OnLoadingOverlayRequested;
            ViewModel.MessageRequested += OnMessageRequested;
            ViewModel.ErrorRequested += OnErrorRequested;
            ViewModel.ThemeChangeRequested += OnThemeChangeRequested;
            ViewModel.MaterialChangeRequested += OnMaterialChangeRequested;
            Loaded += SettingsPage_Loaded;
        }

        /// <summary>错误样式和反馈按钮由页面交给统一 UI 服务处理。</summary>
        private async void OnErrorRequested(object? sender, SettingsErrorRequestedEventArgs e)
        {
            _ = sender;

            try
            {
                await DialogService.ShowErrorAsync(e.Title, e.Details, e.Source);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "显示设置页可反馈错误失败");
            }
        }

        /// <summary>将视图模型产生的普通结果提示转换为 UI 对话框。</summary>
        private async void OnMessageRequested(object? sender, SettingsMessageRequestedEventArgs e)
        {
            _ = sender;

            try
            {
                await DialogService.ShowMessageAsync(e.Title, e.Message, e.CloseButtonText);
            }
            catch (Exception ex)
            {
                // 事件处理器必须吸收并记录展示异常，不能让通知失败变成未处理的 UI 异常。
                Log.Error(ex, "显示设置页反馈结果失败");
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_pageLifetime.IsCancellationRequested)
            {
                _pageLifetime.Dispose();
                _pageLifetime = new CancellationTokenSource();
            }

            _focusRegionPolicyButton = e.Parameter is string parameter &&
                parameter == "RegionPolicy";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            CancelPageOperations();
            base.OnNavigatedFrom(e);
        }

        /// <summary>通过窗口已有关闭协议取消设置页操作，不让 Store 查询或购买继续更新离开的页面。</summary>
        public void OnWindowClosing()
        {
            CancelPageOperations();
        }

        private void CancelPageOperations()
        {
            _pageLifetime.Cancel();
            ViewModel.DonateCommand.Cancel();
            ViewModel.UnlockRegionPolicyCommand.Cancel();
        }

        /// <summary>
        /// 页面加载完成后启动视图模型初始化，并按需聚焦“解除地区限制”按钮。
        /// </summary>
        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            CancellationToken cancellationToken = _pageLifetime.Token;
            try
            {
                if (_focusRegionPolicyButton)
                {
                    _focusRegionPolicyButton = false;
                    FocusRegionPolicyButton();
                }

                // 设置项已在构造时读取；许可证查询由页面生命周期取消，并在事件内等待和观察结果。
                await ViewModel.InitializeAsync(GetWindowHandle(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Log.Debug("设置页离开或窗口关闭，已取消页面初始化");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设置页初始化失败");
            }
        }

        /// <summary>将主题设置意图转换为窗口外观更新，持久化仍由视图模型交给设置服务。</summary>
        private void OnThemeChangeRequested(object? sender, string themeSetting)
        {
            _ = sender;
            ApplyAppearanceChange(() =>
            {
                ElementTheme theme = themeSetting switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };

                AppThemeManager.CurrentTheme = theme;
                if (App.MainWindow?.Content is FrameworkElement root)
                {
                    root.RequestedTheme = theme;
                }

                AppThemeManager.UpdateTitleBarColors();
            }, "theme");
        }

        /// <summary>将材质设置意图转换为窗口背景更新，不在页面重复写入用户设置。</summary>
        private void OnMaterialChangeRequested(object? sender, string materialSetting)
        {
            _ = sender;
            ApplyAppearanceChange(() =>
            {
                AppThemeManager.CurrentMaterial = materialSetting switch
                {
                    "MicaAlt" => BackgroundMaterial.MicaAlt,
                    "Acrylic" => BackgroundMaterial.Acrylic,
                    _ => BackgroundMaterial.Mica
                };
                AppThemeManager.ApplyMaterial();
            }, "material");
        }

        private void ApplyAppearanceChange(Action change, string context)
        {
            void ApplyChange()
            {
                try
                {
                    change();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "应用设置页外观请求失败，类型={AppearanceContext}", context);
                }
            }

            // 正常绑定回调同步更新外观；若请求来自后台线程，则只在窗口 Dispatcher 上接触控件。
            if (DispatcherQueue.HasThreadAccess)
            {
                ApplyChange();
                return;
            }

            if (!DispatcherQueue.TryEnqueue(ApplyChange))
            {
                Log.Warning("无法将外观请求提交到窗口 UI 线程，类型={AppearanceContext}", context);
            }
        }

        /// <summary>将焦点移动到“解除地区限制”按钮（纯 UI 逻辑）。</summary>
        private void FocusRegionPolicyButton()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UnlockRegionPolicyButton.StartBringIntoView();
                UnlockRegionPolicyButton.Focus(FocusState.Keyboard);
            });
        }

        /// <summary>获取主窗口句柄，供 Store 购买与许可证查询使用。</summary>
        private static IntPtr GetWindowHandle()
        {
            return App.MainWindow is MainWindow window
                ? WinRT.Interop.WindowNative.GetWindowHandle(window)
                : IntPtr.Zero;
        }

        /// <summary>处理视图模型发出的打开外部链接请求。</summary>
        private async void OnOpenLinkRequested(object? sender, string url)
        {
            _ = sender;

            if (App.MainWindow is MainWindow window)
            {
                await window.ExternalLaunch.OpenLinkAsync(url);
            }
        }

        /// <summary>使用与帮助文档一致的受控路径和系统关联程序打开随包文档。</summary>
        private async void OnDocumentOpenRequested(
            object? sender,
            ApplicationDocumentOpenRequestedEventArgs e)
        {
            _ = sender;

            bool opened = false;
            if (App.MainWindow is MainWindow window)
            {
                opened = await window.ExternalLaunch.OpenFileAsync(e.FilePath);
            }

            if (opened)
            {
                return;
            }

            Log.Error("打开应用文档失败，类型={DocumentKind}", e.Kind);
            await DialogService.ShowDocumentOpenFailureAsync();
        }

        /// <summary>记录文档生成阶段的结构化诊断，并向用户展示统一的打开失败提示。</summary>
        private async void OnDocumentPreparationFailed(
            object? sender,
            ApplicationDocumentResult result)
        {
            _ = sender;

            Log.Error(
                "生成应用文档失败，状态={DocumentStatus}，诊断={Diagnostic}",
                result.Status,
                LogPrivacy.PrepareDiagnostic(result.Diagnostic));

            await DialogService.ShowDocumentOpenFailureAsync();
        }

        /// <summary>打开贡献者链接：只从 UI 元素中取出数据对象，其余逻辑交给视图模型。</summary>
        private void OnContributorLinkClicked(object sender, RoutedEventArgs _)
        {
            if (sender is FrameworkElement { Tag: Contributor contributor })
            {
                ViewModel.OpenContributorLink(contributor);
            }
        }

        /// <summary>
        /// 处理视图模型发出的加载遮罩显隐请求。
        /// </summary>
        private void OnLoadingOverlayRequested(object? sender, bool isVisible)
        {
            _ = sender;

            if (App.MainWindow is MainWindow window)
            {
                window.SetLoadingOverlayVisible(isVisible);
            }
        }
    }
}
