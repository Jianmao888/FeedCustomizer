using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.DataService;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.IO;
using System.Threading.Tasks;

namespace FeedCustomizer.ViewModels
{
    /// <summary>
    /// 单个源（以及“添加/编辑源”页面）的视图模型：
    /// 封装源的编辑状态、图标选择与保存等业务逻辑。
    /// </summary>
    public partial class FeedViewModel : ObservableObject
    {
        private readonly ResourceLoader _resourceLoader = new();

        /// <summary>标记用户是否切换过图标模式（用于决定保存按钮是否可用）。</summary>
        private bool _iconModeChanged;

        // =====================
        // 数据
        // =====================

        /// <summary>底层的源数据模型。</summary>
        public Feed FeedItem { get; }

        /// <summary>是否为编辑模式（决定页面标题等展示）。</summary>
        public bool IsEditMode { get; }

        // =====================
        // 绑定到 UI 的属性
        // =====================

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsesDefaultImage))]
        public partial string ImagePath { get; set; } = Constants.DefaultImageRelativePath;

        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Description { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Url { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSaveAndNotSaving))]
        public partial bool CanSave { get; set; }

        [ObservableProperty]
        public partial bool CanClearImage { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustomIconMode))]
        public partial bool IsWebsiteIconMode { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSaveAndNotSaving))]
        public partial bool IsSaving { get; set; }

        /// <summary>预览图片（绑定到页面上的 Image.Source）。</summary>
        [ObservableProperty]
        public partial BitmapImage? PreviewImage { get; set; }

        /// <summary>页面标题：编辑模式显示“编辑源配置”，否则显示“添加源配置”。</summary>
        public string PageTitle =>
            _resourceLoader.GetString(IsEditMode ? "EditFeedPageTitle" : "AddFeedPageTitle");

        /// <summary>源图标（用于主列表卡片显示）。</summary>
        public BitmapImage BitmapImage =>
            new(new Uri(ImageHelper.GetImageFullPathFromXmlRelativePath(ImagePath)));

        /// <summary>是否使用默认图片。</summary>
        public bool UsesDefaultImage => string.Equals(
            Path.GetFileName(ImagePath),
            Constants.DefaultImageName,
            StringComparison.OrdinalIgnoreCase);

        /// <summary>是否使用自定义图标（网站图标模式的取反）。</summary>
        public bool IsCustomIconMode
        {
            get => !IsWebsiteIconMode;
            set => IsWebsiteIconMode = !value;
        }

        /// <summary>保存按钮的最终可用状态：可保存且未在保存中。</summary>
        public bool CanSaveAndNotSaving => CanSave && !IsSaving;

        public bool IsEdited { get => FeedItem.IsEdited; set => FeedItem.IsEdited = value; }

        public string Id => FeedItem.Id;

        // =====================
        // UI 请求事件（视图模型不依赖具体控件）
        // =====================

        /// <summary>保存完成后请求页面返回主页。</summary>
        public event EventHandler? SaveCompleted;

        /// <summary>请求显示或隐藏加载遮罩。</summary>
        public event EventHandler<bool>? LoadingOverlayRequested;

        /// <summary>请求展示“网页图标获取失败”对话框。</summary>
        public event EventHandler<string>? WebIconFetchErrorRequested;

        // =====================
        // 构造函数
        // =====================

        public FeedViewModel() : this(new Feed(), isEditMode: false)
        {
        }

        public FeedViewModel(Feed feedItem) : this(feedItem, isEditMode: true)
        {
        }

        private FeedViewModel(Feed feedItem, bool isEditMode)
        {
            FeedItem = feedItem;
            IsEditMode = isEditMode;
            Name = feedItem.Name;
            Description = feedItem.Description;
            Url = feedItem.Url;
            ImagePath = feedItem.ImagePath;
            CanClearImage = !string.Equals(
                ImagePath,
                Constants.DefaultImageRelativePath,
                StringComparison.OrdinalIgnoreCase);
            IsWebsiteIconMode = !isEditMode
                || Path.GetFileName(ImagePath).StartsWith("web-", StringComparison.OrdinalIgnoreCase);

            if (!UsesDefaultImage)
            {
                PreviewImage = new BitmapImage(
                    new Uri(ImageHelper.GetImageFullPathFromXmlRelativePath(ImagePath)));
            }
        }

        // =====================
        // 属性变化处理
        // =====================

        partial void OnNameChanged(string value) => CheckCanSave();
        partial void OnDescriptionChanged(string value) => CheckCanSave();
        partial void OnUrlChanged(string value) => CheckCanSave();
        partial void OnImagePathChanged(string value) => CheckCanSave();

        partial void OnIsWebsiteIconModeChanged(bool value)
        {
            // 用户切换图标模式后，需要允许重新保存。
            _iconModeChanged = true;
            CheckCanSave();
        }

        // =====================
        // 业务逻辑
        // =====================

        /// <summary>
        /// 根据当前输入重新计算“可保存”状态。
        /// </summary>
        private void CheckCanSave()
        {
            CanSave = !string.IsNullOrWhiteSpace(Name)
                && !string.IsNullOrWhiteSpace(Url)
                && IsEditing();
        }

        /// <summary>
        /// 判断当前输入是否与原始数据不同（或切换过图标模式）。
        /// </summary>
        private bool IsEditing()
        {
            return !Name.Equals(FeedItem.Name)
                || !Url.Equals(FeedItem.Url)
                || !Description.Equals(FeedItem.Description)
                || !ImagePath.Equals(FeedItem.ImagePath)
                || _iconModeChanged;
        }

        /// <summary>
        /// 选择并保存图片。
        /// </summary>
        [RelayCommand]
        private async Task SelectImageAsync()
        {
            string imageName = await ImageHelper.PickAndSaveImageAsync();
            if (string.IsNullOrWhiteSpace(imageName))
            {
                return;
            }

            ImagePath = Path.Combine("Images", imageName);
            CanClearImage = true;
            PreviewImage = new BitmapImage(
                new Uri(ImageHelper.GetImageFullPathFromXmlRelativePath(ImagePath)));
            CheckCanSave();
        }

        /// <summary>
        /// 清除自定义图片，回退到默认图片。
        /// </summary>
        [RelayCommand]
        private void ClearImage()
        {
            ImagePath = Constants.DefaultImageRelativePath;
            CanClearImage = false;
            PreviewImage = null;
            CheckCanSave();
        }

        /// <summary>
        /// 规范化网址，并在无协议时补充 http(s)。
        /// </summary>
        private Uri NormalizeUrl()
        {
            Uri websiteUri = ManifestXmlService.NormalizeHttpUri(Url);
            Url = websiteUri.AbsoluteUri;
            return websiteUri;
        }

        /// <summary>
        /// 下载网页图标并更新预览。
        /// </summary>
        private async Task<BitmapImage> PrepareWebsiteIconAsync(Uri requestedUri)
        {
            WebsiteIconResult iconResult = await WebsiteIconDownloader.DownloadAsync(requestedUri);
            Url = iconResult.PageUri.AbsoluteUri;
            SetDownloadedImage(iconResult.RelativeIconPath);
            return await ImageHelper.LoadImageFromPathAsync(
                ImageHelper.GetImageFullPathFromXmlRelativePath(iconResult.RelativeIconPath))
                ?? throw new InvalidDataException("网页图标已保存，但无法加载预览。");
        }

        /// <summary>
        /// 设置下载到的图标路径。
        /// </summary>
        private void SetDownloadedImage(string relativePath)
        {
            ImagePath = relativePath;
            CanClearImage = true;
            CheckCanSave();
        }

        /// <summary>
        /// 将编辑结果写回模型，并放入数据服务缓存供主页读取。
        /// </summary>
        private void CommitChanges()
        {
            FeedItem.Name = Name;
            FeedItem.Description = string.IsNullOrWhiteSpace(Description) || Description == "string.Empty"
                ? Name
                : Description;
            FeedItem.Url = Url;
            FeedItem.ImagePath = ImagePath;
            _iconModeChanged = false;
            IsEdited = true;

            // 将编辑或添加的源放进缓存，主页面将读取此数据。
            AddOrEditFeedDataService.Feed = FeedItem;
        }

        /// <summary>
        /// 离开页面时撤销未提交的更改。
        /// </summary>
        public void CancelChanges()
        {
            if (IsEdited)
            {
                return;
            }

            Name = FeedItem.Name;
            Description = FeedItem.Description;
            Url = FeedItem.Url;
            ImagePath = FeedItem.ImagePath;
            _iconModeChanged = false;
        }

        // =====================
        // 命令（绑定到页面按钮）
        // =====================

        /// <summary>
        /// 保存源：规范化网址，需要时下载网页图标，提交更改后请求返回主页。
        /// </summary>
        [RelayCommand]
        private async Task SaveAsync()
        {
            if (IsSaving)
            {
                return;
            }

            IsSaving = true;
            Uri? websiteUri = null;
            try
            {
                websiteUri = NormalizeUrl();

                if (IsWebsiteIconMode)
                {
                    LoadingOverlayRequested?.Invoke(this, true);
                    try
                    {
                        PreviewImage = await PrepareWebsiteIconAsync(websiteUri);
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                    finally
                    {
                        LoadingOverlayRequested?.Invoke(this, false);
                    }
                }

                CommitChanges();
                SaveCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                string details = string.Join(
                    Environment.NewLine,
                    $"URL: {websiteUri?.AbsoluteUri ?? Url}",
                    $"Exception: {ex.GetType().FullName}",
                    $"Message: {ex.Message}",
                    string.Empty,
                    ex.ToString());
                WebIconFetchErrorRequested?.Invoke(this, details);
            }
            finally
            {
                IsSaving = false;
            }
        }
    }
}
