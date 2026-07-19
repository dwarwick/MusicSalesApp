using Microsoft.AspNetCore.Authorization;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Admin;

namespace MusicSalesApp.Tests.Components;

[TestFixture]
public class MediaIntegrityAuthorizationTests
{
    [Test]
    public void Page_RequiresManageAllCreatorSongsPermission()
    {
        var attribute = typeof(MediaIntegrity)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.That(attribute.Policy, Is.EqualTo(Permissions.ManageAllCreatorSongs));
    }

    [Test]
    public void AdminCanSelectAllThreeExplicitModes()
        => Assert.That(Enum.GetValues<MediaAuditMode>(), Is.EquivalentTo(new[]
        {
            MediaAuditMode.ReportOnly,
            MediaAuditMode.RepairSafeMetadata,
            MediaAuditMode.QuarantineConfirmedFailures
        }));
}
