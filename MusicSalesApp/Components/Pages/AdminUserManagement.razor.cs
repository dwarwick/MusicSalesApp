using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using Microsoft.EntityFrameworkCore;
using Syncfusion.Blazor.Grids;

#nullable enable

namespace MusicSalesApp.Components.Pages;

public class AdminUserManagementModel : BlazorBase
{
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
    protected List<string> _editSelectedRoles = new();
    protected List<string> _availableRoles = new();
    protected List<string> _validationErrors = new();
    protected bool _isSaving = false;
    private bool _hasLoadedData = false;

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

        _users = users.Select(u => new UserViewModel
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
            Roles = string.Join(", ", userRoles
                .Where(ur => ur.UserId == u.Id)
                .Join(roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
                .Where(r => r != null))
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
        _editSelectedRoles = user.Roles.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
        _validationErrors.Clear();
        _showEditModal = true;
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

            await context.SaveChangesAsync();

            // Update local model
            _editingUser.Email = _editEmail;
            _editingUser.EmailConfirmed = _editEmailConfirmed;
            _editingUser.PhoneNumber = _editPhoneNumber;
            _editingUser.PhoneNumberConfirmed = _editPhoneNumberConfirmed;
            _editingUser.LockoutEnabled = _editLockoutEnabled;
            _editingUser.LockoutEnd = _editLockoutEnd;
            _editingUser.IsSuspended = _editIsSuspended;
            _editingUser.SuspendedAt = _editIsSuspended ? DateTime.UtcNow : null;
            _editingUser.Roles = string.Join(", ", _editSelectedRoles);

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
    }
}
