namespace Arrow.DataAccess.Tools;

/// <summary>Windows settings that <c>arrow doctor</c> reports (P08 §2.6).</summary>
public static class WindowsSettings
{
    /// <summary>
    /// <c>LongPathsEnabled</c> of the file system: true when paths over 260 characters work. Null when this is not Windows or the
    /// value cannot be read. Maven and Gradle copies of a project may need long paths.
    /// </summary>
    public static bool? LongPathsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue("LongPathsEnabled") is int value ? value != 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
