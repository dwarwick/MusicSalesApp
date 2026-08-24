using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using System.Reflection;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class ManagePersonasTests : BUnitTestBase
{
    private const int CreatorId = 12;
    private const int UserId = 5;

    [Test]
    public void ManagePersonas_RendersOneCardPerIdentity_NotAGridRow()
    {
        // The SfGrid this replaced split one persona across Actions / Image / Name / Bio /
        // Website / Songs / Status columns. Everything on this list is the same person, so it
        // is a card, and everything about that person has to be on it.
        SetupCreatorWithPersonas(
            new CreatorPersona
            {
                Id = 1,
                CreatorId = CreatorId,
                Name = "Nightshift Radio",
                Bio = "Slow, late and mostly instrumental.",
                WebsiteUrl = "https://nightshift.example.com",
                IsEnabled = true,
            },
            new CreatorPersona { Id = 2, CreatorId = CreatorId, Name = "Warwick", IsEnabled = true });

        MockCreatorPersonaService.Setup(x => x.GetPersonaSongCountsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, int> { [1] = 14, [2] = 1 });

        var cut = TestContext.Render<ManagePersonas>();
        cut.WaitForState(() => cut.FindAll(".persona-card").Count == 2, TimeSpan.FromSeconds(5));

        var first = cut.FindAll(".persona-card")[0];
        var second = cut.FindAll(".persona-card")[1];

        Assert.Multiple(() =>
        {
            Assert.That(first.TextContent, Does.Contain("Nightshift Radio"));
            Assert.That(first.TextContent, Does.Contain("Slow, late and mostly instrumental."));
            Assert.That(first.TextContent, Does.Contain("14 songs"));
            Assert.That(first.QuerySelector(".persona-card-site")?.GetAttribute("href"),
                Is.EqualTo("https://nightshift.example.com"));

            // Singular, and the absences say what they are rather than printing a dash.
            Assert.That(second.TextContent, Does.Contain("1 song"));
            Assert.That(second.TextContent, Does.Contain("No bio yet."));
            Assert.That(second.TextContent, Does.Contain("No website"));

            Assert.That(cut.Find(".persona-summary").TextContent,
                Is.EqualTo("2 personas · 15 songs linked"));
        });
    }

    [Test]
    public void ManagePersonas_NonSquareImage_SaysWhatWillHappenToIt()
    {
        // It used to be a warning-triangle badge on a 60px thumbnail with a title attribute.
        // The reader could see something was wrong and not what, or what to do.
        SetupCreatorWithPersonas(new CreatorPersona
        {
            Id = 1,
            CreatorId = CreatorId,
            Name = "Warwick",
            ImageBlobPath = "personas/1/image.png",
            // IsImageSquare is derived from the two dimensions, so a wide image is how a
            // test says "not square".
            ImageWidth = 1200,
            ImageHeight = 800,
            IsEnabled = true,
        });

        var cut = TestContext.Render<ManagePersonas>();
        cut.WaitForState(() => cut.FindAll(".persona-card").Count == 1, TimeSpan.FromSeconds(5));

        var notice = cut.Find(".persona-card .info-strip-warn").TextContent;
        Assert.Multiple(() =>
        {
            Assert.That(notice, Does.Contain("cropped when a listener sees it"));
            Assert.That(cut.FindAll(".persona-card .info-strip-warn svg"), Is.Not.Empty,
                "the warning carries an icon as well as colour");
        });
    }

    [Test]
    public void ManagePersonas_NoPersonas_OffersToCreateOne()
    {
        SetupCreatorWithPersonas();

        var cut = TestContext.Render<ManagePersonas>();
        cut.WaitForState(() => cut.Markup.Contains("No personas yet"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".persona-card"), Is.Empty);
            Assert.That(cut.FindAll(".persona-toolbar"), Is.Empty,
                "a count of zero and a summary line are noise on an empty page");
            Assert.That(cut.Find(".settings-empty-body").TextContent,
                Does.Contain("display name"), "the empty state says what listeners see instead");
        });
    }

    [Test]
    public void ManagePersonas_NotACreator_SaysSoRatherThanShowingAnEmptyList()
    {
        SetupAuthorizedUser(UserId, "testuser@test.com");
        MockAppSettingsService.Setup(x => x.GetMaxImageUploadSizeMBAsync()).ReturnsAsync(10);
        MockCreatorService.Setup(x => x.GetCreatorIdForUserAsync(UserId)).ReturnsAsync((int?)null);
        SetupRendererInfo();

        var cut = TestContext.Render<ManagePersonas>();
        cut.WaitForState(() => cut.Markup.Contains("not registered as a creator"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("No personas yet"),
                "that would be a wrong answer to \"you have no creator account\"");
            Assert.That(cut.Markup, Does.Not.Contain("Create a Persona"),
                "and the button behind it could not work");
            Assert.That(cut.Find(".settings-empty .e-btn").GetAttribute("href"),
                Is.EqualTo(AppPageRoutes.CreatorSettings),
                "a dead end still needs a way out");
        });
    }

    [TestCase(0, "No songs are linked", TestName = "ManagePersonas_Delete_NoSongs_SaysNothingChanges")]
    [TestCase(3, "3 songs", TestName = "ManagePersonas_Delete_WithSongs_SaysTheSongsSurvive")]
    public void ManagePersonas_DeleteDialog_LeadsWithWhatHappensToTheSongs(int songCount, string expected)
    {
        // What a reader is actually afraid of here is losing the music, so that answer goes
        // first and says plainly that it stays.
        SetupCreatorWithPersonas(new CreatorPersona { Id = 1, CreatorId = CreatorId, Name = "Warwick", IsEnabled = true });
        MockCreatorPersonaService.Setup(x => x.GetPersonaSongCountsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, int> { [1] = songCount });

        var cut = TestContext.Render<ManagePersonas>();
        cut.WaitForState(() => cut.FindAll(".persona-card").Count == 1, TimeSpan.FromSeconds(5));

        cut.InvokeAsync(() => InvokeShowDelete(cut.Instance)).GetAwaiter().GetResult();
        cut.Render();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain(expected));
            Assert.That(cut.Markup, Does.Contain("The persona image, bio and website are deleted"));
        });
    }

    private void SetupCreatorWithPersonas(params CreatorPersona[] personas)
    {
        SetupAuthorizedUser(UserId, "testuser@test.com");
        MockAppSettingsService.Setup(x => x.GetMaxImageUploadSizeMBAsync()).ReturnsAsync(10);
        MockCreatorService.Setup(x => x.GetCreatorIdForUserAsync(UserId)).ReturnsAsync(CreatorId);
        MockCreatorPersonaService.Setup(x => x.GetPersonasByCreatorIdAsync(CreatorId))
            .ReturnsAsync(personas.ToList());
        MockCreatorPersonaService.Setup(x => x.GetPersonaSongCountsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, int>());
        MockCreatorPersonaService
            .Setup(x => x.GetPersonaImageSasUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>()))
            .Returns("https://example.test/persona.png");
        SetupRendererInfo();
    }

    private static void InvokeShowDelete(object instance)
    {
        var personas = (List<PersonaAdminViewModel>)typeof(ManagePersonasModel)
            .GetField("_personas", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;

        var method = typeof(ManagePersonasModel)
            .GetMethod("ShowDeleteConfirmation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, "Expected ShowDeleteConfirmation to exist.");
        method!.Invoke(instance, new object[] { personas[0] });
    }
}
