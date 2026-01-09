namespace MusicSalesApp.Common.Helpers;

public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string NonValidatedUser = "NonValidatedUser"; // new role for newly registered users
    public const string Creator = "Creator"; // role for users who can upload and share music
}
