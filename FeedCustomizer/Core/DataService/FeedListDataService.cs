using FeedCustomizer.Models;
using System.Collections.Generic;

namespace FeedCustomizer.Core.DataService
{
    public static class FeedListDataService
    {
        public static List<Feed> FeedList { get; set; } = [];

        public static List<Feed> DeleteFeedList { get; set; } = [];

        public static bool IsEnable { get; set; } = false;

        public static bool CanAppltFeeds { get; set; } = false;
    }
}
