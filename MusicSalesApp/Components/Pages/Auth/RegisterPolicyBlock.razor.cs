using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages.Auth;

/// <summary>
/// The four agreement rows on <c>Register</c>, which the page renders twice - once on the
/// email path and once on the Google-pending path - from byte-identical markup.
///
/// <para>
/// Presentational only: every value is owned by <c>RegisterModel</c>, because
/// <c>CanRegister</c> gates the submit on the first three and the page posts all four as
/// hidden fields. This component just binds them, so each checkbox is a
/// <c>Checked</c>/<c>CheckedChanged</c> pair rather than a two-way bind on local state.
/// </para>
/// </summary>
public partial class RegisterPolicyBlockModel : BlazorBase
{
    [Parameter] public bool AcceptTermsOfUse { get; set; }
    [Parameter] public EventCallback<bool> AcceptTermsOfUseChanged { get; set; }

    [Parameter] public bool AcceptPrivacyPolicy { get; set; }
    [Parameter] public EventCallback<bool> AcceptPrivacyPolicyChanged { get; set; }

    [Parameter] public bool AcceptRefundPolicy { get; set; }
    [Parameter] public EventCallback<bool> AcceptRefundPolicyChanged { get; set; }

    [Parameter] public bool ReceiveNewSongEmails { get; set; }
    [Parameter] public EventCallback<bool> ReceiveNewSongEmailsChanged { get; set; }

    [Parameter] public EventCallback OnShowTerms { get; set; }
    [Parameter] public EventCallback OnShowPrivacy { get; set; }
    [Parameter] public EventCallback OnShowRefund { get; set; }
}
