namespace SwitchboardRM;

/// <summary>
/// Version 2.0 renamed the app from "AWS RDP Launcher". On first start, copy what 1.x saved (settings, saved
/// connections, saved passwords) to the new locations. The old copies are left alone, so going back still works.
/// </summary>
internal static class Migration
{
    public static void Run()
    {
        try
        {
            string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.OldDataName);
            string newDir = AppSettings.Folder;
            if (Directory.Exists(oldDir) && !File.Exists(Path.Combine(newDir, "settings.json")))
            {
                Directory.CreateDirectory(newDir);
                int files = 0;
                foreach (var file in Directory.GetFiles(oldDir))
                {
                    string dest = Path.Combine(newDir, Path.GetFileName(file));
                    if (File.Exists(dest)) continue;
                    File.Copy(file, dest);
                    files++;
                }
                Log.Write($"Moved {files} settings file(s) from {oldDir} to {newDir}");
            }
        }
        catch (Exception ex) { Log.Write("Couldn't copy settings from the old version: " + ex.Message); }

        try
        {
            int copied = CredentialStore.CopyFromPrefix(AppInfo.OldDataName + ":");
            if (copied > 0) Log.Write($"Copied {copied} saved password(s) from the old version");
        }
        catch (Exception ex) { Log.Write("Couldn't copy saved passwords from the old version: " + ex.Message); }
    }
}
