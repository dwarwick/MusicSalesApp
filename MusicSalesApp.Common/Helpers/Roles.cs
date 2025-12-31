namespace MusicSalesApp.Common.Helpers;

public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string NonValidatedUser = "NonValidatedUser"; // new role for newly registered users
    public const string Seller = "Seller"; // role for users who can sell music
}
