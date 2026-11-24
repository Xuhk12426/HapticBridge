using HapticBridge.Core.Services;

namespace HapticBridge.Setup.Backups;

public class ConfigurationBackupService : IConfigurationBackupService
{
    private readonly string _backupRoot;
    internal static string DefaultBackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HapticBridge", "backups");

    public ConfigurationBackupService(string? backupRoot = null)
    {
        _backupRoot = backupRoot ?? DefaultBackupRoot;
    }

    public string CreateBackup(string componentName, string sourceFilePath)
    {
        if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            return string.Empty;
        }

        try
        {
            string dest = CreateBackupPath(componentName, sourceFilePath);
            File.Copy(sourceFilePath, dest, overwrite: false);
            return dest;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal string SaveSnapshot(string componentName, string fileName, string content)
    {
        string path = CreateBackupPath(componentName, fileName);
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        writer.Write(content);
        return path;
    }

    private string CreateBackupPath(string componentName, string sourceFilePath)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_backupRoot, componentName)).FullName;
        string timestamp = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        string name = Path.GetFileNameWithoutExtension(sourceFilePath);
        return Path.Combine(folder, $"{name}_{timestamp}{Path.GetExtension(sourceFilePath)}.bak");
    }

    public bool RestoreBackup(string backupFilePath, string targetDestinationPath)
    {
        if (!File.Exists(backupFilePath)) return false;

        try
        {
            var dir = Path.GetDirectoryName(targetDestinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(backupFilePath, targetDestinationPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<string> ListBackups(string componentName)
    {
        string compFolder = Path.Combine(_backupRoot, componentName);
        if (!Directory.Exists(compFolder)) return Array.Empty<string>();

        try
        {
            return Directory.GetFiles(compFolder, "*.bak")
                .OrderByDescending(f => new FileInfo(f).CreationTime)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
