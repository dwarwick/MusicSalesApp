namespace MusicSalesApp.Common.Helpers;

public static class MediaTransferValidator
{
    public static void RequireComplete(string fileName, long declaredBytes, long receivedBytes)
    {
        if (declaredBytes < 0 || receivedBytes != declaredBytes)
        {
            throw new InvalidDataException(
                $"'{fileName}' was incomplete. Expected {declaredBytes} bytes but received {receivedBytes}.");
        }
    }
}
