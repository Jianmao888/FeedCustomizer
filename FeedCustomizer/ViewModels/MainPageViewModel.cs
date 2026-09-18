using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.DataService;
using FeedCustomizer.Core.Deployment;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;

namespace FeedCustomizer.ViewModels
{
    /// <summary>
    /// 主页的视图模型：集中管理源列表、源提供程序开关以及页面状态。
    /// 部署意图交给协调器，页面后置代码负责导航、遮罩、对话框等 UI 交互。
    /// </summary>
    public partial class MainPageViewModel : ObservableObject
    {
        private static readonly IAppLog Log = AppLog.For<MainPageViewModel>();

        // =====================
        // 数据
        // =====================

        /// <summary>源列表，绑定到设置展开器。</summary>
        [ObservableProperty]
        public partial ObservableCollection<FeedViewModel> Feeds { get; set; } = [];

        /// <summary>是否存在源，供未来空状态展示使用。</summary>
        public bool HasFeeds => Feeds.Count > 0;

        /// <summary>已删除、等待“应用”的源列表。</summary>
        private readonly List<FeedViewModel> _deleteFeeds = [];

        /// <summary>是否存在待应用的更改。</summary>
        private bool _canApplyFeeds;

        // 开关是用户意图；查询失败时只能退回最后确认的状态，不能把用户刚点击的值当作执行结果。
        private bool _lastConfirmedProviderEnabled;

        /// <summary>主页初始化任务，由窗口启动协调器等待其完成。</summary>
        public Task InitializationTask { get; }

        /// <summary>
        /// 资源同步需求的早期判定结果。窗口据此在长时间文件复制前切换启动覆盖层，
        /// 不依赖 ViewModel 的具体初始化实现或 UI 事件订阅时机。
        /// </summary>
        public Task<bool> ResourceSynchronizationRequiredTask => _resourceSynchronizationRequired.Task;

        private readonly TaskCompletionSource<bool> _resourceSynchronizationRequired =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // =====================
        // UI 状态绑定
        // =====================

        /// <summary>是否正在执行耗时操作（用于禁用控件）。</summary>
        [ObservableProperty]
        public partial bool IsLoading { get; set; } = true;

        /// <summary>常规按钮是否可用。</summary>
        [ObservableProperty]
        public partial bool IsButtonsEnabled { get; set; }

        /// <summary>“应用/取消”按钮是否可用。</summary>
        [ObservableProperty]
        public partial bool IsApplyButtonEnabled { get; set; }

        /// <summary>地区限制警告横幅是否可见。</summary>
        [ObservableProperty]
        public partial bool IsRegionWarningVisible { get; set; }

        /// <summary>自定义源提供程序是否启用。</summary>
        [ObservableProperty]
        public partial bool IsFeedProviderEnabled { get; set; }

        // =====================
        // UI 请求事件（视图模型不依赖具体控件）
        // =====================

        /// <summary>请求页面进行导航。</summary>
        public event EventHandler<MainPageNavigationRequestedEventArgs>? NavigationRequested;

        /// <summary>请求显示或隐藏加载遮罩。</summary>
        public event EventHandler<bool>? LoadingOverlayRequested;

        /// <summary>请求打开帮助文档文件。</summary>
        public event EventHandler<StorageFile>? HelpFileReady;

        /// <summary>
        /// Provider 注册未完成时请求 UI 层处理。结果包含错误类别与诊断，
        /// ViewModel 不直接决定对话框样式或展示时机。
        /// </summary>
        public event EventHandler<ProviderRegistrationResult>? ProviderRegistrationFailed;

        // =====================
        // 初始化逻辑
        // =====================

        public MainPageViewModel()
        {
            // 源集合变化时同步 HasFeeds，便于未来展示空状态。
            Feeds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFeeds));
            InitializationTask = InitializeAllFeedsAsync();
        }

        /// <summary>
        /// 初始化所有源数据，并在完成后设置页面状态。
        /// </summary>
        private async Task InitializeAllFeedsAsync()
        {
            Task regionInitializationTask = InitializeRegionStateAsync();
            Task feedInitializationTask = InitAsync();
            await Task.WhenAll(regionInitializationTask, feedInitializationTask);

            IsLoading = false;
            IsButtonsEnabled = true;
            IsApplyButtonEnabled = _canApplyFeeds;
        }

        /// <summary>
        /// 初始化地区状态，并在进程生命周期内缓存检测结果。
        /// </summary>
        private async Task InitializeRegionStateAsync()
        {
            // 如果系统策略已解限，则不展示地区限制警告横幅。
            bool? cachedIsNonEu = RegionDataService.IsNonEuropeanUnionRegion;
            bool? cachedIsPolicyEnabled = RegionDataService.IsThirdPartyWidgetFeedEnabled;

            Task<bool> regionTask = cachedIsNonEu is bool isNonEu
                ? Task.FromResult(isNonEu)
                : Task.Run(DeviceRegionTool.IsNonEuropeanUnionRegion);
            Task<bool> policyTask = cachedIsPolicyEnabled is bool isPolicyEnabled
                ? Task.FromResult(isPolicyEnabled)
                : RegionPolicyService.IsThirdPartyWidgetFeedEnabledAsync();

            await Task.WhenAll(regionTask, policyTask);

            bool regionResult = regionTask.Result;
            bool policyResult = policyTask.Result;
            RegionDataService.IsNonEuropeanUnionRegion = regionResult;
            RegionDataService.IsThirdPartyWidgetFeedEnabled = policyResult;
            IsRegionWarningVisible = regionResult && !policyResult;
        }

        /// <summary>
        /// 初始化源列表、资源文件和源提供程序状态。
        /// </summary>
        private async Task InitAsync()
        {
            try
            {
                DeploymentInspection inspection = await ProviderDeployment.Current.InspectAsync();
                bool providerInstalled = inspection.Installed;
                bool requiresResourceSynchronization = !inspection.ResourcesCurrent;
                _resourceSynchronizationRequired.TrySetResult(requiresResourceSynchronization);

                SetFeedProviderEnabled(providerInstalled);

                // 先通知启动协调器切换加载遮罩，再执行耗时准备；准备工作不影响现有注册目录。
                if (requiresResourceSynchronization) await ProviderDeployment.Current.PrepareAsync();

                if (FeedListDataService.IsEnable)
                {
                    LoadFeedsFromDataService();
                }
                else
                {
                    await LoadFeedsFromXmlAsync();
                }

                AddPendingFeed();
                if (IsFeedProviderEnabled) await TryInstallFeedProviderAsync();
            }
            finally
            {
                // 前置探测异常时也必须解除窗口等待；初始化异常会由调用方展示。
                _resourceSynchronizationRequired.TrySetResult(false);
            }
        }

        /// <summary>
        /// 设置源提供程序开关状态：优先使用进程内缓存的用户开关状态。
        /// </summary>
        private void SetFeedProviderEnabled(bool providerInstalled)
        {
            _lastConfirmedProviderEnabled = providerInstalled;
            IsFeedProviderEnabled = FeedProviderEnableDataService.IsFeedProviderEnabled is bool cachedEnabled
                ? cachedEnabled
                : providerInstalled;
        }

        /// <summary>
        /// 如果有新建或编辑的源，则将其加入列表。
        /// </summary>
        private void AddPendingFeed()
        {
            if (AddOrEditFeedDataService.Feed is Feed feed)
            {
                AddOrEditFeed(new FeedViewModel(feed));
                AddOrEditFeedDataService.Feed = null;
            }
        }

        /// <summary>
        /// 从 Xml 文件加载源列表。
        /// </summary>
        private async Task LoadFeedsFromXmlAsync()
        {
            Feeds.Clear();
            try
            {
                var feedItems = await ManifestXmlService.Read();
                foreach (var item in feedItems)
                {
                    Feeds.Add(new FeedViewModel(item));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "从 Provider 清单加载订阅源失败");
            }
        }

        /// <summary>
        /// 从内存数据服务中加载源列表与待删除列表。
        /// </summary>
        private void LoadFeedsFromDataService()
        {
            foreach (var item in FeedListDataService.FeedList)
            {
                Feeds.Add(new FeedViewModel(item));
            }

            foreach (var item in FeedListDataService.DeleteFeedList)
            {
                _deleteFeeds.Add(new FeedViewModel(item));
            }

            _canApplyFeeds = FeedListDataService.CanAppltFeeds;
        }

        /// <summary>
        /// 从添加/编辑页返回后刷新状态。
        /// </summary>
        public void RefreshAfterNavigation()
        {
            if (AddOrEditFeedDataService.Feed is Feed feed)
            {
                AddOrEditFeed(new FeedViewModel(feed));
                AddOrEditFeedDataService.Feed = null;
            }

            IsLoading = false;
            IsButtonsEnabled = true;
            IsApplyButtonEnabled = _canApplyFeeds;
        }

        // =====================
        // 业务逻辑
        // =====================

        /// <summary>
        /// 进入忙碌状态：禁用相关控件，防止用户重复操作。
        /// </summary>
        private void BeginLoading()
        {
            IsLoading = true;
            IsButtonsEnabled = false;
            IsApplyButtonEnabled = false;
        }

        /// <summary>
        /// 结束忙碌状态：恢复控件，并同步“应用”按钮的可用性。
        /// </summary>
        private void EndLoading()
        {
            IsButtonsEnabled = true;
            IsApplyButtonEnabled = _canApplyFeeds;
            IsLoading = false;
        }

        /// <summary>
        /// 更新是否存在待应用更改，并同步“应用/取消”按钮状态。
        /// </summary>
        private void MarkCanApplyFeeds(bool canApply)
        {
            _canApplyFeeds = canApply;
            IsApplyButtonEnabled = canApply;
        }

        /// <summary>
        /// 编辑已有源：保存当前列表到数据服务后请求导航到添加/编辑页。
        /// </summary>
        public void EditFeed(FeedViewModel feed)
        {
            if (IsLoading)
            {
                return;
            }

            SaveListToDataService();
            NavigationRequested?.Invoke(
                this,
                new MainPageNavigationRequestedEventArgs(MainPageNavigationTarget.AddFeed, feed));
        }

        /// <summary>
        /// 删除源。
        /// </summary>
        public void DeleteFeed(FeedViewModel feed)
        {
            BeginLoading();
            if (Feeds.Remove(feed))
            {
                _deleteFeeds.Add(feed);
                MarkCanApplyFeeds(true);
            }

            EndLoading();
        }

        /// <summary>
        /// 编辑或添加源。
        /// </summary>
        private void AddOrEditFeed(FeedViewModel feed)
        {
            if (UpdateObject(Feeds, feed))
            {
                Log.Debug("内存中的订阅源条目已更新");
                if (feed.IsEdited)
                {
                    feed.IsEdited = false;
                    MarkCanApplyFeeds(true);
                }
            }
            else
            {
                Feeds.Add(feed);
                feed.IsEdited = false;
                MarkCanApplyFeeds(true);
            }
        }

        /// <summary>
        /// 保存源列表到 Xml 文件，并根据开关状态安装或卸载源提供程序。
        /// </summary>
        private async Task SaveFeedsAsync()
        {
            BeginLoading();
            try
            {
                var feedItems = new List<Feed>();
                foreach (var feedViewModel in Feeds)
                {
                    feedItems.Add(feedViewModel.FeedItem);
                }

                // 保存、卸载和注册在协调器的一次串行操作内完成；失败时保留“应用”能力用于重试。
                var result = await ProviderDeployment.Current.ApplyAsync(
                    feedItems, IsFeedProviderEnabled, SettingsLoader.GetAutoEnableDeveloperMode());
                if (ReportProviderRegistrationResult(result))
                {
                    _deleteFeeds.Clear();
                    _canApplyFeeds = false;
                    await CleanUpUnusedImagesAsync();
                }
            }
            catch (Exception ex)
            {
                ReportProviderRegistrationResult(ProviderRegistrationResult.Failed(string.Empty, null, ex.ToString(), string.Empty));
            }
            finally
            {
                EndLoading();
            }
        }

        /// <summary>
        /// 取消待应用的更改，并重新从 Xml 加载源列表。
        /// </summary>
        private async Task CancelPendingChangesAsync()
        {
            BeginLoading();
            try
            {
                Feeds.Clear();
                _deleteFeeds.Clear();
                FeedListDataService.FeedList.Clear();
                FeedListDataService.DeleteFeedList.Clear();
                FeedListDataService.CanAppltFeeds = false;
                FeedListDataService.IsEnable = false;
                AddOrEditFeedDataService.Feed = null;
                await LoadFeedsFromXmlAsync();
                _canApplyFeeds = false;
            }
            finally
            {
                EndLoading();
            }
        }

        /// <summary>
        /// 启用或关闭自定义源提供程序。
        /// </summary>
        public async Task EnableOrDisableFeedProviderAsync()
        {
            BeginLoading();
            try
            {
                var result = await ProviderDeployment.Current.ApplyAsync(
                    null, IsFeedProviderEnabled, SettingsLoader.GetAutoEnableDeveloperMode());
                ReportProviderRegistrationResult(result);
            }
            catch (Exception ex)
            {
                ReportProviderRegistrationResult(ProviderRegistrationResult.Failed(string.Empty, null, ex.ToString(), string.Empty));
            }
            finally
            {
                EndLoading();
            }
        }

        /// <summary>
        /// 用户在 UI 层确认后开启自动开发者模式，并重新尝试 Provider 注册。
        /// 重试结果直接返回给 UI 层，避免针对同一次失败重复触发错误提示事件。
        /// </summary>
        public async Task<ProviderRegistrationResult> RetryProviderRegistrationWithAutoDeveloperModeAsync()
        {
            // 此重试由 UI 已确认的操作触发；直接返回结果，避免再次触发同一失败事件导致重复弹窗。
            SettingsLoader.SetAutoEnableDeveloperMode(true);
            ProviderRegistrationResult result = await ProviderDeployment.Current.ApplyAsync(null, true, true);
            if (result.ProviderEnabled is bool enabled) _lastConfirmedProviderEnabled = enabled;
            IsFeedProviderEnabled = _lastConfirmedProviderEnabled;

            return result;
        }

        /// <summary>
        /// 执行完整资源检查后的注册，并将失败交给 UI 层展示。
        /// </summary>
        private async Task<bool> TryInstallFeedProviderAsync()
        {
            ProviderRegistrationResult result = await ProviderDeployment.Current.ApplyAsync(
                null, true, SettingsLoader.GetAutoEnableDeveloperMode());
            return ReportProviderRegistrationResult(result);
        }

        /// <summary>
        /// 将注册结果转换为调用方所需的布尔值，并仅在失败时通知 UI 层决定展示策略。
        /// </summary>
        private bool ReportProviderRegistrationResult(ProviderRegistrationResult result)
        {
            // 开关反映确认过的实际状态，不因一次失败便假设包已经卸载成功。
            if (result.ProviderEnabled is bool enabled) _lastConfirmedProviderEnabled = enabled;
            IsFeedProviderEnabled = _lastConfirmedProviderEnabled;
            if (result.Succeeded)
            {
                return true;
            }

            // 事件只传递领域结果，不等待或依赖任何对话框，保持 ViewModel 与具体 UI 解耦。
            ProviderRegistrationFailed?.Invoke(this, result);
            return false;
        }

        /// <summary>
        /// 更新 ObservableCollection 中的对象；找到相同 Id 则替换。
        /// </summary>
        private static bool UpdateObject(ObservableCollection<FeedViewModel> list, FeedViewModel newObj)
        {
            int index = -1;

            foreach (var item in list)
            {
                if (item.Id == newObj.Id)
                {
                    index = list.IndexOf(item);
                    break;
                }
            }

            if (index >= 0)
            {
                list[index] = newObj;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 清理图片目录中不再被任何源引用的图片。
        /// 在 UI 线程快照当前引用，随后把 Xml 读取与文件删除放到后台线程执行，
        /// 避免阻塞启动流程与界面。
        /// </summary>
        public Task CleanUpUnusedImagesAsync()
        {
            var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var feed in Feeds)
            {
                referencedPaths.Add(feed.FeedItem.ImagePath);
            }

            foreach (var feed in _deleteFeeds)
            {
                referencedPaths.Add(feed.FeedItem.ImagePath);
            }

            return CleanImagesAndReportAsync(referencedPaths);
        }

        private static async Task CleanImagesAndReportAsync(HashSet<string> references)
        {
            // 清理是非致命维护任务，但异常必须可观察，尤其不能从后台任务逃逸。
            try
            {
                var result = await ProviderDeployment.Current.CleanImagesAsync(references);
                Log.Information(
                    "图片清理维护任务结束，删除数量={DeletedCount}，诊断数量={DiagnosticCount}",
                    result.Deleted,
                    result.Diagnostics.Count);
                foreach (string diagnostic in result.Diagnostics)
                {
                    // 单条诊断可能包含用户图片文件名，仅保留在附加调试器中，不写入持久化日志。
                    Debug.WriteLine($"图片清理：{diagnostic}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "图片清理维护任务未完成");
            }
        }

        /// <summary>
        /// 将当前数据缓存至 DataService 单例，供页面往返时复用。
        /// </summary>
        private void SaveListToDataService()
        {
            FeedListDataService.FeedList.Clear();
            FeedListDataService.DeleteFeedList.Clear();

            foreach (var item in Feeds)
            {
                FeedListDataService.FeedList.Add(item.FeedItem);
            }

            foreach (var item in _deleteFeeds)
            {
                FeedListDataService.DeleteFeedList.Add(item.FeedItem);
            }

            FeedListDataService.CanAppltFeeds = _canApplyFeeds;
            FeedListDataService.IsEnable = true;
            FeedProviderEnableDataService.IsFeedProviderEnabled = IsFeedProviderEnabled;
        }

        /// <summary>
        /// 是否还能继续添加源。
        /// </summary>
        private bool CanAddFeed()
        {
            return Feeds.Count < Constants.MaxFeedNum;
        }

        /// <summary>
        /// 获取帮助文档文件。
        /// </summary>
        private static async Task<StorageFile?> GetHelpFileAsync()
        {
            return await GetHelpTool.GetHelpFileAsync();
        }

        // =====================
        // 命令（绑定到页面按钮）
        // =====================

        /// <summary>添加源：保存当前状态后请求导航到添加页。</summary>
        [RelayCommand]
        private void AddFeed()
        {
            if (IsLoading || !CanAddFeed())
            {
                return;
            }

            SaveListToDataService();
            NavigationRequested?.Invoke(
                this,
                new MainPageNavigationRequestedEventArgs(MainPageNavigationTarget.AddFeed));
        }

        /// <summary>应用更改：显示遮罩并保存源列表。</summary>
        [RelayCommand]
        private async Task ApplyFeedsAsync()
        {
            if (IsLoading)
            {
                return;
            }

            LoadingOverlayRequested?.Invoke(this, true);
            try
            {
                await SaveFeedsAsync();
            }
            finally
            {
                LoadingOverlayRequested?.Invoke(this, false);
            }
        }

        /// <summary>取消更改：显示遮罩并重新加载源列表。</summary>
        [RelayCommand]
        private async Task CancelChangesAsync()
        {
            if (IsLoading)
            {
                return;
            }

            LoadingOverlayRequested?.Invoke(this, true);
            try
            {
                await CancelPendingChangesAsync();
            }
            finally
            {
                LoadingOverlayRequested?.Invoke(this, false);
            }
        }

        /// <summary>获取帮助文档，并请求打开。</summary>
        [RelayCommand]
        private async Task GetHelpAsync()
        {
            if (await GetHelpFileAsync() is StorageFile file)
            {
                HelpFileReady?.Invoke(this, file);
            }
        }

        /// <summary>打开“关于”页面：先保存当前状态。</summary>
        [RelayCommand]
        private void About()
        {
            SaveListToDataService();
            NavigationRequested?.Invoke(
                this,
                new MainPageNavigationRequestedEventArgs(MainPageNavigationTarget.Settings));
        }

        /// <summary>跳转到解除地区限制的设置页。</summary>
        [RelayCommand]
        private void UnlockRegionPolicy()
        {
            SaveListToDataService();
            NavigationRequested?.Invoke(
                this,
                new MainPageNavigationRequestedEventArgs(
                    MainPageNavigationTarget.RegionPolicySettings,
                    "RegionPolicy"));
        }
    }

    /// <summary>
    /// 主页可触发的导航目标。
    /// </summary>
    public enum MainPageNavigationTarget
    {
        AddFeed,
        Settings,
        RegionPolicySettings,
    }

    /// <summary>
    /// 导航请求参数，视图模型通过它告知视图要导航到哪个页面。
    /// </summary>
    public sealed class MainPageNavigationRequestedEventArgs(MainPageNavigationTarget target, object? parameter = null) : EventArgs
    {
        public MainPageNavigationTarget Target { get; } = target;

        public object? Parameter { get; } = parameter;
    }
}
