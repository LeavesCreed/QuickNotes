using QuickNotes.Application;

namespace QuickNotes.Infrastructure.Backup;

public sealed class DriveInfoDiskSpaceProvider : IDiskSpaceProvider
{
    public long GetAvailableBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath)
            : fullPath;
        var root = Path.GetPathRoot(directory ?? string.Empty);
        if (string.IsNullOrEmpty(root))
        {
            throw new RestoreException(
                "space",
                RestoreErrorCodes.SpaceUnavailable,
                "The volume for the path could not be determined.");
        }

        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new RestoreException(
                "space",
                RestoreErrorCodes.SpaceUnavailable,
                "Available space could not be determined.",
                exception);
        }
    }
}
