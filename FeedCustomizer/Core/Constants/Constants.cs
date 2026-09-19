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
        // 开源仓库链接
        public static readonly string GiteeUrl = "https://gitee.com/jianmao888/FeedCustomizer";
        public static readonly string GitHubLink = "https://github.com/Jianmao888/FeedCustomizer";

        // 版权开发者（版权信息中固定只显示这一位）
        public static readonly string DeveloperName = "窗边的贱猫";
        public static readonly string DeveloperLink = "https://gitee.com/jianmao888";

        // 反馈邮件使用固定地址与固定标识。标识不会按邮件变化，便于开发者建立稳定的邮件整理规则。
        public const string FeedbackEmailAddress = "jianmao888@outlook.com";
        public const string FeedbackIdentifier = "7c64a21e-b32e-4ca8-b958-4e8758fe9e1b";
        public const string FeedbackExportFolderName = "FeedCustomizer";

        // 贡献者名单，未来新增贡献者时在下方数组中追加即可
        public static readonly Contributor[] Contributors =
            [
                new("窗边的贱猫", "https://github.com/Jianmao888/"),
                new("惜忆想睡觉", "https://apps.microsoft.com/search/publisher?name=%E6%83%9C%E5%BF%86%E6%83%B3%E7%9D%A1%E8%A7%89&")
            ];

        // 捐赠者版（Store 加载项）的商店 ID
        // 请在合作伙伴中心创建“捐赠者版”加载项后，将其 Store ID 填入此处
        public static readonly string DonationAddOnStoreId = "Donation_Tier";

        // 设置项默认值
        public static class SettingDefaults
        {
            public const string AppTheme = "System";
            public const string AppMaterial = "Mica";
            public const bool IsFirstRun = true;
            public const bool AutoEnableDeveloperMode = false;
        }
    }

    /// <summary>贡献者信息：姓名与对应的个人主页链接。</summary>
    public sealed record Contributor(string Name, string Link);
}
