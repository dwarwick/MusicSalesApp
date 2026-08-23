using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// <see cref="BlazorBase"/>'s dispatcher hop for background callbacks.
///
/// <para>
/// Every component that subscribes to a SignalR hub, a timer or a service event reaches the renderer
/// from a thread the renderer does not own. Blazor Server keeps component state on a single
/// dispatcher: <c>StateHasChanged</c> called from anywhere else throws "The current thread is not
/// associated with the Dispatcher", and the fields mutated on the way there race the renderer
/// reading them. This used to be hand-rolled in nine places, several of which got it wrong - one of
/// them shipped and showed up in production as a song grid that silently stopped repainting.
/// </para>
/// </summary>
[TestFixture]
public class BlazorBaseDispatchTests : BUnitTestBase
{
    /// <summary>
    /// The smallest thing that can prove a repaint happened: a component that counts its renders.
    /// </summary>
    public class DispatchProbe : BlazorBase
    {
        public int Renders { get; private set; }

        public int WorkRuns { get; private set; }

        public bool WorkShouldThrow { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            Renders++;
            builder.AddMarkupContent(0, $"<span id=\"renders\">{Renders}</span>");
        }

        /// <summary>Queues asynchronous work, the shape a hub handler uses.</summary>
        public void RaiseAsyncWork() => DispatchUiUpdate(async () =>
        {
            await Task.Yield();

            if (WorkShouldThrow)
            {
                throw new InvalidOperationException("The background work failed.");
            }

            WorkRuns++;
        });

        /// <summary>Queues synchronous work, the shape a stream-count handler uses.</summary>
        public void RaiseSyncWork() => DispatchUiUpdate(() => WorkRuns++);

        /// <summary>Repaints with nothing of its own to change, the shape a theme handler uses.</summary>
        public void RaiseRefresh() => DispatchUiRefresh();
    }

    private IRenderedComponent<DispatchProbe> RenderProbe()
    {
        var cut = TestContext.Render<DispatchProbe>();
        cut.WaitForState(() => cut.Instance.Renders > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    /// <summary>
    /// Raises the callback the way a hub does: on some other thread entirely.
    /// </summary>
    /// <remarks>
    /// Task.Run rather than calling it inline, because the whole point is the absence of the
    /// renderer's synchronization context. Called inline from a test the context is often present by
    /// accident, and the test passes whether or not the hop exists.
    /// </remarks>
    private static void FromABackgroundThread(Action raise) => Task.Run(raise).GetAwaiter().GetResult();

    [Test]
    public void AsyncWorkFromABackgroundThreadRunsAndRepaints()
    {
        var cut = RenderProbe();
        var rendersBefore = cut.Instance.Renders;

        FromABackgroundThread(cut.Instance.RaiseAsyncWork);

        cut.WaitForState(() => cut.Instance.Renders > rendersBefore, TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Instance.WorkRuns, Is.EqualTo(1), "The work itself must run, not just the repaint.");
            Assert.That(cut.Instance.Renders, Is.GreaterThan(rendersBefore));
        });
    }

    [Test]
    public void SyncWorkFromABackgroundThreadRunsAndRepaints()
    {
        var cut = RenderProbe();
        var rendersBefore = cut.Instance.Renders;

        FromABackgroundThread(cut.Instance.RaiseSyncWork);

        cut.WaitForState(() => cut.Instance.Renders > rendersBefore, TimeSpan.FromSeconds(5));

        Assert.That(cut.Instance.WorkRuns, Is.EqualTo(1));
    }

    [Test]
    public void ARefreshWithNoWorkStillRepaints()
    {
        var cut = RenderProbe();
        var rendersBefore = cut.Instance.Renders;

        FromABackgroundThread(cut.Instance.RaiseRefresh);

        cut.WaitForState(() => cut.Instance.Renders > rendersBefore, TimeSpan.FromSeconds(5));

        Assert.That(cut.Instance.Renders, Is.GreaterThan(rendersBefore));
    }

    [Test]
    public void AThrowingCallbackIsContainedRatherThanLostOrFatal()
    {
        // These callers are void event handlers, so the Task is discarded and an exception inside it
        // is unobserved - it would surface later, on the finalizer thread, attributed to nothing.
        // Containing it here is what keeps one bad handler from taking the circuit with it.
        var cut = RenderProbe();
        cut.Instance.WorkShouldThrow = true;

        Assert.DoesNotThrow(() => FromABackgroundThread(cut.Instance.RaiseAsyncWork));

        // And the component is still usable afterwards.
        cut.Instance.WorkShouldThrow = false;
        var rendersBefore = cut.Instance.Renders;

        FromABackgroundThread(cut.Instance.RaiseSyncWork);

        cut.WaitForState(() => cut.Instance.Renders > rendersBefore, TimeSpan.FromSeconds(5));

        Assert.That(cut.Instance.WorkRuns, Is.EqualTo(1), "The throwing attempt must not have counted.");
    }

    [Test]
    public void ACallbackArrivingAfterDisposalIsNotAnError()
    {
        // Unsubscribing in Dispose narrows this race; it cannot remove it. A push already in flight
        // when the circuit went away must land on nothing rather than throw into a discarded Task.
        var cut = RenderProbe();
        var probe = cut.Instance;

        // Disposing the context tears down the renderer and every component in it, which is the
        // closest a test gets to the circuit going away underneath a queued push. The base fixture
        // disposes again in TearDown; that is idempotent.
        TestContext.Dispose();

        Assert.DoesNotThrow(() => FromABackgroundThread(probe.RaiseSyncWork));
    }
}
