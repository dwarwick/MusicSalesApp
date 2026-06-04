#nullable enable

using System.Globalization;

namespace MusicSalesApp.Common.Helpers;

public static class CurrencyDisplayHelper
{
    public static string FormatCurrency(decimal amount, CultureInfo? culture = null)
        => amount.ToString("C", culture ?? CultureInfo.CurrentCulture);

    public static string FormatCurrencyText(string? priceText, decimal fallbackAmount, CultureInfo? culture = null)
        => FormatCurrencyText(priceText, fallbackAmount.ToString(CultureInfo.InvariantCulture), culture);

    public static string FormatCurrencyText(string? priceText, string? fallbackPriceText, CultureInfo? culture = null)
    {
        var price = string.IsNullOrWhiteSpace(priceText) ? fallbackPriceText?.Trim() : priceText.Trim();
        if (string.IsNullOrWhiteSpace(price))
        {
            return string.Empty;
        }

        if (ContainsCurrencyMarker(price))
        {
            return price;
        }

        return TryParseAmount(price, culture, out var amount)
            ? FormatCurrency(amount, culture)
            : price;
    }

    private static bool ContainsCurrencyMarker(string price)
        => price.Any(character =>
            char.GetUnicodeCategory(character) == UnicodeCategory.CurrencySymbol ||
            char.IsLetter(character));

    private static bool TryParseAmount(string price, CultureInfo? culture, out decimal amount)
    {
        var displayCulture = culture ?? CultureInfo.CurrentCulture;
        var styles = NumberStyles.Number | NumberStyles.AllowCurrencySymbol;

        if (price.Contains('.'))
        {
            return decimal.TryParse(price, styles, CultureInfo.InvariantCulture, out amount) ||
                decimal.TryParse(price, styles, displayCulture, out amount);
        }

        return decimal.TryParse(price, styles, displayCulture, out amount) ||
            decimal.TryParse(price, styles, CultureInfo.InvariantCulture, out amount);
    }
}