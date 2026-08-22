using FeedCustomizer.Core.Constants;
using System;

namespace FeedCustomizer.Models
{
    public class Feed
    {
        public string Id { get; set; }


        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;

        public string ImagePath { get; set; } = Constants.DefaultImageRelativePath;
        public string Description { get; set; } = "string.Empty";

        // 这个属性用于标记是否已经编辑过并且已经保存
        public bool IsEdited { get; set; } = false;

        public Feed()
        {
            Id = Guid.NewGuid().ToString();
        }
    }
}
