using InvoiceExceptions.Application.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace InvoiceExceptions.Infrastructure.Documents;

/// <summary>
/// Reads the text layer of a PDF with PdfPig, page by page, in reading order.
/// No OCR and no rendering: what is not text in the file is not returned.
/// </summary>
public sealed class PdfTextSource : IDocumentTextSource
{
    public IReadOnlyList<PageText>? ReadPages(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = PdfDocument.Open(path);
            return [.. document.GetPages().Select(page => new PageText(page.Number, ContentOrderTextExtractor.GetText(page)))];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // An unreadable or malformed file is an expected input here, and "no text available" is a valid answer.
            return null;
        }
    }
}
