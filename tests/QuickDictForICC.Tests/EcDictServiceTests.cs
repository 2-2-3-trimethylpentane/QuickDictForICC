using System;
using System.IO;
using QuickDictForICC.Services;
using Xunit;

namespace QuickDictForICC.Tests
{
    public class EcDictServiceTests
    {
        [Fact]
        public void CsvSource_CreatesDatabaseCacheAndUsesItForLookupAndSuggestions()
        {
            string directory = Path.Combine(Path.GetTempPath(), "QuickDictForICC.Tests", Guid.NewGuid().ToString("N"));
            string csvPath = Path.Combine(directory, "custom.csv");
            string databasePath = Path.ChangeExtension(csvPath, ".db");
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(csvPath,
                    "word,phonetic,definition,translation,pos,exchange,phrase,sentence,synonym\n"
                    + "apple,/'aepl/,apple definition,苹果,n,,apple pie,,fruit\n"
                    + "application,,application definition,应用,n,,,apply,,\n");

                var service = new EcDictService(csvPath);
                service.Load();

                Assert.True(service.IsLoaded);
                Assert.True(File.Exists(databasePath));
                Assert.Equal("apple", service.Lookup("APPLE").Word);
                Assert.Equal(new[] { "apple", "application" }, service.GetSuggestions("app", 10));

                var cachedService = new EcDictService(csvPath);
                cachedService.Load();

                Assert.True(cachedService.IsLoaded);
                Assert.Equal("应用", cachedService.Lookup("application").Translation);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }
    }
}
