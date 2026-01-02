namespace MusicSalesApp.Common.Helpers;

public static class Permissions
{
    public const string ManageUsers = "ManageUsers";
    public const string ValidatedUser = "ValidatedUser";
    public const string NonValidatedUser = "NonValidatedUser";
    public const string UploadFiles = "UploadFiles";
    public const string UseHangfire = "UseHangfire";
    public const string ManageSongs = "ManageSongs"; // Permission to manage song metadata
    public const string ManageOwnSongs = "ManageOwnSongs"; // Permission to manage only own songs (for sellers)
}
