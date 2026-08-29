namespace MusicSalesApp.Helpers;

/// <summary>
/// Prepares a percentage for <c>SfProgressBar</c>.
///
/// <para>
/// Syncfusion derives a rounding precision from the value it is given and calls
/// <see cref="Math.Round(double, int)"/> with it. Hand it a raw quotient like
/// <c>49.058803773584904</c> and that precision runs past the 15 digits
/// <see cref="Math.Round(double, int)"/> accepts, so the component throws
/// <see cref="ArgumentOutOfRangeException"/> <em>while rendering</em>.
/// </para>
///
/// <para>
/// A render-time throw in Blazor Server does not just fail the component - it tears down the
/// circuit. Every later JS interop call then fails with "Cannot send data if the connection is not
/// in the 'Connected' State", and the page freezes on whatever it last managed to draw. That is far
/// worse than a wrong number, because a frozen progress bar is indistinguishable from a stalled job:
/// during the August 2026 production rollout it showed "0 packaged" while 127 songs were already
/// done, and "343 of 19106" while the backup had passed 8,500. The obvious reaction to that display
/// is to cancel and restart a run that is perfectly healthy.
/// </para>
///
/// <para>
/// So every percentage bound to a progress bar goes through here. NaN and infinity are folded to
/// zero as well: the callers all guard their own division, but <see cref="Math.Clamp(double, double, double)"/>
/// passes NaN straight through, and NaN reaching the component is the same class of failure.
/// </para>
/// </summary>
public static class ProgressBarValue
{
    /// <summary>
    /// How much precision survives. A progress bar is a few hundred pixels wide, so a tenth of a
    /// percent is already finer than it can draw - the point is to bound the digit count, not to
    /// preserve the quotient.
    /// </summary>
    private const int DisplayDecimals = 1;

    /// <summary>
    /// Clamps <paramref name="percent"/> to 0-100 and rounds it to something a progress bar can
    /// safely format.
    /// </summary>
    public static double ForDisplay(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
        {
            return 0;
        }

        return Math.Round(Math.Clamp(percent, 0, 100), DisplayDecimals);
    }
}
