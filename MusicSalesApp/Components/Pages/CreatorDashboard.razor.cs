using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using Microsoft.JSInterop;

namespace MusicSalesApp.Components.Pages;

public partial class CreatorDashboardModel : BlazorBase, IDisposable
{
    protected bool _loading = true;
    protected string _errorMessage = string.Empty;
    private bool _hasLoadedData;
    private bool _disposed;

    // These will be set from the user's browser timezone in OnAfterRenderAsync
    protected DateTime _startDate;
    protected DateTime _endDate;
    protected DateTime _maxStartDate;
    protected DateTime _maxEndDate;
    protected StreamInterval _selectedInterval = StreamInterval.Day;
    protected string _selectedIntervalStr = "Day";
    protected List<StreamDataPoint> _chartData = new();
    protected string _userTimeZoneDisplayName = string.Empty;

    private TimeZoneInfo _userTimeZone = TimeZoneInfo.Utc;

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

    /// <summary>
    /// Gets the current time in the user's timezone.
    /// </summary>
    private DateTime GetUserNow() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _userTimeZone);

    /// <summary>
    /// Converts a user's local time to UTC using the detected browser timezone.
    /// </summary>
    private DateTime ConvertUserLocalToUtc(DateTime userLocal)
    {
        var unspecified = DateTime.SpecifyKind(userLocal, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, _userTimeZone);
    }

    /// <summary>
    /// Converts a UTC time to the user's local timezone.
    /// </summary>
    private DateTime ConvertUtcToUserLocal(DateTime utc)
    {
        var utcTime = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utcTime, _userTimeZone);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                // Detect user's browser timezone via JS interop
                await DetectUserTimezone();

                // Now set default dates using user's actual timezone
                var userNow = GetUserNow();
                _startDate = userNow.AddDays(-30);
                _endDate = userNow;
                _maxStartDate = userNow;
                _maxEndDate = userNow;

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

    private async Task DetectUserTimezone()
    {
        try
        {
            // Get the IANA timezone name from the browser (e.g., "Australia/Sydney", "America/New_York")
            var ianaTimeZone = await JS.InvokeAsync<string>("eval",
                "Intl.DateTimeFormat().resolvedOptions().timeZone");

            if (!string.IsNullOrEmpty(ianaTimeZone))
            {
                _userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone);
                _userTimeZoneDisplayName = ianaTimeZone;
            }
        }
        catch
        {
            // Fallback to UTC if timezone detection fails
            _userTimeZone = TimeZoneInfo.Utc;
            _userTimeZoneDisplayName = "UTC";
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

        // Convert user's local times to UTC for the query
        var startUtc = ConvertUserLocalToUtc(_startDate);
        var endUtc = ConvertUserLocalToUtc(_endDate);

        _chartData = await DashboardService.GetStreamDataAsync(_creatorId.Value, startUtc, endUtc, _selectedInterval);

        // Convert UTC data points to the user's timezone for display
        foreach (var point in _chartData)
        {
            point.PeriodStart = ConvertUtcToUserLocal(point.PeriodStart);
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
                // Update end date to user's current time to include the latest data
                var userNow = GetUserNow();
                _endDate = userNow;
                _maxStartDate = userNow;
                _maxEndDate = userNow;
                await LoadChartData();
                await InvokeAsync(StateHasChanged);
            }
            catch (ObjectDisposedException)
            {
                // Component was disposed during the async update - safe to ignore
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
