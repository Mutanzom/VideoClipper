namespace VideoClipper.Core;

public static class OutputNaming
{
    /// <summary>
    /// "&lt;video&gt;_clip01.mp4"; if that is taken, "&lt;video&gt;_clip01 (2).mp4" and so on.
    /// Existing files are never overwritten.
    /// </summary>
    /// <param name="exists">Injected for tests; defaults to <see cref="File.Exists"/>.</param>
    public static string ClipPath(string folder, string inputPath, int clipNumber, string extension, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var stem = $"{Path.GetFileNameWithoutExtension(inputPath)}_clip{clipNumber:00}";
        var path = Path.Combine(folder, stem + extension);
        for (int n = 2; exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        }
        return path;
    }
}
