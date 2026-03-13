using MusicSalesApp.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class TipUrlHelperTests
{
    // ==================== GetLastTipParams Tests ====================

    [Test]
    public void GetLastTipParams_NoQueryString_ReturnsNulls()
    {
        var (status, token) = TipUrlHelper.GetLastTipParams("https://example.com/music-library");

        Assert.That(status, Is.Null);
        Assert.That(token, Is.Null);
    }

    [Test]
    public void GetLastTipParams_SingleApproved_ReturnsApproved()
    {
        var url = "https://example.com/music-library?tip_status=approved&token=ORDER123&PayerID=ABC";

        var (status, token) = TipUrlHelper.GetLastTipParams(url);

        Assert.That(status, Is.EqualTo("approved"));
        Assert.That(token, Is.EqualTo("ORDER123"));
    }

    [Test]
    public void GetLastTipParams_SingleCancelled_ReturnsCancelled()
    {
        var url = "https://example.com/music-library?tip_status=cancelled&token=ORDER456";

        var (status, token) = TipUrlHelper.GetLastTipParams(url);

        Assert.That(status, Is.EqualTo("cancelled"));
        Assert.That(token, Is.EqualTo("ORDER456"));
    }

    [Test]
    public void GetLastTipParams_AccumulatedParams_CancelledThenApproved_ReturnsApproved()
    {
        // Real-world scenario: user cancelled first, then completed a second tip
        var url = "https://example.com/music-library?tip_status=cancelled&token=ORDER1&tip_status=approved&token=ORDER2&PayerID=XYZ";

        var (status, token) = TipUrlHelper.GetLastTipParams(url);

        Assert.That(status, Is.EqualTo("approved"));
        Assert.That(token, Is.EqualTo("ORDER2"));
    }

    [Test]
    public void GetLastTipParams_AccumulatedParams_ApprovedThenCancelled_ReturnsCancelled()
    {
        var url = "https://example.com/music-library?tip_status=approved&token=ORDER1&PayerID=ABC&tip_status=cancelled&token=ORDER2";

        var (status, token) = TipUrlHelper.GetLastTipParams(url);

        Assert.That(status, Is.EqualTo("cancelled"));
        Assert.That(token, Is.EqualTo("ORDER2"));
    }

    [Test]
    public void GetLastTipParams_MultipleAccumulations_ReturnsLast()
    {
        // Reproduces the exact bug from the log: 4 accumulated tip returns
        var url = "https://localhost:5162/music-library?tip_status=cancelled&token=1KB52121ND095315L" +
                  "&tip_status=cancelled&token=1KB52121ND095315L" +
                  "&tip_status=approved&token=34A807390R0933325&PayerID=YTXTXMJXBD5CG" +
                  "&tip_status=approved&token=9GS30764JP330861J&PayerID=YTXTXMJXBD5CG";

        var (status, token) = TipUrlHelper.GetLastTipParams(url);

        Assert.That(status, Is.EqualTo("approved"));
        Assert.That(token, Is.EqualTo("9GS30764JP330861J"));
    }

    [Test]
    public void GetLastTipParams_CancelledWithoutToken_ReturnsCancelledAndNullToken()
    {
        var url = "https://example.com/music-library?tip_status=cancelled";

        var (status, token) = TipUrlHelper.GetLastTipParams(url);

        Assert.That(status, Is.EqualTo("cancelled"));
        Assert.That(token, Is.Null);
    }

    [Test]
    public void GetLastTipParams_OtherParamsPresent_IgnoresThem()
    {
        var url = "https://example.com/music-library?page=1&sort=name&tip_status=approved&token=ORDER789";

        var (status, token) = TipUrlHelper.GetLastTipParams(url);

        Assert.That(status, Is.EqualTo("approved"));
        Assert.That(token, Is.EqualTo("ORDER789"));
    }

    // ==================== StripTipQueryParams Tests ====================

    [Test]
    public void StripTipQueryParams_NoQueryString_ReturnsUnchanged()
    {
        var url = "https://example.com/music-library";

        var result = TipUrlHelper.StripTipQueryParams(url);

        Assert.That(result, Is.EqualTo("https://example.com/music-library"));
    }

    [Test]
    public void StripTipQueryParams_OnlyTipParams_ReturnsBaseUrl()
    {
        var url = "https://example.com/music-library?tip_status=approved&token=ORDER123&PayerID=ABC";

        var result = TipUrlHelper.StripTipQueryParams(url);

        Assert.That(result, Is.EqualTo("https://example.com/music-library"));
    }

    [Test]
    public void StripTipQueryParams_MixedParams_KeepsNonTipParams()
    {
        var url = "https://example.com/music-library?page=1&tip_status=cancelled&token=ORDER456&sort=name";

        var result = TipUrlHelper.StripTipQueryParams(url);

        Assert.That(result, Is.EqualTo("https://example.com/music-library?page=1&sort=name"));
    }

    [Test]
    public void StripTipQueryParams_AccumulatedTipParams_RemovesAll()
    {
        var url = "https://example.com/music-library?tip_status=cancelled&token=ORDER1" +
                  "&tip_status=approved&token=ORDER2&PayerID=XYZ";

        var result = TipUrlHelper.StripTipQueryParams(url);

        Assert.That(result, Is.EqualTo("https://example.com/music-library"));
    }

    [Test]
    public void StripTipQueryParams_NoTipParams_ReturnsUnchanged()
    {
        var url = "https://example.com/music-library?page=2&sort=date";

        var result = TipUrlHelper.StripTipQueryParams(url);

        Assert.That(result, Is.EqualTo("https://example.com/music-library?page=2&sort=date"));
    }

    [Test]
    public void StripTipQueryParams_CaseInsensitiveKeys()
    {
        var url = "https://example.com/music-library?TIP_STATUS=approved&Token=ORDER1&payerid=ABC";

        var result = TipUrlHelper.StripTipQueryParams(url);

        Assert.That(result, Is.EqualTo("https://example.com/music-library"));
    }
}
