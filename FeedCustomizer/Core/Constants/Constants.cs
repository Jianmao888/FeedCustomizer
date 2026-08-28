namespace FeedCustomizer.Core.Constants
{
    public static class Constants
    {
        // 默认图片信息
        public static readonly string DefaultImageName = "Default.png";
        public static readonly string DefaultImageRelativePath = "Images\\Default.png";

        // 最大列表数量
        public static readonly int MaxFeedNum = 32;

        // 应用信息
        // 合作者姓名与商店链接统一写入常量，方便维护
        public static readonly string OpenSourceLink = "https://gitee.com/jianmao888/FeedCustomizer";

        // 捐赠者版（Store 加载项）的商店 ID
        // 请在合作伙伴中心创建“捐赠者版”加载项后，将其 Store ID 填入此处
        public static readonly string DonationAddOnStoreId = "Donation_Tier";

        // 如果有多个合作者，可以在这里添加姓名与对应的商店链接
        public static readonly string[] DeveloperNames = ["窗边的贱猫", "惜忆想睡觉"];
        public static readonly string[] DeveloperStoreLinks
            = [
                "https://gitee.com/jianmao888",
                "https://apps.microsoft.com/search/publisher?name=%E6%83%9C%E5%BF%86%E6%83%B3%E7%9D%A1%E8%A7%89&"
            ];

        // 设置项默认值
        public static class SettingDefaults
        {
            public const string AppTheme = "System";
            public const string AppMaterial = "Mica";
            public const bool IsFirstRun = true;
            public const bool AutoEnableDeveloperMode = false;
        }
    }
}
