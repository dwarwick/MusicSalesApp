using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using Microsoft.EntityFrameworkCore;
using Syncfusion.Blazor.Grids;

#nullable enable

namespace MusicSalesApp.Components.Pages;

public class AdminUserManagementModel : BlazorBase
{
    private const string RolesDelimiter = ", ";

    [Microsoft.AspNetCore.Components.Inject]
    protected IDbContextFactory<AppDbContext> DbContextFactory { get; set; } = default!;

    [Microsoft.AspNetCore.Components.Inject]
    protected RoleManager<IdentityRole<int>> RoleManager { get; set; } = default!;

    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected List<UserViewModel> _users = new();
    protected SfGrid<UserViewModel>? _grid;

    // Edit modal fields
    protected bool _showEditModal = false;
    protected UserViewModel? _editingUser = null;
    protected string _editEmail = string.Empty;
    protected bool _editEmailConfirmed = false;
    protected string _editPhoneNumber = string.Empty;
    protected bool _editPhoneNumberConfirmed = false;
    protected bool _editLockoutEnabled = false;
    protected DateTimeOffset? _editLockoutEnd = null;
    protected bool _editIsSuspended = false;
    protected bool _editIsSubscriptionBlocked = false;
    protected bool _editIsTipBlocked = false;
    protected string _editTheme = string.Empty;
    protected List<string> _editSelectedRoles = new();
    protected List<string> _availableRoles = new();
    protected string? _selectedRoleToAdd = null;
    protected List<string> _themeOptions = new() { "Light", "Dark" };
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;
    private bool _hasLoadedData = false;

    // Creator status edit fields
    protected CreatorOnboardingStatus? _editPayPalOnboardingStatus;
    protected TaxFormStatus? _editTaxFormStatus;
    protected bool _editCreatorIsActive;
    protected List<string> _payPalOnboardingStatusOptions = Enum.GetNames<CreatorOnboardingStatus>().ToList();
    protected List<string> _taxFormStatusOptions = Enum.GetNames<TaxFormStatus>().ToList();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadUsersAsync();
                await LoadAvailableRolesAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load users: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadUsersAsync()
    {
        await using var context = await DbContextFactory.CreateDbContextAsync();
        
        var users = await context.Users.ToListAsync();
        var userRoles = await context.UserRoles.ToListAsync();
        var roles = await context.Roles.ToListAsync();
        var creators = await context.Creators.ToListAsync();
        var subscriptions = await context.Subscriptions.ToListAsync();

        // Pre-group subscriptions by UserId for O(users) lookup instead of O(users × subscriptions)
        var latestSubByUser = subscriptions
            .GroupBy(s => s.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.CreatedAt).First());

        _users = users.Select(u => 
        {
            var creator = creators.FirstOrDefault(c => c.UserId == u.Id);
            latestSubByUser.TryGetValue(u.Id, out var latestSub);

            string subStatus;
            if (u.IsSubscriptionBlocked)
                subStatus = "Blocked";
            else if (latestSub != null && latestSub.Status == SubscriptionStatuses.Active
                     && (latestSub.EndDate == null || latestSub.EndDate > DateTime.UtcNow))
                subStatus = "Active";
            else if (latestSub != null && (latestSub.Status == SubscriptionStatuses.Cancelled
                     || latestSub.Status == SubscriptionStatuses.Suspended
                     || latestSub.Status == SubscriptionStatuses.Expired))
                subStatus = "Cancelled";
            else
                subStatus = "Not Subscribed";

            return new UserViewModel
            {
                Id = u.Id,
                UserName = u.UserName ?? string.Empty,
                Email = u.Email ?? string.Empty,
                EmailConfirmed = u.EmailConfirmed,
                PhoneNumber = u.PhoneNumber ?? string.Empty,
                PhoneNumberConfirmed = u.PhoneNumberConfirmed,
                LockoutEnd = u.LockoutEnd,
                LockoutEnabled = u.LockoutEnabled,
                AccessFailedCount = u.AccessFailedCount,
                LastVerificationEmailSent = u.LastVerificationEmailSent,
                Theme = u.Theme,
                IsSuspended = u.IsSuspended,
                SuspendedAt = u.SuspendedAt,
                IsSubscriptionBlocked = u.IsSubscriptionBlocked,
                SubscriptionBlockedAt = u.SubscriptionBlockedAt,
                IsTipBlocked = u.IsTipBlocked,
                TipBlockedAt = u.TipBlockedAt,
                SubscriptionStatus = subStatus,
                Roles = string.Join(RolesDelimiter, userRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
                    .Where(r => r != null)),
                // Creator status fields
                HasCreatorRecord = creator != null,
                CreatorId = creator?.Id,
                PayPalOnboardingStatus = creator?.OnboardingStatus,
                PayPalOnboardingStatusDisplay = creator?.OnboardingStatus.ToString() ?? "-",
                TaxFormStatus = creator?.TaxFormStatus,
                TaxFormStatusDisplay = creator?.TaxFormStatus.ToString() ?? "-",
                CreatorIsActive = creator?.IsActive ?? false,
                TaxFormCompletedAt = creator?.TaxFormCompletedAt,
                PayPalOnboardedAt = creator?.OnboardedAt
            };
        }).ToList();
    }

    protected async Task LoadAvailableRolesAsync()
    {
        await using var context = await DbContextFactory.CreateDbContextAsync();
        _availableRoles = await context.Roles.Select(r => r.Name!).ToListAsync();
    }

    protected void EditUser(UserViewModel user)
    {
        _editingUser = user;
        _editEmail = user.Email;
        _editEmailConfirmed = user.EmailConfirmed;
        _editPhoneNumber = user.PhoneNumber;
        _editPhoneNumberConfirmed = user.PhoneNumberConfirmed;
        _editLockoutEnabled = user.LockoutEnabled;
        _editLockoutEnd = user.LockoutEnd;
        _editIsSuspended = user.IsSuspended;
        _editIsSubscriptionBlocked = user.IsSubscriptionBlocked;
        _editIsTipBlocked = user.IsTipBlocked;
        _editTheme = user.Theme ?? "Light";
        _editSelectedRoles = user.Roles.Split(RolesDelimiter, StringSplitOptions.RemoveEmptyEntries).ToList();
        _selectedRoleToAdd = null;
        _validationErrors.Clear();

        // Creator status fields
        _editPayPalOnboardingStatus = user.PayPalOnboardingStatus;
        _editTaxFormStatus = user.TaxFormStatus;
        _editCreatorIsActive = user.CreatorIsActive;

        _showEditModal = true;
    }

    protected List<string> GetUnassignedRoles()
    {
        return _availableRoles.Where(r => !_editSelectedRoles.Contains(r)).ToList();
    }

    protected void OnRoleSelected(Syncfusion.Blazor.DropDowns.ChangeEventArgs<string, string> args)
    {
        if (!string.IsNullOrEmpty(args.Value) && !_editSelectedRoles.Contains(args.Value))
        {
            _editSelectedRoles.Add(args.Value);
        }
        _selectedRoleToAdd = null;
        StateHasChanged();
    }

    protected void RemoveRole(string role)
    {
        _editSelectedRoles.Remove(role);
        StateHasChanged();
    }

    protected void CancelEdit()
    {
        _showEditModal = false;
        _editingUser = null;
        _validationErrors.Clear();
    }

    protected async Task SaveEdit()
    {
        if (_editingUser == null) return;

        _validationErrors.Clear();
        _isSaving = true;

        try
        {
            // Validation
            if (string.IsNullOrWhiteSpace(_editEmail))
            {
                _validationErrors.Add("Email is required.");
            }

            if (_validationErrors.Any())
            {
                StateHasChanged();
                return;
            }

            await using var context = await DbContextFactory.CreateDbContextAsync();

            var user = await context.Users.FindAsync(_editingUser.Id);
            if (user == null)
            {
                _validationErrors.Add("User not found.");
                return;
            }

            // Update user properties
            user.Email = _editEmail;
            user.NormalizedEmail = _editEmail.ToUpperInvariant();
            user.EmailConfirmed = _editEmailConfirmed;
            user.PhoneNumber = string.IsNullOrWhiteSpace(_editPhoneNumber) ? null : _editPhoneNumber;
            user.PhoneNumberConfirmed = _editPhoneNumberConfirmed;
            user.LockoutEnabled = _editLockoutEnabled;
            user.LockoutEnd = _editLockoutEnd;
            user.IsSuspended = _editIsSuspended;
            user.SuspendedAt = _editIsSuspended ? (user.SuspendedAt ?? DateTime.UtcNow) : null;
            user.IsSubscriptionBlocked = _editIsSubscriptionBlocked;
            user.SubscriptionBlockedAt = _editIsSubscriptionBlocked ? (user.SubscriptionBlockedAt ?? DateTime.UtcNow) : null;
            user.IsTipBlocked = _editIsTipBlocked;
            user.TipBlockedAt = _editIsTipBlocked ? (user.TipBlockedAt ?? DateTime.UtcNow) : null;
            user.Theme = _editTheme;

            // Update roles
            var existingRoles = await context.UserRoles
                .Where(ur => ur.UserId == user.Id)
                .ToListAsync();
            context.UserRoles.RemoveRange(existingRoles);

            foreach (var roleName in _editSelectedRoles)
            {
                var role = await context.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
                if (role != null)
                {
                    context.UserRoles.Add(new IdentityUserRole<int>
                    {
                        UserId = user.Id,
                        RoleId = role.Id
                    });
                }
            }

            // Update creator status if user is a creator
            if (_editingUser.HasCreatorRecord && _editingUser.CreatorId.HasValue)
            {
                var creator = await context.Creators.FindAsync(_editingUser.CreatorId.Value);
                if (creator != null)
                {
                    var statusChanged = false;
                    
                    if (_editPayPalOnboardingStatus.HasValue && creator.OnboardingStatus != _editPayPalOnboardingStatus.Value)
                    {
                        creator.OnboardingStatus = _editPayPalOnboardingStatus.Value;
                        statusChanged = true;
                    }
                    
                    if (_editTaxFormStatus.HasValue && creator.TaxFormStatus != _editTaxFormStatus.Value)
                    {
                        creator.TaxFormStatus = _editTaxFormStatus.Value;
                        statusChanged = true;
                        // Set TaxFormCompletedAt when status changes to Completed
                        if (_editTaxFormStatus.Value == TaxFormStatus.Completed)
                        {
                            creator.TaxFormCompletedAt ??= DateTime.UtcNow;
                        }
                    }
                    
                    if (creator.IsActive != _editCreatorIsActive)
                    {
                        var wasActive = creator.IsActive;
                        creator.IsActive = _editCreatorIsActive;
                        statusChanged = true;

                        // Notify admin about creator status change
                        try
                        {
                            if (_editCreatorIsActive && !wasActive)
                            {
                                await AdminNotificationService.NotifyCreatorStatusGainedAsync(_editEmail);
                            }
                            else if (!_editCreatorIsActive && wasActive)
                            {
                                await AdminNotificationService.NotifyCreatorStatusLostAsync(_editEmail);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Failed to send admin notification for creator status change of {Email}", _editEmail);
                        }
                    }
                    
                    if (statusChanged)
                    {
                        creator.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            await context.SaveChangesAsync();

            // Send tip re-enabled email if admin just unblocked tipping
            if (_editingUser.IsTipBlocked && !_editIsTipBlocked)
            {
                try
                {
                    await SendTipReenabledEmailAsync(_editEmail);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to send tip re-enabled email to {Email}", _editEmail);
                }
            }

            // Update local model
            _editingUser.Email = _editEmail;
            _editingUser.EmailConfirmed = _editEmailConfirmed;
            _editingUser.PhoneNumber = _editPhoneNumber;
            _editingUser.PhoneNumberConfirmed = _editPhoneNumberConfirmed;
            _editingUser.LockoutEnabled = _editLockoutEnabled;
            _editingUser.LockoutEnd = _editLockoutEnd;
            _editingUser.IsSuspended = _editIsSuspended;
            _editingUser.SuspendedAt = _editIsSuspended ? DateTime.UtcNow : null;
            _editingUser.IsSubscriptionBlocked = _editIsSubscriptionBlocked;
            _editingUser.SubscriptionBlockedAt = _editIsSubscriptionBlocked ? DateTime.UtcNow : null;
            _editingUser.IsTipBlocked = _editIsTipBlocked;
            _editingUser.TipBlockedAt = _editIsTipBlocked ? DateTime.UtcNow : null;
            _editingUser.Theme = _editTheme;
            _editingUser.Roles = string.Join(RolesDelimiter, _editSelectedRoles);

            // Update creator status in local model
            if (_editingUser.HasCreatorRecord)
            {
                _editingUser.PayPalOnboardingStatus = _editPayPalOnboardingStatus;
                _editingUser.PayPalOnboardingStatusDisplay = _editPayPalOnboardingStatus?.ToString() ?? "-";
                _editingUser.TaxFormStatus = _editTaxFormStatus;
                _editingUser.TaxFormStatusDisplay = _editTaxFormStatus?.ToString() ?? "-";
                _editingUser.CreatorIsActive = _editCreatorIsActive;
            }

            // Close modal and refresh
            _showEditModal = false;
            await LoadUsersAsync();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            _validationErrors.Add($"Error saving changes: {ex.Message}");
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task SendTipReenabledEmailAsync(string userEmail)
    {
        var logoUrl = EmailService.GetLogoUrl();
        var baseUrl = EmailService.GetAppBaseUrl();
        var subject = "StreamTunes - Tipping Privileges Restored";

        var body = $@"
        <div style='max-width: 600px; margin: 0 auto; font-family: Arial, sans-serif;'>
            <div style='text-align: center; padding: 20px; background-color: #1a1a2e; border-radius: 8px 8px 0 0;'>
                <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
                <h1 style='color: #ffffff; margin: 10px 0 0 0; font-size: 24px;'>Tipping Privileges Restored</h1>
            </div>
            <div style='padding: 20px; background-color: #ffffff; border: 1px solid #e0e0e0; border-top: none;'>
                <p style='font-size: 16px; color: #333;'>Your tipping privileges on StreamTunes have been restored. You are now able to send tips to creators again.</p>
                <p style='font-size: 16px; color: #333;'>Please note that any future chargebacks on tips will result in your tipping privileges being permanently revoked again.</p>
                <p style='font-size: 14px; color: #999;'>StreamTunes Support: {Configuration["EmailSettings:CustomerServiceEmail"]}</p>
                <p style='color: #999; font-size: 12px;'>
                    <a href='{baseUrl}/manage-account' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
                </p>
            </div>
        </div>";

        await EmailService.SendEmailAsync(userEmail, subject, body);
    }

    protected class UserViewModel
    {
        public int Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool EmailConfirmed { get; set; }
        public string PhoneNumber { get; set; } = string.Empty;
        public bool PhoneNumberConfirmed { get; set; }
        public DateTimeOffset? LockoutEnd { get; set; }
        public bool LockoutEnabled { get; set; }
        public int AccessFailedCount { get; set; }
        public DateTime? LastVerificationEmailSent { get; set; }
        public string Theme { get; set; } = string.Empty;
        public bool IsSuspended { get; set; }
        public DateTime? SuspendedAt { get; set; }
        public bool IsSubscriptionBlocked { get; set; }
        public DateTime? SubscriptionBlockedAt { get; set; }
        public bool IsTipBlocked { get; set; }
        public DateTime? TipBlockedAt { get; set; }
        public string SubscriptionStatus { get; set; } = string.Empty;
        public string Roles { get; set; } = string.Empty;

        // Creator status fields
        public bool HasCreatorRecord { get; set; }
        public int? CreatorId { get; set; }
        public CreatorOnboardingStatus? PayPalOnboardingStatus { get; set; }
        public string PayPalOnboardingStatusDisplay { get; set; } = "-";
        public TaxFormStatus? TaxFormStatus { get; set; }
        public string TaxFormStatusDisplay { get; set; } = "-";
        public bool CreatorIsActive { get; set; }
        public DateTime? TaxFormCompletedAt { get; set; }
        public DateTime? PayPalOnboardedAt { get; set; }
    }
}
