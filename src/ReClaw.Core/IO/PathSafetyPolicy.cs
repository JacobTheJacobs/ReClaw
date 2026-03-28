using System;
using System.IO;

namespace ReClaw.Core.IO;

/// <summary>
/// Archive extraction safety policy:
/// - Only relative paths (no traversal, no absolute/UNC/drive-letter paths)
/// - Max segment length 255
/// - Max full extracted path length 1024 (after combining destination root)
/// - Case-variant collisions are rejected during extraction
/// </summary>
internal static class PathSafetyPolicy
{
    public const int MaxPathSegmentLength = 255;
    public const int MaxExtractedPathLength = 1024;

    public static void EnsureTotalPathLength(string fullPath, string label)
    {
        if (fullPath.Length > MaxExtractedPathLength)
        {
            throw new InvalidDataException($"{label} exceeds the maximum allowed path length ({MaxExtractedPathLength}): {fullPath}");
        }
    }
}
