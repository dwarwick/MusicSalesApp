#nullable enable
using MusicSalesApp.Models;

namespace MusicSalesApp.Helpers;

public static class UserTimeZoneDisplayHelper
{
    public const string UtcTimeZoneId = "UTC";

    public static string GetTimeZoneId(ApplicationUser? user)
        => string.IsNullOrWhiteSpace(user?.TimeZoneId) ? UtcTimeZoneId : user.TimeZoneId;

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static DateTime ConvertUtcToUserTime(DateTime utcDateTime, string? timeZoneId)
    {
        var utc = EnsureUtc(utcDateTime);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, ResolveTimeZone(timeZoneId));
    }

    public static string FormatDateTime(DateTime utcDateTime, string? timeZoneId)
        => ConvertUtcToUserTime(utcDateTime, timeZoneId).ToString("MMMM dd, yyyy 'at' h:mm tt");

    public static string FormatDate(DateTime utcDateTime, string? timeZoneId)
        => ConvertUtcToUserTime(utcDateTime, timeZoneId).ToString("MMMM dd, yyyy");

    public static string FormatDateTimeWithTimeZone(DateTime utcDateTime, string? timeZoneId)
    {
        var localTime = ConvertUtcToUserTime(utcDateTime, timeZoneId);
        return $"{localTime:MMMM dd, yyyy 'at' h:mm tt} {BuildTimeZoneSuffix(localTime, timeZoneId)}";
    }

    public static string GetTimeZoneDisplayLabel(string? timeZoneId, DateTime? utcReference = null)
    {
        var referenceUtc = EnsureUtc(utcReference ?? DateTime.UtcNow);
        var localTime = ConvertUtcToUserTime(referenceUtc, timeZoneId);
        return BuildTimeZoneSuffix(localTime, timeZoneId);
    }

    private static string BuildTimeZoneSuffix(DateTime localTime, string? timeZoneId)
    {
        var normalizedTimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? UtcTimeZoneId : timeZoneId;
        var offset = localTime.ToString("zzz");
        return $"({normalizedTimeZoneId}, UTC{offset})";
    }

    private static DateTime EnsureUtc(DateTime dateTime)
        => dateTime.Kind switch
        {
            DateTimeKind.Utc => dateTime,
            DateTimeKind.Local => dateTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
        };
}