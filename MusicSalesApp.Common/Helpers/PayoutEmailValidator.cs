using System.Net.Mail;

namespace MusicSalesApp.Common.Helpers;

public static class PayoutEmailValidator
{
    public const string InvalidPayPalEmailMessage = "Please enter a valid PayPal email address.";
    public const string PayPalEmailRequiredForAffirmationMessage = "Please enter a PayPal email address or uncheck the PayPal affirmation before saving.";
    public const string PayPalAffirmationRequiredMessage = "Please confirm that you own or are authorized to use this PayPal account and that it can receive payouts.";

    public static bool IsValidPayPalEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var trimmedEmail = email.Trim();
        if (trimmedEmail.Length > 255 || trimmedEmail.Any(char.IsWhiteSpace))
        {
            return false;
        }

        try
        {
            var address = new MailAddress(trimmedEmail);
            if (!string.Equals(address.Address, trimmedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var atIndex = trimmedEmail.LastIndexOf('@');
            if (atIndex <= 0 || atIndex == trimmedEmail.Length - 1)
            {
                return false;
            }

            var domain = trimmedEmail[(atIndex + 1)..];
            return domain.Contains('.')
                   && !domain.StartsWith('.')
                   && !domain.EndsWith('.')
                   && domain.Split('.').All(part => part.Length > 0);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
