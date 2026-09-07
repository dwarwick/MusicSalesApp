using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

/// <summary>
/// The validator is the only thing standing between a creator and a listener's inbox, so both
/// directions matter equally: a leak that gets through is a broken privacy promise, and a false
/// rejection makes the feature unusable for someone writing an ordinary thank-you.
/// </summary>
[TestFixture]
public class ArtistMessageContentPolicyTests
{
    // Written as code points: as literals these are invisible in the source, so a later edit
    // could delete one and the test would still look right while proving nothing.
    private static readonly string ZeroWidthSpace = ((char)0x200B).ToString();
    private static readonly string SoftHyphen = ((char)0x00AD).ToString();
    private static readonly string Nul = ((char)0x00).ToString();

    // ---------------------------------------------------------------- accepted

    [TestCase("Thanks so much for following me and listening. It means a lot!")]
    [TestCase("Thanks for the support!")]
    [TestCase("Glad you're enjoying the record - more on the way soon.")]
    [TestCase("Welcome aboard. Hope you like what comes next.")]
    [TestCase("Cheers for the follow, genuinely appreciated.")]
    [TestCase("You made my day. Thank you!")]
    [TestCase("Thanks. me too - that one is my favourite.")]
    [TestCase("Love it. Its great to have you here.")]
    [TestCase("Track 3 took 2 years and about 40 takes.")]
    [TestCase("Thank you for the 100 streams!")]
    public void TryValidate_AcceptsAnOrdinaryThankYou(string text)
    {
        var accepted = ArtistMessageContentPolicy.TryValidate(text, out var normalized, out var reason);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True, $"Rejected with: {reason}");
            Assert.That(normalized, Is.Not.Empty);
            Assert.That(reason, Is.Empty);
        });
    }

    // The two cases that made the domain pattern require a tight dot. Both are natural prose
    // whose second word happens to be a real TLD, and both were rejected as links before.
    [Test]
    public void TryValidate_DoesNotTreatASentenceBoundaryAsADomain()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ArtistMessageContentPolicy.TryValidate("Thanks. me too!", out _, out _), Is.True);
            Assert.That(ArtistMessageContentPolicy.TryValidate("Nice. co-writing is fun.", out _, out _), Is.True);
        });
    }

    // ---------------------------------------------------------------- email addresses

    [TestCase("Thanks! dave@gmail.com")]
    [TestCase("reach out: hello@my-band.co.uk")]
    [TestCase("Write to me at dave dot warwick at proton")]
    [TestCase("dave (at) mailbox (dot) net")]
    [TestCase("dave AT mailbox DOT net")]
    [TestCase("dave at mailbox d0t net")]
    public void TryValidate_RejectsAnEmailAddress(string text)
    {
        AssertRejected(text);
    }

    [Test]
    public void TryValidate_RejectsAnEmailSplitByInvisibleCharacters()
    {
        // The standard evasion: the address reads normally on screen but no naive matcher sees
        // it, because a zero-width space sits inside the domain.
        var split = $"thanks dave{ZeroWidthSpace}@g{ZeroWidthSpace}mail{SoftHyphen}.com";

        AssertRejected(split);
    }

    // ---------------------------------------------------------------- links

    [TestCase("Check https://my-band.com for more")]
    [TestCase("go to www.mysite.org")]
    [TestCase("mysite.com has the rest")]
    [TestCase("find it at mysite . com")]
    [TestCase("find it at mysite. com")]
    [TestCase("HTTP :// sneaky.example")]
    public void TryValidate_RejectsALink(string text)
    {
        AssertRejected(text);
    }

    // ---------------------------------------------------------------- phone numbers

    [TestCase("call 5551234567")]
    [TestCase("ring me on 555 123 4567")]
    [TestCase("+1 (555) 123-4567 anytime")]
    [TestCase("my number is 555-123-4567")]
    public void TryValidate_RejectsAPhoneNumber(string text)
    {
        AssertRejected(text);
    }

    [Test]
    public void TryValidate_AllowsShortNumbersThatAreNotPhoneNumbers()
    {
        // Six digits is below the shortest real subscriber number, and a creator talking about
        // stream counts or dates must not be blocked.
        Assert.That(ArtistMessageContentPolicy.TryValidate("We hit 123456 streams!", out _, out _), Is.True);
    }

    // ---------------------------------------------------------------- handles and platforms

    [TestCase("follow @daverivers for more")]
    [TestCase("im on instagram as dave")]
    [TestCase("catch me on tiktok")]
    [TestCase("support me on patreon")]
    [TestCase("send a tip via cash app")]
    [TestCase("my ko-fi is open")]
    public void TryValidate_RejectsSocialHandlesAndOtherPlatforms(string text)
    {
        AssertRejected(text);
    }

    // ---------------------------------------------------------------- solicitations

    [TestCase("email me anytime")]
    [TestCase("dm me if you want a sticker")]
    [TestCase("text me for the setlist")]
    [TestCase("contact me for merch")]
    [TestCase("find me elsewhere")]
    public void TryValidate_RejectsAnInvitationToMakeContactElsewhere(string text)
    {
        AssertRejected(text);
    }

    // ---------------------------------------------------------------- length and emptiness

    [Test]
    public void TryValidate_RejectsAMessageOverTheLimit()
    {
        var tooLong = new string('a', ArtistMessageContentPolicy.MaxLength + 1);

        var accepted = ArtistMessageContentPolicy.TryValidate(tooLong, out _, out var reason);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(reason, Does.Contain(ArtistMessageContentPolicy.MaxLength.ToString()));
        });
    }

    [Test]
    public void TryValidate_AcceptsAMessageExactlyAtTheLimit()
    {
        var exact = new string('a', ArtistMessageContentPolicy.MaxLength);

        Assert.That(ArtistMessageContentPolicy.TryValidate(exact, out _, out _), Is.True);
    }

    [Test]
    public void TryValidate_MeasuresLengthAfterNormalisation()
    {
        // Padding that normalisation removes must not count against the limit, or a message that
        // displays as being under the cap is rejected for reasons the sender cannot see.
        var padded = "  " + new string('a', ArtistMessageContentPolicy.MaxLength) + ZeroWidthSpace + "  ";

        Assert.That(ArtistMessageContentPolicy.TryValidate(padded, out var normalized, out _), Is.True);
        Assert.That(normalized, Has.Length.EqualTo(ArtistMessageContentPolicy.MaxLength));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void TryValidate_RejectsAnEmptyMessage(string text)
    {
        AssertRejected(text);
    }

    [Test]
    public void TryValidate_RejectsAMessageThatIsOnlyInvisibleCharacters()
    {
        // It renders as blank but is not string.IsNullOrWhiteSpace, so only the post-normalisation
        // emptiness check catches it.
        AssertRejected(ZeroWidthSpace + ZeroWidthSpace + SoftHyphen);
    }

    // ---------------------------------------------------------------- normalisation

    [Test]
    public void Normalize_CollapsesWhitespaceAndStripsInvisibleCharacters()
    {
        var result = ArtistMessageContentPolicy.Normalize($"  Thanks{ZeroWidthSpace}\t\tso   much \r\n ");

        Assert.That(result, Is.EqualTo("Thanks so much"));
    }

    [Test]
    public void Normalize_TurnsAControlCharacterIntoASeparatorRatherThanDeletingIt()
    {
        // Deleting it would glue the words together and let "call\0me" past the solicitation
        // check as "callme".
        var result = ArtistMessageContentPolicy.Normalize($"call{Nul}me");

        Assert.That(result, Is.EqualTo("call me"));
    }

    [Test]
    public void TryValidate_RejectsASolicitationHiddenByAControlCharacter()
    {
        AssertRejected($"call{Nul}me");
    }

    private static void AssertRejected(string text)
    {
        var accepted = ArtistMessageContentPolicy.TryValidate(text, out var normalized, out var reason);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False, $"Should have been rejected: {text}");
            Assert.That(reason, Is.Not.Empty, "A rejection must say why, so the sender can fix it.");
            Assert.That(normalized, Is.Empty);
        });
    }
}
