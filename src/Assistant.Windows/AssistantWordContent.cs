using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;

namespace CodexDashboard;
internal static class AssistantWordContent
{
    internal const string Kind = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    internal const string Note = "仅提取文档正文文字；不含页眉页脚、批注、图片、公式识别或版式，表格以文本顺序呈现。";
    internal static string? Read(byte[] content, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(content, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) return null;
            if (entry.Length > 8 * 1024 * 1024) throw new WeChatMediaException("document_xml_too_large");
            using var input = entry.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
            var text = new StringBuilder();
            int bodyDepth = -1, textDepth = -1;
            bool bodyFound = false;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool word = reader.NamespaceURI is "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                    or "http://purl.oclc.org/ooxml/wordprocessingml/main";
                if (word && reader.LocalName == "body" && reader.NodeType == XmlNodeType.Element)
                { bodyDepth = reader.Depth; bodyFound = true; }
                if (bodyDepth < 0) continue;
                if (word && reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "t" && !reader.IsEmptyElement) textDepth = reader.Depth;
                    else if (reader.LocalName is "tab") text.Append('\t');
                    else if (reader.LocalName is "br" or "cr") text.Append('\n');
                }
                if (textDepth >= 0 && reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
                    text.Append(reader.Value);
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Depth == textDepth) textDepth = -1;
                    if (word && reader.LocalName == "p") text.Append('\n');
                    if (reader.Depth == bodyDepth) bodyDepth = -1;
                }
            }
            if (!bodyFound) throw new WeChatMediaException("document_body_missing");
            return text.ToString();
        }
        catch (InvalidDataException) { throw new WeChatMediaException("invalid_document_archive"); }
        catch (XmlException) { throw new WeChatMediaException("invalid_document_xml"); }
    }
}
