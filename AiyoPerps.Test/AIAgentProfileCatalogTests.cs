using AiyoPerps.Services;
using System.Globalization;
using Xunit;

namespace AiyoPerps.Test;

public sealed class AIAgentProfileCatalogTests
{
    [Fact]
    public void BuildDefaultPrompt_EnglishUi_ReturnsEnglishTemplateOnly()
    {
        var previousUi = CultureInfo.CurrentUICulture;
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            var culture = CultureInfo.GetCultureInfo("en");
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;

            var prompt = AIAgentProfileCatalog.BuildDefaultPrompt("Codex");

            Assert.Contains("You are an AI agent working with the AiyoPerps MCP server.", prompt);
            Assert.DoesNotContain("你是一個透過 AiyoPerps MCP server 執行工作的 AI agent。", prompt);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void BuildDefaultPrompt_ChineseUi_ReturnsChineseTemplateOnly()
    {
        var previousUi = CultureInfo.CurrentUICulture;
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            var culture = CultureInfo.GetCultureInfo("zh-TW");
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;

            var prompt = AIAgentProfileCatalog.BuildDefaultPrompt("Codex");

            Assert.Contains("你是一個透過 AiyoPerps MCP server 執行工作的 AI agent。", prompt);
            Assert.DoesNotContain("You are an AI agent working with the AiyoPerps MCP server.", prompt);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
