using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace DocumentTailor.Services;

public sealed class PdfMergeService
{
    public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (inputPaths.Count < 2)
                throw new InvalidOperationException("請至少加入 2 份 PDF。");

            using var output = new PdfDocument();
            for (var index = 0; index < inputPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inputPath = inputPaths[index];
                progress?.Report($"正在合併 {index + 1} / {inputPaths.Count}：{Path.GetFileName(inputPath)}");
                using var input = PdfReader.Open(inputPath, PdfDocumentOpenMode.Import);
                for (var page = 0; page < input.PageCount; page++)
                    output.AddPage(input.Pages[page]);
            }
            output.Save(outputPath);
        }, cancellationToken);
}
