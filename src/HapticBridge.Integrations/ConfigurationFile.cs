namespace HapticBridge.Integrations;

internal static class ConfigurationFile
{
    public static void Replace(string path, Action<string> write)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".hapticbridge-{Guid.NewGuid():N}.tmp");
        try
        {
            write(temporary);
            File.Replace(temporary, path, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
