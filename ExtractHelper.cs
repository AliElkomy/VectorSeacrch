using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

namespace VectorSeacrch
{
    public static class ExtractHelper
    {
        public static string ExtractPdfText(Stream stream)
        {
            if (stream != null)
            {
                stream.Position = 0;
                using var pdf = PdfDocument.Open(stream);
                return string.Join("\n", pdf.GetPages().Select(p => p.Text));

            }
            return null;

        }

        public static string ExtractDocxText(Stream stream)
        {
            stream.Position = 0;
            using var doc = WordprocessingDocument.Open(stream, false);
            return doc.MainDocumentPart?.Document.Body?.InnerText ?? string.Empty;
        }

        public static List<string> ChunkText(string text, int wordsPerChunk)
        {
            string[] words = text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var chunks = new List<string>();

            for (int i = 0; i < words.Length; i += wordsPerChunk)
            {
                chunks.Add(string.Join(" ", words.Skip(i).Take(wordsPerChunk)));
            }

            return chunks;
        }
    }
}
