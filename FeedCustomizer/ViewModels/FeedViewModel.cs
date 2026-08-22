using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.DataService;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace FeedCustomizer.ViewModels
{
    public class FeedViewModel
    {
        // ====================================
        // 初始化逻辑
        // ====================================
        public Feed FeedItem
        {
            get;
            set
            {
                if (value != null)
                {
                    IsEditMode = true;
                    CanClearImage.Value = value.ImagePath != Constants.DefaultImageRelativePath;
                }
                field = value!;
            }
        } = new();
        private bool IsEditMode { get; set; } = false;  // 标记当前是编辑模式还是添加模式
        private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader resourceLoader = new();
        // 根据 IsEditMode 属性返回不同的页面标题
        public string PageTitle
        {
            get
            {
                return IsEditMode ? resourceLoader.GetString("EditFeedPageTitle") : resourceLoader.GetString("AddFeedPageTitle");
            }
        }

        public FeedViewModel(Feed FeedItem)
        {
            this.FeedItem = FeedItem;
            _name = FeedItem.Name;
            _description = FeedItem.Description;
            _url = FeedItem.Url;
            _imagePath = FeedItem.ImagePath;
        }
        public FeedViewModel()
        {
            // 默认构造函数
            _name = FeedItem.Name;
            _description = FeedItem.Description;
            _url = FeedItem.Url;
            _imagePath = FeedItem.ImagePath;
        }


        // ====================================
        // 绑定到 UI 的属性
        // ====================================
        public BitmapImage BitmapImage
        {
            get
            {
                return new BitmapImage(new Uri(ImageHelper.GetImageFullPathFromXmlRelativePath(ImagePath)));
            }
        }

        public ObservableValue<bool> CanClearImage { get; set; } = new(false);
        public bool IsEdited { get => FeedItem.IsEdited; set => FeedItem.IsEdited = value; }
        public string Id
        {
            get => FeedItem.Id;
        }
        private string _name = string.Empty;
        private string _description = string.Empty;
        private string _url = string.Empty;
        private string _imagePath = Constants.DefaultImageRelativePath; // 存储相对路径
        private bool _iconModeChanged;

        private string CacheImagePath { get; set; } = string.Empty; // 用于缓存图片路径，防止在编辑过程中丢失

        public string Name
        {
            get => _name.Equals(FeedItem.Name) ? FeedItem.Name : _name;
            set
            {
                _name = value;
                CheckCanSave();
            }
        }

        public string Description
        {
            get => _description.Equals(FeedItem.Description) ? FeedItem.Description : _description;
            set
            {
                _description = value;
                CheckCanSave();
            }
        }

        public string Url
        {
            get => _url.Equals(FeedItem.Url) ? FeedItem.Url : _url;
            set
            {
                _url = value;
                CheckCanSave();
            }
        }

        public string ImagePath
        {
            get => _imagePath.Equals(FeedItem.ImagePath) ? FeedItem.ImagePath : _imagePath;
            set
            {
                if (_imagePath != FeedItem.ImagePath && _imagePath != Constants.DefaultImageRelativePath)
                {
                    ImageHelper.DeleteImage(Path.Combine(AppDataPaths.FeedProviderFolder, _imagePath));
                }
                _imagePath = value;
                CheckCanSave();
            }
        }

        /// <summary>
        /// 这个属性用于标记是否可以保存，在 UI 中绑定到保存按钮的可用性
        /// </summary>
        public ObservableValue<bool> CanSave { get; set; } = new(false);
        private void CheckCanSave()
        {
            CanSave.Value = !string.IsNullOrWhiteSpace(Name)
                && !string.IsNullOrWhiteSpace(Url)
                && IsEditing();
        }

        private bool IsEditing()
        {
            return !_name.Equals(FeedItem.Name)
                || !_url.Equals(FeedItem.Url)
                || !_description.Equals(FeedItem.Description)
                || !_imagePath.Equals(FeedItem.ImagePath)
                || _iconModeChanged;
        }


        // ====================================
        // 方法（按钮点击事件）
        // ====================================
        public async Task<BitmapImage?> SelectImage()
        {
            string imageName = await ImageHelper.PickAndSaveImageAsync();
            if (!string.IsNullOrWhiteSpace(imageName))
            {
                if (ImagePath != FeedItem.ImagePath && ImagePath != Constants.DefaultImageRelativePath)
                {

                }
                ImagePath = "Images\\" + imageName;
                CanClearImage.Value = true;
                CheckCanSave();
                return new BitmapImage(new Uri(ImageHelper.GetImageFullPathFromXmlRelativePath(ImagePath)));
            }
            return null;
        }

        public BitmapImage? ClearImage()
        {
            if (ImagePath != Constants.DefaultImageRelativePath)
            {
                ImageHelper.DeleteImage(Path.Combine(AppDataPaths.FeedProviderFolder, _imagePath));
            }
            ImagePath = "Images\\Default.png";
            CanClearImage.Value = false;
            CheckCanSave();
            return new BitmapImage(new Uri(ImageHelper.GetImageFullPathFromXmlRelativePath(ImagePath)));
        }

        public void MarkIconModeChanged()
        {
            _iconModeChanged = true;
            CheckCanSave();
        }

        public void SetDownloadedImage(string relativePath)
        {
            ImagePath = relativePath;
            CanClearImage.Value = true;
            CheckCanSave();
        }

        public void CommitChanges()
        {
            // 缓存旧的图片路径，当外部列表应用后再删除旧图片
            if (string.IsNullOrWhiteSpace(CacheImagePath))
            {
                CacheImagePath = FeedItem.ImagePath;
            }
            else
            {
                // 如果已经缓存过旧图片路径，说明是多次编辑，删除上一次的旧图片
                if (_imagePath != FeedItem.ImagePath && FeedItem.ImagePath != Constants.DefaultImageRelativePath)
                {
                    ImageHelper.DeleteImage(Path.Combine(AppDataPaths.FeedProviderFolder, _imagePath));
                }
            }

            FeedItem.Name = Name;
            FeedItem.Description = string.IsNullOrWhiteSpace(Description) || Description == "string.Empty"
                ? Name
                : Description;
            FeedItem.Url = Url;
            FeedItem.ImagePath = ImagePath;
            _iconModeChanged = false;
            IsEdited = true;
            Debug.WriteLine(IsEdited);
            Debug.WriteLine(this.GetHashCode());

            // 将编辑或添加的源放进缓存，主页面将读取此数据
            AddOrEditFeedDataService.Feed = FeedItem;
        }

        public void CancelChanges()
        {
            if (IsEdited) return;
            // 清理图片缓存
            if (_imagePath != FeedItem.ImagePath && _imagePath != Constants.DefaultImageRelativePath)
            {
                ImageHelper.DeleteImage(Path.Combine(AppDataPaths.FeedProviderFolder, _imagePath));
            }

            // 重置临时属性
            _name = FeedItem.Name;
            _description = FeedItem.Description;
            _url = FeedItem.Url;
            _imagePath = FeedItem.ImagePath;
            _iconModeChanged = false;
        }

        public async Task Delete()
        {
            if (FeedItem.ImagePath != Constants.DefaultImageRelativePath)
            {
                ImageHelper.DeleteImage(Path.Combine(AppDataPaths.FeedProviderFolder, FeedItem.ImagePath));
            }
        }

        public async Task DeleteOldCacheImage()
        {
            if (!string.IsNullOrWhiteSpace(CacheImagePath) && CacheImagePath != Constants.DefaultImageRelativePath)
            {
                ImageHelper.DeleteImage(Path.Combine(AppDataPaths.FeedProviderFolder, CacheImagePath));
                CacheImagePath = string.Empty;
            }
        }

        public async Task DeleteNewCacheImage()
        {
            if (!string.IsNullOrWhiteSpace(CacheImagePath) && ImagePath != Constants.DefaultImageRelativePath)
            {
                ImageHelper.DeleteImage(Path.Combine(AppDataPaths.FeedProviderFolder, ImagePath));
                ImagePath = string.Empty;
            }
        }
    }
}
