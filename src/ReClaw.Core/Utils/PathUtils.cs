using System;
using System.IO;

namespace ReClaw.Core.Utils
{
    public static class PathUtils
    {
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? string.Empty;
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string ToPosixRelative(string basePath, string targetPath)
        {
            var baseFull = NormalizePath(basePath).Replace(Path.DirectorySeparatorChar, '/');
            var targetFull = NormalizePath(targetPath).Replace(Path.DirectorySeparatorChar, '/');
            if (targetFull.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
            {
                return targetFull.Substring(baseFull.Length).TrimStart('/');
            }
            return targetFull;
        }
    }
}
