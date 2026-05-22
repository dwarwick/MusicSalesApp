namespace MusicSalesApp.Services;

public interface IUnverifiedUserCleanupService
{
    Task<int> DeleteStaleUnverifiedUsersAsync();
}