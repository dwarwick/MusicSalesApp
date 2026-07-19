using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;

#nullable enable

namespace MusicSalesApp.Common.Helpers;

public static class MediaFileNameRules
{
    public const int MaxTitleLength = 200;
    private const string TitlePattern = @"[\p{L}\p{Nd}]+(?:[ -][\p{L}\p{Nd}]+)*(?: \([\p{L}\p{Nd}]+(?:[ -][\p{L}\p{Nd}]+)*\))?";
    private const string FileNameBasePattern = @"[\p{L}\p{Nd}]+(?:[ _-][\p{L}\p{Nd}]+)*(?: \([\p{L}\p{Nd}]+(?:[ _-][\p{L}\p{Nd}]+)*\))?";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Options = RegexOptions.Compiled |
                                         RegexOptions.CultureInvariant |
                                         RegexOptions.IgnoreCase |
                                         RegexOptions.NonBacktracking;

    public static readonly Regex TitleRegex = new($@"\A{TitlePattern}\z", Options, MatchTimeout);

    public static readonly Regex FileNameBaseRegex = new(
        $@"\A{FileNameBasePattern}\z", Options, MatchTimeout);

    public static readonly Regex AudioFileNameRegex = new(
        $@"\A{FileNameBasePattern}\.(?:{BuildExtensionAlternation(MusicFileExtensions.ValidAudioExtensions)})\z",
        Options,
        MatchTimeout);

    public static readonly Regex ImageFileNameRegex = new(
        $@"\A{FileNameBasePattern}\.(?:{BuildExtensionAlternation(MusicFileExtensions.ValidCoverArtExtensions)})\z",
        Options,
        MatchTimeout);

    public static bool IsValidTitle(string? title)
        => !string.IsNullOrWhiteSpace(title) &&
           title.Length <= MaxTitleLength &&
           TitleRegex.IsMatch(title);

    public static bool IsValidAudioFileName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName) &&
           AudioFileNameRegex.IsMatch(fileName) &&
           IsValidFileNameBase(Path.GetFileNameWithoutExtension(fileName));

    public static bool IsValidImageFileName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName) &&
           ImageFileNameRegex.IsMatch(fileName) &&
           IsValidFileNameBase(Path.GetFileNameWithoutExtension(fileName));

    public static bool IsValidFileNameBase(string? baseName)
        => !string.IsNullOrWhiteSpace(baseName) &&
           baseName.Length <= MaxTitleLength &&
           FileNameBaseRegex.IsMatch(baseName);

    public static string ToSongTitleFromBaseName(string baseName)
        => baseName?.Replace('_', ' ') ?? string.Empty;

    public static IReadOnlyList<string> GetTitleValidationErrors(string? title)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add("the title is blank");
            return errors;
        }

        if (title.Length > MaxTitleLength)
        {
            errors.Add($"it is {title.Length} characters long; the maximum is {MaxTitleLength}");
        }

        var invalidCharacters = title.EnumerateRunes()
            .Where(rune => !IsAllowedTitleRune(rune))
            .Distinct()
            .Select(FormatRune)
            .ToList();
        if (invalidCharacters.Count > 0)
        {
            errors.Add($"invalid character{(invalidCharacters.Count == 1 ? "" : "s")}: {string.Join(", ", invalidCharacters)}");
        }

        if (title.StartsWith(' ') || title.StartsWith('-')
            || title.EndsWith(' ') || title.EndsWith('-'))
        {
            errors.Add("spaces and hyphens must be between letters or numbers, not at an edge");
        }

        if (title.Contains("  ", StringComparison.Ordinal)
            || title.Contains("--", StringComparison.Ordinal)
            || title.Contains(" -", StringComparison.Ordinal)
            || title.Contains("- ", StringComparison.Ordinal))
        {
            errors.Add("spaces and hyphens cannot be adjacent or repeated");
        }

        if (title.Contains('(') || title.Contains(')'))
        {
            var suffixStart = title.LastIndexOf(" (", StringComparison.Ordinal);
            var validSuffix = suffixStart > 0
                && title.EndsWith(')')
                && title.Count(character => character == '(') == 1
                && title.Count(character => character == ')') == 1
                && suffixStart + 2 < title.Length - 1;
            if (!validSuffix)
            {
                errors.Add("parentheses are allowed only once as a nonempty final suffix preceded by a space, such as Song Name (Remix)");
            }
        }

        if (errors.Count == 0 && !TitleRegex.IsMatch(title))
        {
            errors.Add("each word must contain letters or numbers separated by one space or one hyphen");
        }

        return errors;
    }

    public static string GetTitleValidationMessage(string? title, bool wasAcceptedUnderPreviousRules)
    {
        var prefix = wasAcceptedUnderPreviousRules
            ? "This existing title was accepted under previous rules, but it is invalid under the current rules"
            : "The song title is invalid";
        return $"{prefix}: {string.Join("; ", GetTitleValidationErrors(title))}.";
    }

    public static IReadOnlyList<string> GetFileNameValidationErrors(string? fileName)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            errors.Add("the filename is blank");
            return errors;
        }

        var name = Path.GetFileName(fileName);
        var periodCount = name.Count(character => character == '.');
        if (periodCount != 1)
        {
            errors.Add($"exactly one period is required immediately before the extension; found {periodCount}");
        }

        var invalidCharacters = name.EnumerateRunes()
            .Where(rune => !IsAllowedFileNameRune(rune))
            .Distinct()
            .Select(FormatRune)
            .ToList();
        if (invalidCharacters.Count > 0)
        {
            errors.Add($"invalid character{(invalidCharacters.Count == 1 ? "" : "s")}: {string.Join(", ", invalidCharacters)}");
        }

        var extension = Path.GetExtension(name);
        var supportedExtension = MusicFileExtensions.ValidAudioExtensions
            .Concat(MusicFileExtensions.ValidCoverArtExtensions)
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
        if (!supportedExtension)
        {
            var extensionDisplay = string.IsNullOrWhiteSpace(extension) ? "none" : $"'{extension}'";
            errors.Add($"unsupported extension {extensionDisplay}; allowed audio extensions are "
                + $"{string.Join(", ", MusicFileExtensions.ValidAudioExtensions)} and allowed image extensions are "
                + string.Join(", ", MusicFileExtensions.ValidCoverArtExtensions));
        }

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (baseName.Length > MaxTitleLength)
        {
            errors.Add($"the filename before the extension is {baseName.Length} characters long; the maximum is {MaxTitleLength}");
        }

        if (string.IsNullOrEmpty(baseName) || !IsLetterOrDecimalDigit(baseName.EnumerateRunes().First()))
        {
            errors.Add("the filename must begin with a letter or number");
        }

        if (baseName.StartsWith(' ') || baseName.StartsWith('-') || baseName.StartsWith('_')
            || baseName.EndsWith(' ') || baseName.EndsWith('-') || baseName.EndsWith('_'))
        {
            errors.Add("spaces, hyphens, and underscores must be between letters or numbers, not at an edge");
        }

        if (Regex.IsMatch(baseName, "[ _-]{2}", RegexOptions.CultureInvariant, MatchTimeout))
        {
            errors.Add("spaces, hyphens, and underscores cannot be adjacent or repeated");
        }

        if (baseName.Contains('(') || baseName.Contains(')'))
        {
            var suffixStart = baseName.LastIndexOf(" (", StringComparison.Ordinal);
            var validSuffix = suffixStart > 0
                && baseName.EndsWith(')')
                && baseName.Count(character => character == '(') == 1
                && baseName.Count(character => character == ')') == 1
                && suffixStart + 2 < baseName.Length - 1;
            if (!validSuffix)
            {
                errors.Add("parentheses are allowed only once as a nonempty final suffix preceded by a space, such as Song Name (Remix).wav");
            }
        }

        if (errors.Count == 0 && !FileNameBaseRegex.IsMatch(baseName))
        {
            errors.Add("each word must contain letters or numbers separated by one space, hyphen, or underscore");
        }

        return errors;
    }

    public static string GetFileNameValidationMessage(string? fileName)
        => $"'{fileName}': {string.Join("; ", GetFileNameValidationErrors(fileName))}.";

    private static string BuildExtensionAlternation(IEnumerable<string> extensions)
        => string.Join('|', extensions.Select(extension => Regex.Escape(extension.TrimStart('.'))));

    private static bool IsAllowedTitleRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            || rune.Value is ' ' or '-' or '(' or ')';
    }

    private static bool IsAllowedFileNameRune(Rune rune)
        => IsLetterOrDecimalDigit(rune)
           || rune.Value is ' ' or '_' or '-' or '(' or ')' or '.';

    private static bool IsLetterOrDecimalDigit(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber;
    }

    private static string FormatRune(Rune rune)
    {
        var display = rune.Value switch
        {
            '_' => "underscore '_'",
            '\'' => "apostrophe \"'\"",
            '@' => "at sign '@'",
            '&' => "ampersand '&'",
            '.' => "period '.'",
            _ => $"'{rune}'"
        };
        return $"{display} (U+{rune.Value:X4})";
    }
}
