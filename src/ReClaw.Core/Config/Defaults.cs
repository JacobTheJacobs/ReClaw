using System;

namespace ReClaw.Core.Config
{
    public static class Defaults
    {
        public static string Home => Environment.GetEnvironmentVariable("OPENCLAW_HOME") ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string BackupDir => Environment.GetEnvironmentVariable("RECLAW_BACKUP_DIR") ??
            System.IO.Path.Combine(Home, ".reclaw", "backups");

        public const int Pbkdf2Iterations = 310000;
    }
}
