using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
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

    [Microsoft.AspNetCore.Components.Inject]
    protected ICreatorEmailService CreatorEmailService { get; set; } = default!;

    [Microsoft.AspNetCore.Components.Inject]
    protected IEmailService EmailService { get; set; } = default!;

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
    protected TaxFormStatus? _originalTaxFormStatus;
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

        _users = users.Select(u => 
        {
            var creator = creators.FirstOrDefault(c => c.UserId == u.Id);
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
                Roles = string.Join(RolesDelimiter, userRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
                    .Where(r => r != null)),
                // Creator status fields
                IsCreator = creator != null,
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
        _editTheme = user.Theme ?? "Light";
        _editSelectedRoles = user.Roles.Split(RolesDelimiter, StringSplitOptions.RemoveEmptyEntries).ToList();
        _selectedRoleToAdd = null;
        _validationErrors.Clear();

        // Creator status fields
        _editPayPalOnboardingStatus = user.PayPalOnboardingStatus;
        _editTaxFormStatus = user.TaxFormStatus;
        _originalTaxFormStatus = user.TaxFormStatus; // Store the original status to detect changes
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
            user.SuspendedAt = _editIsSuspended ? DateTime.UtcNow : null;
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

            // Track if tax status changed for email notification
            var taxStatusChanged = false;
            var previousTaxStatus = _originalTaxFormStatus?.ToString() ?? "NotStarted";
            var newTaxStatus = _editTaxFormStatus?.ToString() ?? "NotStarted";

            // Update creator status if user is a creator
            if (_editingUser.IsCreator && _editingUser.CreatorId.HasValue)
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
                        taxStatusChanged = true;
                        creator.TaxFormStatus = _editTaxFormStatus.Value;
                        statusChanged = true;
                        
                        // Handle status change to Completed - run onboarding workflow
                        if (_editTaxFormStatus.Value == TaxFormStatus.Completed)
                        {
                            creator.TaxFormCompletedAt ??= DateTime.UtcNow;
                            
                            // If PayPal onboarding is also complete, activate the creator
                            if (creator.OnboardingStatus == CreatorOnboardingStatus.Completed)
                            {
                                creator.IsActive = true;
                                creator.OnboardedAt ??= DateTime.UtcNow;
                                _editCreatorIsActive = true;
                                
                                // Add Creator role if not present
                                var creatorRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == Roles.Creator);
                                if (creatorRole != null)
                                {
                                    var hasCreatorRole = await context.UserRoles
                                        .AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == creatorRole.Id);
                                    if (!hasCreatorRole)
                                    {
                                        context.UserRoles.Add(new IdentityUserRole<int>
                                        {
                                            UserId = user.Id,
                                            RoleId = creatorRole.Id
                                        });
                                    }
                                }
                            }
                        }
                        // Handle status change from Completed to another status - deactivate creator
                        else if (_originalTaxFormStatus == TaxFormStatus.Completed)
                        {
                            creator.IsActive = false;
                            _editCreatorIsActive = false;
                        }
                    }
                    
                    if (creator.IsActive != _editCreatorIsActive)
                    {
                        creator.IsActive = _editCreatorIsActive;
                        statusChanged = true;
                    }
                    
                    if (statusChanged)
                    {
                        creator.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            await context.SaveChangesAsync();

            // Send email notification if tax status changed
            if (taxStatusChanged && !string.IsNullOrWhiteSpace(_editEmail))
            {
                var baseUrl = EmailService.GetAppBaseUrl();
                await CreatorEmailService.SendTaxStatusChangedEmailAsync(
                    _editEmail, 
                    baseUrl, 
                    previousTaxStatus, 
                    newTaxStatus);
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
            _editingUser.Theme = _editTheme;
            _editingUser.Roles = string.Join(RolesDelimiter, _editSelectedRoles);

            // Update creator status in local model
            if (_editingUser.IsCreator)
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
        public string Roles { get; set; } = string.Empty;

        // Creator status fields
        public bool IsCreator { get; set; }
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
