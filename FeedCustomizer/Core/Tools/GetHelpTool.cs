using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace FeedCustomizer.Core.Tools
{
    public static class GetHelpTool
    {
        public static async Task<StorageFile?> GetHelpFileAsync()
        {
            var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
            string LanguageTag = resourceLoader.GetString("LanguageTag");

            try
            {
                string filePath = Path.Combine(AppDataPaths.HelpDocFolder, LanguageTag + ".html");
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                return file;
            }
            catch (Exception)
            {
                try
                {
                    string filePath = Path.Combine(AppDataPaths.HelpDocFolder, "en-US.html");
                    return await StorageFile.GetFileFromPathAsync(filePath);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }
}
