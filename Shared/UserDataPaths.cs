namespace ScanArchive.Integration;

// Keep application data outside redirected AppData views used by packaged launchers.
public static class UserDataPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scan-archive");
    public static string Secretary => Path.Combine(Root, "Secretary");
}
