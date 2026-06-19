using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Components;

[TestFixture]
public class CreatorFunnelAnalyticsTests
{
    [Test]
    public async Task CreatorActivation_FiresPrimaryConversionAndSecondaryFunnelEvent()
    {
        var js = new RecordingJsRuntime();
        var adminNotificationService = CreateAdminNotificationServiceMock();
        var model = new CreatorSettingsModel();
        var user = new ApplicationUser { Id = 7, Email = "creator@example.com", UserName = "creator@example.com" };

        ConfigureCreatorSettingsModel(model, js, adminNotificationService.Object, user);
        SetMember(model, "_isActiveCreator", true);
        SetMember(model, "_creatorId", 42);

        await InvokeTask(model, "ShowCreatorActivatedDialog");

        Assert.That(js.Invocations.Any(i =>
            i.Identifier == GoogleAdsTrackingConfigKeys.TrackFunnelEventFunctionName &&
            i.Arguments.Count >= 1 &&
            Equals(i.Arguments[0], FunnelAnalyticsEvents.CreatorActivated)), Is.True);
        Assert.That(js.Invocations.Any(i =>
            i.Identifier == GoogleAdsTrackingConfigKeys.TrackConversionFunctionName &&
            i.Arguments.Count == 2 &&
            Equals(i.Arguments[0], "AW-TEST/creator-label") &&
            Equals(i.Arguments[1], $"{GoogleAdsTrackingConfigKeys.CreatorSignupTransactionIdPrefix}42")), Is.True);

        adminNotificationService.Verify(x => x.RecordUserHistoryAsync(
            user.Id,
            user.Email,
            UserHistoryEventTypes.CreatorActivated,
            It.IsAny<string>(),
            null,
            null), Times.Once);
    }

    [Test]
    public async Task CreatorSignupStarted_FiresSecondaryEventOnly()
    {
        var js = new RecordingJsRuntime();
        var adminNotificationService = CreateAdminNotificationServiceMock();
        var model = new CreatorSettingsModel();
        var user = new ApplicationUser { Id = 7, Email = "creator@example.com", UserName = "creator@example.com" };

        ConfigureCreatorSettingsModel(model, js, adminNotificationService.Object, user);

        await InvokeTask(model, "TrackCreatorSignupStartedAsync", FunnelAnalyticsLabels.CreatorSignupTaxFormPending);

        Assert.That(js.Invocations.Any(i =>
            i.Identifier == GoogleAdsTrackingConfigKeys.TrackFunnelEventFunctionName &&
            i.Arguments.Count >= 1 &&
            Equals(i.Arguments[0], FunnelAnalyticsEvents.CreatorSignupStarted)), Is.True);
        Assert.That(js.Invocations.Any(i => i.Identifier == GoogleAdsTrackingConfigKeys.TrackConversionFunctionName), Is.False);

        adminNotificationService.Verify(x => x.RecordUserHistoryAsync(
            user.Id,
            user.Email,
            UserHistoryEventTypes.CreatorSignupStarted,
            It.IsAny<string>(),
            null,
            null), Times.Once);
    }

    [Test]
    public async Task TaxFormLoaded_FiresSecondaryEventOnly()
    {
        var js = new RecordingJsRuntime();
        var adminNotificationService = CreateAdminNotificationServiceMock();
        var model = new SubmitTaxFormModel();
        var user = new ApplicationUser { Id = 8, Email = "tax@example.com", UserName = "tax@example.com" };

        ConfigureTaxFormModel(model, js, adminNotificationService.Object, user);

        await InvokeTask(model, "TrackTaxFormLoadedAsync");

        Assert.That(js.Invocations.Any(i =>
            i.Identifier == GoogleAdsTrackingConfigKeys.TrackFunnelEventFunctionName &&
            i.Arguments.Count >= 1 &&
            Equals(i.Arguments[0], FunnelAnalyticsEvents.CreatorTaxFormLoaded)), Is.True);
        Assert.That(js.Invocations.Any(i => i.Identifier == GoogleAdsTrackingConfigKeys.TrackConversionFunctionName), Is.False);

        adminNotificationService.Verify(x => x.RecordUserHistoryAsync(
            user.Id,
            user.Email,
            UserHistoryEventTypes.CreatorTaxFormLoaded,
            It.IsAny<string>(),
            null,
            null), Times.Once);
    }

    [Test]
    public async Task TaxFormReturned_FiresSecondaryEventOnlyAndReturnsToCreatorSettings()
    {
        var js = new RecordingJsRuntime();
        var navigationManager = new TestNavigationManager();
        var adminNotificationService = CreateAdminNotificationServiceMock();
        var model = new SubmitTaxFormModel();
        var user = new ApplicationUser { Id = 8, Email = "tax@example.com", UserName = "tax@example.com" };

        ConfigureTaxFormModel(model, js, adminNotificationService.Object, user, navigationManager);

        await model.OnTaxFormComplete("Completed");

        Assert.That(js.Invocations.Any(i =>
            i.Identifier == GoogleAdsTrackingConfigKeys.TrackFunnelEventFunctionName &&
            i.Arguments.Count >= 1 &&
            Equals(i.Arguments[0], FunnelAnalyticsEvents.CreatorTaxFormCompletedOrReturned)), Is.True);
        Assert.That(js.Invocations.Any(i => i.Identifier == GoogleAdsTrackingConfigKeys.TrackConversionFunctionName), Is.False);
        Assert.That(navigationManager.LastUri, Is.EqualTo(AppPageRoutes.CreatorSettings));
        Assert.That(navigationManager.LastOptions.ForceLoad, Is.True);

        adminNotificationService.Verify(x => x.RecordUserHistoryAsync(
            user.Id,
            user.Email,
            UserHistoryEventTypes.CreatorTaxFormCompletedOrReturned,
            It.IsAny<string>(),
            null,
            null), Times.Once);
    }

    private static void ConfigureCreatorSettingsModel(
        CreatorSettingsModel model,
        RecordingJsRuntime js,
        IAdminNotificationService adminNotificationService,
        ApplicationUser user)
    {
        SetMember(model, "JS", js);
        SetMember(model, "NavigationManager", new TestNavigationManager());
        SetMember(model, "LoggerFactory", LoggerFactory.Create(_ => { }));
        SetMember(model, "Configuration", CreateTrackingConfiguration());
        SetMember(model, "HttpContextAccessor", CreateHttpContextAccessor());
        SetMember(model, "AdminNotificationService", adminNotificationService);
        SetMember(model, "_currentUser", user);
        SetMember(model, "_userEmail", user.Email);
    }

    private static void ConfigureTaxFormModel(
        SubmitTaxFormModel model,
        RecordingJsRuntime js,
        IAdminNotificationService adminNotificationService,
        ApplicationUser user,
        TestNavigationManager navigationManager = null)
    {
        SetMember(model, "JS", js);
        SetMember(model, "NavigationManager", navigationManager ?? new TestNavigationManager());
        SetMember(model, "LoggerFactory", LoggerFactory.Create(_ => { }));
        SetMember(model, "AdminNotificationService", adminNotificationService);
        SetMember(model, "_currentUser", user);
    }

    private static IConfiguration CreateTrackingConfiguration()
    {
        var values = new Dictionary<string, string>
        {
            [GoogleAdsTrackingConfigKeys.Enabled] = bool.TrueString,
            [GoogleAdsTrackingConfigKeys.TagId] = "AW-TEST",
            [GoogleAdsTrackingConfigKeys.CreatorSignupConversionLabel] = "creator-label",
            [$"{GoogleAdsTrackingConfigKeys.EnabledHosts}:0"] = "streamtunes.net"
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static IHttpContextAccessor CreateHttpContextAccessor()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString("streamtunes.net");
        return new HttpContextAccessor { HttpContext = httpContext };
    }

    private static Mock<IAdminNotificationService> CreateAdminNotificationServiceMock()
    {
        var mock = new Mock<IAdminNotificationService>();
        mock.Setup(x => x.RecordUserHistoryAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static Task InvokeTask(object target, string methodName, params object[] arguments)
    {
        var method = FindMethod(target.GetType(), methodName);
        Assert.That(method, Is.Not.Null);

        var result = method!.Invoke(target, arguments);
        return result as Task ?? Task.CompletedTask;
    }

    private static MethodInfo FindMethod(Type type, string methodName)
    {
        while (type != null)
        {
            var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method != null)
            {
                return method;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static void SetMember(object target, string memberName, object value)
    {
        var type = target.GetType();
        while (type != null)
        {
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (property != null)
            {
                property.SetValue(target, value);
                return;
            }

            type = type.BaseType;
        }

        Assert.Fail($"Could not find member {memberName}.");
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public List<JsInvocation> Invocations { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object[] args)
        {
            Invocations.Add(new JsInvocation(identifier, args?.ToList() ?? new List<object>()));
            return new ValueTask<TValue>(default(TValue));
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object[] args)
        {
            Invocations.Add(new JsInvocation(identifier, args?.ToList() ?? new List<object>()));
            return new ValueTask<TValue>(default(TValue));
        }
    }

    private sealed record JsInvocation(string Identifier, List<object> Arguments);

    private sealed class TestNavigationManager : NavigationManager
    {
        public string LastUri { get; private set; } = string.Empty;
        public NavigationOptions LastOptions { get; private set; }

        public TestNavigationManager()
        {
            Initialize("https://streamtunes.net/", "https://streamtunes.net/");
        }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            LastUri = uri;
            LastOptions = options;
        }
    }
}
