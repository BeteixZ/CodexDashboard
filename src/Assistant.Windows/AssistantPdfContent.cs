using System;
using System.Text;
using System.Threading;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace CodexDashboard;
internal static class AssistantPdfContent
{
    internal const string Note = "仅提取PDF文字层并标记页码；扫描图片未做OCR，表格、多栏和公式顺序可能不完整。";
    internal static (string Text, string Note) Read(byte[] content, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = PdfDocument.Open(content);
            if (document.NumberOfPages > 1000) throw new WeChatMediaException("pdf_too_many_pages");
            var text = new StringBuilder();
            bool hasText = false;
            for (int number = 1; number <= document.NumberOfPages; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.GetPage(number);
                string body = ContentOrderTextExtractor.GetText(page);
                cancellationToken.ThrowIfCancellationRequested();
                hasText |= !string.IsNullOrWhiteSpace(body);
                if (text.Length + body.Length > 8 * 1024 * 1024) throw new WeChatMediaException("pdf_text_too_large");
                text.Append("[第 ").Append(page.Number).Append(" 页]\n").Append(body).Append('\n');
            }
            return hasText ? (text.ToString(), Note) : ("", "这个PDF没有可提取的文字层；可能是扫描件或空白文档，当前未做OCR。可以改发文字或PNG/JPEG截图进行图片分析。");
        }
        catch (PdfDocumentEncryptedException) { throw new WeChatMediaException("pdf_password_required"); }
        catch (PdfDocumentFormatException) { throw new WeChatMediaException("invalid_pdf_document"); }
    }
}
