using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The pseudonym is the whole of the anonymity guarantee, so these test the properties it has to
/// have rather than the code path that produces them.
/// </summary>
[TestFixture]
public class ArtistFollowerIdentityServiceTests
{
    private ArtistFollowerIdentityService _service;

    [SetUp]
    public void SetUp()
    {
        _service = new ArtistFollowerIdentityService(new Random(20260904));
    }

    [Test]
    public void AllocateNumber_NeverReturnsANumberAlreadyInUse()
    {
        var used = new HashSet<int>();

        for (var i = 0; i < 500; i++)
        {
            var allocated = _service.AllocateNumber(used);

            Assert.That(used.Add(allocated), Is.True, $"Allocated a duplicate number: {allocated}");
        }
    }

    [Test]
    public void AllocateNumber_StaysOutOfLowNumbersThatWouldLookLikeAUserId()
    {
        // Not cosmetic. Numbers starting at 1 would be indistinguishable from a sequence, and a
        // creator seeing "Listener #3" would reasonably read it as the third user on the platform.
        var used = new HashSet<int>();

        for (var i = 0; i < 200; i++)
        {
            Assert.That(_service.AllocateNumber(used), Is.GreaterThanOrEqualTo(1000));
        }
    }

    [Test]
    public void AllocateNumber_IsNotSequential()
    {
        // A counter would leak the order people followed in, which sits next to a visible
        // "Following Since" date and turns two coarse dates into an ordering of everyone between.
        var used = new HashSet<int>();
        var allocated = new List<int>();

        for (var i = 0; i < 50; i++)
        {
            var number = _service.AllocateNumber(used);
            used.Add(number);
            allocated.Add(number);
        }

        var ascendingSteps = allocated
            .Zip(allocated.Skip(1), (first, second) => second - first)
            .Count(step => step == 1);

        Assert.That(ascendingSteps, Is.Zero, "Numbers are being handed out in sequence.");
    }

    [Test]
    public void AllocateNumber_DoesNotDependOnTheListener()
    {
        // There is no listener parameter at all, and that is the design: a value derived from the
        // user id - even a keyed hash - stays a function of the identity it hides, so one leaked
        // key would deanonymise every follower at once and let two creators compare lists.
        var parameters = typeof(IArtistFollowerIdentityService)
            .GetMethod(nameof(IArtistFollowerIdentityService.AllocateNumber))!
            .GetParameters();

        Assert.That(parameters, Has.Length.EqualTo(1));
        Assert.That(parameters[0].Name, Does.Contain("Used"));
    }

    [Test]
    public void AllocateNumber_KeepsWorkingWhenTheFirstBandIsCrowded()
    {
        // A persona with tens of thousands of followers must not stall or throw; the allocator
        // moves up a band rather than probing a nearly-full range forever.
        var crowded = new HashSet<int>(Enumerable.Range(1_000, 99_000 - 1));

        var allocated = _service.AllocateNumber(crowded);

        Assert.Multiple(() =>
        {
            Assert.That(crowded, Does.Not.Contain(allocated));
            Assert.That(allocated, Is.GreaterThanOrEqualTo(1000));
        });
    }

    [Test]
    public void AllocateNumber_ToleratesNoNumbersHavingBeenUsed()
    {
        Assert.That(_service.AllocateNumber(new HashSet<int>()), Is.GreaterThanOrEqualTo(1000));
    }

    [Test]
    public void FormatDisplayName_RendersTheLabelTheCreatorSees()
    {
        Assert.That(_service.FormatDisplayName(4817), Is.EqualTo("Listener #4817"));
    }
}
