namespace Arrow.DataAccess.Tools;

/// <summary>Free disk space, for the 2 GB check of P07 §2 step 5 and <c>arrow doctor</c>.</summary>
public static class DiskSpace
{
    /// <summary>
    /// Free bytes on the drive that holds <paramref name="path"/>, looking at the nearest parent folder that exists
    /// (ARROW_HOME may not be created yet). Null when it cannot be read.
    /// </summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            while (dir is { Exists: false, Parent: not null })
            {
                dir = dir.Parent;
            }

            return new DriveInfo(dir.FullName).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
