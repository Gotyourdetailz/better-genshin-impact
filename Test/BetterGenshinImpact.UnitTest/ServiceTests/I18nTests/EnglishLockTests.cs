using BetterGenshinImpact.Service.I18n;
using BetterGenshinImpact.View.Converters;

namespace BetterGenshinImpact.UnitTest.ServiceTests.I18nTests;

public class EnglishLockTests
{
    [Fact]
    public void I18nService_ShouldAlwaysRemainLockedToEnglish()
    {
        Assert.Equal("en", I18nService.DefaultLanguage);
        I18nService.Instance.ChangeLanguage("zh-Hans");
        Assert.Equal("en", CultureInfoNameToKVPConverter.GetDisplayName("zh-Hans") == "English" ? "en" : "");
        Assert.Equal("English", CultureInfoNameToKVPConverter.GetDisplayName("zh-Hans"));
    }

    [Fact]
    public void I18nService_Translate_ShouldTranslateCompositeStringsAndPunctuation()
    {
        var input = "（设置已保存，待重启验证）";
        var result = I18nService.Instance.Translate(input);
        Assert.DoesNotContain("设置", result);
    }
}
