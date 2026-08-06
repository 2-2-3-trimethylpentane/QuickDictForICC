using System;
using System.IO;
using QuickDictForICC.Services;
using Xunit;
using Xunit.Sdk;

namespace QuickDictForICC.Tests
{
    public class MDictServiceIntegrationTests
    {
        [Fact]
        public void RealMdx_LoadsAndLooksUpMultipleEnglishWords()
        {
            string path = Environment.GetEnvironmentVariable("QUICKDICT_TEST_MDX_PATH");
            if (string.IsNullOrWhiteSpace(path))
                path = @"C:\Users\Lenovo\Downloads\简明英汉字典增强版.mdx";
            if (!File.Exists(path))
                throw new SkipException($"测试词典不存在: {path}");

            var service = new MDictService(path);
            service.Load();

            Assert.True(service.IsLoaded);
            Assert.NotNull(service.Lookup("apple"));
            Assert.NotNull(service.Lookup("computer"));
            Assert.NotNull(service.Lookup("dictionary"));
        }
    }
}
