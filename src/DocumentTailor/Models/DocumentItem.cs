using System.IO;

namespace DocumentTailor.Models;

public sealed class DocumentItem
{
    public DocumentItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var info = new FileInfo(path);
        SizeLabel = FormatSize(info.Exists ? info.Length : 0);
    }

    public string Path { get; }
    public string Name { get; }
    public string Extension { get; }
    public string SizeLabel { get; }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
