using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using Microsoft.JSInterop;
using Syncfusion.Blazor.Grids;

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
    protected string _userTimeZoneDisplayName = "UTC";

    private TimeZoneInfo _userTimeZone = TimeZoneInfo.Utc;

    // Filter pill state
    protected Dictionary<string, int> _genreItems = new();
    protected HashSet<string> _selectedGenres = new();
    protected Dictionary<string, int> _artistItems = new();
    protected HashSet<string> _selectedArtists = new();
    protected Dictionary<string, int> _songTitleItems = new();
    protected HashSet<string> _selectedSongTitles = new();

    // Payout history state
    protected List<PayoutHistoryViewModel> _payoutHistory = new();
    protected SfGrid<PayoutHistoryViewModel> _payoutGrid;

    // Tip history state
    protected List<TipHistoryViewModel> _tipHistory = new();
    protected SfGrid<TipHistoryViewModel> _tipGrid;
    protected decimal _tipsOnHoldAmount;
    protected int _tipsOnHoldCount;
    protected decimal _tipsPendingPayoutAmount;
    protected int _tipsPendingPayoutCount;
    protected decimal _tipsPaidAmount;
    protected int _tipsPaidCount;

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

    protected Syncfusion.Blazor.Theme _chartTheme =>
        ThemeService.IsDarkTheme ? Syncfusion.Blazor.Theme.Bootstrap5Dark : Syncfusion.Blazor.Theme.Bootstrap5;

    protected double _yAxisInterval
    {
        get
        {
            if (_chartData == null || _chartData.Count == 0) return 1;
            var maxCount = _chartData.Max(d => d.StreamCount);
            if (maxCount <= 10) return 1;
            if (maxCount <= 50) return 5;
            if (maxCount <= 100) return 10;
            if (maxCount <= 500) return 50;
            if (maxCount <= 1000) return 100;
            return Math.Ceiling(maxCount / 10.0);
        }
    }

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

                await LoadFilterData();
                await LoadChartData();
                await LoadPayoutHistory();
                await LoadTipHistory();

                // Subscribe to real-time stream updates
                StreamCountHubClient.OnStreamCountReceived += HandleStreamCountReceived;
                await StreamCountHubClient.StartAsync();

                // Subscribe to theme changes so the chart updates when dark/light mode is toggled
                ThemeService.OnThemeChanged += HandleThemeChanged;
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
            var ianaTimeZone = await JS.InvokeAsync<string>("dashboardHelper.getUserTimeZone");

            if (!string.IsNullOrEmpty(ianaTimeZone))
            {
                _userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone);
                _userTimeZoneDisplayName = ianaTimeZone;
            }
        }
        catch (TimeZoneNotFoundException ex)
        {
            Logger.LogWarning(ex, "Browser returned unrecognized timezone, falling back to UTC");
            _userTimeZone = TimeZoneInfo.Utc;
            _userTimeZoneDisplayName = "UTC";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to detect user timezone via JS interop, falling back to UTC");
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

        var genres = _selectedGenres.Count > 0 ? _selectedGenres : null;
        var artists = _selectedArtists.Count > 0 ? _selectedArtists : null;
        var titles = _selectedSongTitles.Count > 0 ? _selectedSongTitles : null;

        _chartData = await DashboardService.GetStreamDataAsync(_creatorId.Value, startUtc, endUtc, _selectedInterval, genres, artists, titles);

        // Convert UTC data points to the user's timezone for display
        foreach (var point in _chartData)
        {
            point.PeriodStart = ConvertUtcToUserLocal(point.PeriodStart);
        }
    }

    private async Task LoadFilterData()
    {
        if (_creatorId == null) return;

        // Convert user's local times to UTC for the query
        var startUtc = ConvertUserLocalToUtc(_startDate);
        var endUtc = ConvertUserLocalToUtc(_endDate);

        var genres = _selectedGenres.Count > 0 ? _selectedGenres : null;
        var artists = _selectedArtists.Count > 0 ? _selectedArtists : null;
        var titles = _selectedSongTitles.Count > 0 ? _selectedSongTitles : null;

        var options = await DashboardService.GetStreamFilterOptionsAsync(_creatorId.Value, startUtc, endUtc, genres, artists, titles);

        _genreItems = options.Genres;
        _artistItems = options.Artists;
        _songTitleItems = options.SongTitles;

        // Remove any selected items that are no longer available
        _selectedGenres.IntersectWith(_genreItems.Keys);
        _selectedArtists.IntersectWith(_artistItems.Keys);
        _selectedSongTitles.IntersectWith(_songTitleItems.Keys);
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

        await LoadFilterData();
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

        await LoadFilterData();
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

    protected async Task OnGenreToggled((string item, bool isChecked) args)
    {
        if (args.isChecked)
            _selectedGenres.Add(args.item);
        else
            _selectedGenres.Remove(args.item);

        await LoadFilterData();
        await LoadChartData();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnGenreCleared()
    {
        _selectedGenres.Clear();
        await LoadFilterData();
        await LoadChartData();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnArtistToggled((string item, bool isChecked) args)
    {
        if (args.isChecked)
            _selectedArtists.Add(args.item);
        else
            _selectedArtists.Remove(args.item);

        await LoadFilterData();
        await LoadChartData();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnArtistCleared()
    {
        _selectedArtists.Clear();
        await LoadFilterData();
        await LoadChartData();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnSongTitleToggled((string item, bool isChecked) args)
    {
        if (args.isChecked)
            _selectedSongTitles.Add(args.item);
        else
            _selectedSongTitles.Remove(args.item);

        await LoadFilterData();
        await LoadChartData();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnSongTitleCleared()
    {
        _selectedSongTitles.Clear();
        await LoadFilterData();
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
                await LoadFilterData();
                await LoadChartData();
                await InvokeAsync(StateHasChanged);
            }
            catch (ObjectDisposedException)
            {
                // Component was disposed during the async update - safe to ignore
            }
        }
    }

    private void HandleThemeChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private async Task LoadPayoutHistory()
    {
        if (_creatorId == null) return;

        try
        {
            var payouts = await StreamPayoutService.GetPayoutHistoryAsync(_creatorId.Value);

            _payoutHistory = payouts.Select(p => new PayoutHistoryViewModel
            {
                PaymentDate = ConvertUtcToUserLocal(p.PaymentDate),
                SongTitle = Services.DashboardService.GetEffectiveSongTitle(p.SongMetadata),
                NumberOfStreams = p.NumberOfStreams,
                GrossAmount = p.GrossAmount,
                WithheldAmount = p.WithheldAmount,
                NetAmount = p.NetAmount,
                PayPalTransactionId = p.PayPalTransactionId ?? string.Empty
            }).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading payout history for creator {CreatorId}", _creatorId);
            _payoutHistory = new();
        }
    }

    private async Task LoadTipHistory()
    {
        if (_creatorId == null) return;

        try
        {
            var tips = await TipService.GetTipsForCreatorAsync(_creatorId.Value);

            // Calculate bucket totals
            var onHold = tips.Where(t => t.Status == Models.TipStatus.Pending && t.CapturedAt != null).ToList();
            var pendingPayout = tips.Where(t => t.Status == Models.TipStatus.Cleared).ToList();
            var paid = tips.Where(t => t.Status == Models.TipStatus.Paid).ToList();

            _tipsOnHoldAmount = onHold.Sum(t => t.Amount);
            _tipsOnHoldCount = onHold.Count;
            _tipsPendingPayoutAmount = pendingPayout.Sum(t => t.Amount);
            _tipsPendingPayoutCount = pendingPayout.Count;
            _tipsPaidAmount = paid.Sum(t => t.Amount);
            _tipsPaidCount = paid.Count;

            _tipHistory = tips.Select(t => new TipHistoryViewModel
            {
                Date = ConvertUtcToUserLocal(t.CreatedAt),
                Amount = t.Amount,
                SongTitle = t.SongMetadata != null
                    ? Services.DashboardService.GetEffectiveSongTitle(t.SongMetadata)
                    : "General",
                Status = t.Status.ToString(),
                PaidDate = t.PaidAt.HasValue ? ConvertUtcToUserLocal(t.PaidAt.Value) : null
            }).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading tip history for creator {CreatorId}", _creatorId);
            _tipHistory = new();
        }
    }

    protected async Task ExportPayoutsToExcel()
    {
        if (_payoutGrid == null) return;

        try
        {
            var excelExportProperties = new ExcelExportProperties
            {
                FileName = $"PayoutHistory_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx",
                ExportType = ExportType.AllPages
            };

            await _payoutGrid.ExportToExcelAsync(excelExportProperties);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error exporting payout history to Excel");
        }
    }

    protected async Task ExportPayoutsToCsv()
    {
        if (_payoutGrid == null) return;

        try
        {
            var excelExportProperties = new ExcelExportProperties
            {
                FileName = $"PayoutHistory_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv",
                ExportType = ExportType.AllPages
            };

            await _payoutGrid.ExportToCsvAsync(excelExportProperties);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error exporting payout history to CSV");
        }
    }

    protected async Task PrintPayouts()
    {
        if (_payoutGrid == null) return;

        try
        {
            await _payoutGrid.PrintAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error printing payout history");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StreamCountHubClient.OnStreamCountReceived -= HandleStreamCountReceived;
            ThemeService.OnThemeChanged -= HandleThemeChanged;
            _disposed = true;
        }
    }
}

/// <summary>
/// View model for payout history grid rows.
/// </summary>
public class PayoutHistoryViewModel
{
    public DateTime PaymentDate { get; set; }
    public string SongTitle { get; set; } = string.Empty;
    public int NumberOfStreams { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal WithheldAmount { get; set; }
    public decimal NetAmount { get; set; }
    public string PayPalTransactionId { get; set; } = string.Empty;
}

/// <summary>
/// Represents an interval option for the dropdown.
/// </summary>
public class IntervalOption
{
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// View model for tip history grid rows.
/// </summary>
public class TipHistoryViewModel
{
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string SongTitle { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? PaidDate { get; set; }
}
