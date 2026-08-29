using System;
using System.Globalization;
using MusicSalesApp.Helpers;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Helpers;

/// <summary>
/// The guard between a raw quotient and Syncfusion's progress bar.
///
/// <para>
/// These are crash-prevention tests rather than formatting ones. A render-time throw inside a Blazor
/// Server component tears down the circuit, so the page stops updating entirely and shows whatever
/// it last drew - which reads as a stalled job rather than a broken page.
/// </para>
/// </summary>
[TestFixture]
public class ProgressBarValueTests
{
    /// <summary>
    /// The exact value that took down the admin page during the production rollout.
    /// </summary>
    [Test]
    public void TheValueThatCrashedTheCircuitIsReducedToOneDecimal()
    {
        Assert.That(ProgressBarValue.ForDisplay(49.058803773584904d), Is.EqualTo(49.1d));
    }

    /// <summary>
    /// The property that actually matters, stated directly.
    ///
    /// <para>
    /// Syncfusion works out how many digits to round to from the value itself, and throws when that
    /// count leaves the 0-15 range <see cref="Math.Round(double, int)"/> permits. Bounding the
    /// decimals is what keeps it in range, so this asserts the bound over the whole domain rather
    /// than trusting one example.
    /// </para>
    /// </summary>
    [Test]
    public void NoOutputEverCarriesMoreThanOneDecimalPlace()
    {
        // Deliberately awkward denominators - these are what produce long repeating quotients.
        foreach (var total in new[] { 3, 7, 9, 11, 23, 530, 19106 })
        {
            for (var done = 0; done <= total; done += Math.Max(1, total / 97))
            {
                var value = ProgressBarValue.ForDisplay(done * 100d / total);
                var text = value.ToString("R", CultureInfo.InvariantCulture);
                var dot = text.IndexOf('.');
                var decimals = dot < 0 ? 0 : text.Length - dot - 1;

                Assert.That(
                    decimals,
                    Is.LessThanOrEqualTo(1),
                    $"{done}/{total} produced {text}, which would let the component compute a "
                    + "rounding precision past what Math.Round accepts");
            }
        }
    }

    [Test]
    public void ValuesAreClampedToTheBarsRange()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProgressBarValue.ForDisplay(-12d), Is.Zero);
            Assert.That(ProgressBarValue.ForDisplay(140d), Is.EqualTo(100d));
            Assert.That(ProgressBarValue.ForDisplay(0d), Is.Zero);
            Assert.That(ProgressBarValue.ForDisplay(100d), Is.EqualTo(100d));
        });
    }

    /// <summary>
    /// NaN and infinity fold to zero rather than reaching the component.
    ///
    /// <para>
    /// Every caller guards its own division, so this should be unreachable - but
    /// <see cref="Math.Clamp(double, double, double)"/> returns NaN for NaN rather than clamping it,
    /// so the previous code would have passed one straight through. Defence in depth on the same
    /// failure mode this class exists to prevent.
    /// </para>
    /// </summary>
    [Test]
    public void NonFiniteValuesBecomeZeroRatherThanReachingTheComponent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProgressBarValue.ForDisplay(double.NaN), Is.Zero);
            Assert.That(ProgressBarValue.ForDisplay(double.PositiveInfinity), Is.Zero);
            Assert.That(ProgressBarValue.ForDisplay(double.NegativeInfinity), Is.Zero);
            Assert.That(ProgressBarValue.ForDisplay(0d / 0d), Is.Zero);
        });
    }

    [Test]
    public void AnExactPercentageIsLeftAlone()
    {
        // Rounding must not perturb the common case - half of 530 really is 50%.
        Assert.That(ProgressBarValue.ForDisplay(265 * 100d / 530), Is.EqualTo(50d));
    }
}
