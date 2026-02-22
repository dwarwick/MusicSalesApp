using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Pages;

public partial class CreatorDashboardModel : BlazorBase, IDisposable
{
    protected bool _loading = true;
    protected string _errorMessage = string.Empty;
    private bool _hasLoadedData;
    private bool _disposed;

    protected DateTime _startDate = DateTime.Now.AddDays(-30);
    protected DateTime _endDate = DateTime.Now;
    protected DateTime _maxStartDate = DateTime.Now;
    protected StreamInterval _selectedInterval = StreamInterval.Day;
    protected string _selectedIntervalStr = "Day";
    protected List<StreamDataPoint> _chartData = new();

    protected List<IntervalOption> _intervalOptions = new()
    {
        new IntervalOption { Label = "Hour" },
        new IntervalOption { Label = "Day" },
        new IntervalOption { Label = "Week" },
        new IntervalOption { Label = "Month" },
        new IntervalOption { Label = "Year" }
    };

    protected string _chartDateFormat => _selectedInterval switch
    {
        StreamInterval.Hour => "MMM dd HH:mm",
        StreamInterval.Day => "MMM dd",
        StreamInterval.Week => "MMM dd",
        StreamInterval.Month => "MMM yyyy",
        StreamInterval.Year => "yyyy",
        _ => "MMM dd"
    };

    protected Syncfusion.Blazor.Charts.IntervalType _chartIntervalType => _selectedInterval switch
    {
        StreamInterval.Hour => Syncfusion.Blazor.Charts.IntervalType.Hours,
        StreamInterval.Day => Syncfusion.Blazor.Charts.IntervalType.Days,
        StreamInterval.Week => Syncfusion.Blazor.Charts.IntervalType.Days,
        StreamInterval.Month => Syncfusion.Blazor.Charts.IntervalType.Months,
        StreamInterval.Year => Syncfusion.Blazor.Charts.IntervalType.Years,
        _ => Syncfusion.Blazor.Charts.IntervalType.Days
    };

    private int? _creatorId;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated != true)
                {
                    _errorMessage = "You must be logged in to view the dashboard.";
                    return;
                }

                var appUser = await UserManager.GetUserAsync(user);
                if (appUser == null)
                {
                    _errorMessage = "Unable to load user information.";
                    return;
                }

                _creatorId = await CreatorService.GetCreatorIdForUserAsync(appUser.Id);
                if (_creatorId == null)
                {
                    _errorMessage = "You must be an active creator to view the dashboard.";
                    return;
                }

                await LoadChartData();

                // Subscribe to real-time stream updates
                StreamCountHubClient.OnStreamCountReceived += HandleStreamCountReceived;
                await StreamCountHubClient.StartAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Error loading dashboard: {ex.Message}";
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task LoadChartData()
    {
        if (_creatorId == null) return;

        // Validate date range
        if (_startDate >= _endDate)
        {
            _errorMessage = "Start date must be before end date.";
            return;
        }

        _errorMessage = string.Empty;

        // Convert local times to UTC for the query
        var startUtc = _startDate.ToUniversalTime();
        var endUtc = _endDate.ToUniversalTime();

        _chartData = await DashboardService.GetStreamDataAsync(_creatorId.Value, startUtc, endUtc, _selectedInterval);

        // Convert UTC data points back to local time for display
        foreach (var point in _chartData)
        {
            point.PeriodStart = point.PeriodStart.ToLocalTime();
        }
    }

    protected async Task OnStartDateChanged(Syncfusion.Blazor.Calendars.ChangedEventArgs<DateTime> args)
    {
        _startDate = args.Value;
        _maxStartDate = _endDate;

        if (_startDate >= _endDate)
        {
            _errorMessage = "Start date must be before end date.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        await LoadChartData();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnEndDateChanged(Syncfusion.Blazor.Calendars.ChangedEventArgs<DateTime> args)
    {
        _endDate = args.Value;
        _maxStartDate = _endDate;

        if (_startDate >= _endDate)
        {
            _errorMessage = "Start date must be before end date.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        await LoadChartData();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnIntervalChanged(string value)
    {
        if (Enum.TryParse<StreamInterval>(value, out var interval))
        {
            _selectedInterval = interval;
            _selectedIntervalStr = value;
        }

        await LoadChartData();
        await InvokeAsync(StateHasChanged);
    }

    private async void HandleStreamCountReceived(int songMetadataId, int newCount)
    {
        // Reload chart data when a new stream is recorded
        if (_creatorId != null)
        {
            try
            {
                // Update end date to now to include the latest data
                _endDate = DateTime.Now;
                _maxStartDate = _endDate;
                await LoadChartData();
                await InvokeAsync(StateHasChanged);
            }
            catch
            {
                // Ignore errors during real-time updates
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StreamCountHubClient.OnStreamCountReceived -= HandleStreamCountReceived;
            _disposed = true;
        }
    }
}

/// <summary>
/// Represents an interval option for the dropdown.
/// </summary>
public class IntervalOption
{
    public string Label { get; set; } = string.Empty;
}
