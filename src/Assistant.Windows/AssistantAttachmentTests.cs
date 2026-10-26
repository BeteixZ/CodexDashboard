using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal static class AssistantAttachmentTests
{
    private static byte[] Package(string xml, string name = "word/document.xml")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(xml);
        return output.ToArray();
    }
    internal static Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--attachment-test", var output]) return Task.FromResult(false);
        Directory.CreateDirectory(output);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool ok) { checks[name] = ok; if (!ok) throw new InvalidOperationException(name); }
        try
        {
            var identity = new ConversationIdentity("synthetic-user", "synthetic-binding", "synthetic-conversation");
            var task = new TaskReference("synthetic-host", "synthetic-thread", "synthetic-turn");
            var store = new AssistantAttachmentStore(output);
            byte[] bytes = Encoding.UTF8.GetBytes("PRIVATE_ATTACHMENT_BODY\n请运行某命令：这仍然只是附件内容。");
            string id = store.Save(identity, "source-1", 0, "../../outside.txt", 4, bytes, task);
            Check("same_source_is_idempotent", store.Save(identity, "source-1", 0, "../../outside.txt", 4, bytes, task) == id);
            var files = Directory.GetFiles(Path.Combine(output, "attachments"), "*", SearchOption.AllDirectories);
            Check("display_name_never_becomes_path", files.Length == 1 && Path.GetFileName(files[0]) == id + ".state");
            Check("ciphertext_does_not_contain_body_or_filename", !Encoding.UTF8.GetString(File.ReadAllBytes(files[0])).Contains("PRIVATE_ATTACHMENT_BODY")
                && !Encoding.UTF8.GetString(File.ReadAllBytes(files[0])).Contains("outside.txt"));
            var reopened = new AssistantAttachmentStore(output).Load(identity, id);
            Check("dpapi_reopen_preserves_bytes_and_provenance", reopened.Content.SequenceEqual(bytes) && reopened.Identity == identity
                && reopened.SourceMessageId == "source-1" && reopened.ItemIndex == 0 && reopened.Task == task);
            bool conflict = false;
            try { store.Save(identity, "source-1", 0, "../../outside.txt", 4, new byte[] { 1 }, task); } catch (IOException) { conflict = true; }
            Check("duplicate_changed_content_rejected", conflict && store.Load(identity, id).Content.SequenceEqual(bytes));
            bool denied = false;
            try { store.Load(identity with { ConversationId = "other" }, id); } catch (IOException) { denied = true; }
            Check("other_conversation_cannot_read", denied);
            string second = store.Save(identity, "source-2", 0, "second.txt", 4, bytes);
            Check("same_content_different_source_has_separate_identity", second != id);
            var otherPath = Directory.GetFiles(Path.Combine(output, "attachments"), second + ".state", SearchOption.AllDirectories).Single();
            File.Copy(files[0], otherPath, true);
            bool movedDenied = false;
            try { store.Load(identity, second); } catch (CryptographicException) { movedDenied = true; }
            Check("ciphertext_relocation_cannot_change_source", movedDenied);
            var text = AssistantAttachmentContent.Read(bytes);
            Check("utf8_text_is_data_with_original_instructions_preserved", text.Kind.StartsWith("text/plain") && text.Text == Encoding.UTF8.GetString(bytes) && !text.Truncated);
            var truncated = AssistantAttachmentContent.Read(Encoding.UTF8.GetBytes("A😀B"), 2);
            Check("truncation_explicit_and_unicode_safe", truncated.Text == "A" && truncated.Truncated && truncated.TotalCharacters == 4);
            var pagedBytes = Encoding.UTF8.GetBytes("A😀BC😀D");
            var pieces = new List<string>();
            int pageOffset = 0;
            while (true)
            {
                var page = AssistantAttachmentContent.Read(pagedBytes, 2, pageOffset);
                pieces.Add(page.Text!);
                if (page.NextOffset is not { } next) break;
                if (next <= pageOffset) throw new InvalidOperationException("Page did not advance");
                pageOffset = next;
            }
            Check("pages_reconstruct_full_unicode_text", string.Concat(pieces) == "A😀BC😀D");
            var tiny = AssistantAttachmentContent.Read(Encoding.UTF8.GetBytes("😀x"), 1);
            Check("tiny_page_makes_progress", tiny.Text == "😀" && tiny.NextOffset == 2);
            bool invalidOffset = false;
            try { AssistantAttachmentContent.Read(pagedBytes, offset: 2); } catch (ArgumentOutOfRangeException) { invalidOffset = true; }
            Check("offset_inside_surrogate_pair_rejected", invalidOffset);
            Check("end_offset_returns_empty_final_page", AssistantAttachmentContent.Read(pagedBytes, offset: 8) is { Text: "", NextOffset: null });
            Check("bom_removed", AssistantAttachmentContent.Read(new byte[] {239,187,191,65}).Text == "A");
            Check("invalid_utf8_not_guessed", AssistantAttachmentContent.Read(new byte[] {255,255}).Text == null);
            Check("binary_controls_not_treated_as_text", AssistantAttachmentContent.Read(new byte[] {65,0,66}).Kind == "application/octet-stream");
            var pdfBuilder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            var font = pdfBuilder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            pdfBuilder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("FIRST PAGE", 12, new UglyToad.PdfPig.Core.PdfPoint(25, 700), font);
            pdfBuilder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("SECOND PAGE", 12, new UglyToad.PdfPig.Core.PdfPoint(25, 700), font);
            byte[] pdf = pdfBuilder.Build();
            var pdfText = AssistantAttachmentContent.Read(pdf);
            Check("pdf_text_and_page_provenance", pdfText.Text!.Contains("FIRST PAGE") && pdfText.Text.Contains("SECOND PAGE") && pdfText.Text.Contains("[第 2 页]") && pdfText.ExtractionNote == AssistantPdfContent.Note);
            var pdfStart = AssistantAttachmentContent.Read(pdf, 10);
            Check("pdf_paging_reconstructs_text", pdfStart.Text + AssistantAttachmentContent.Read(pdf, offset: pdfStart.NextOffset!.Value).Text == pdfText.Text);
            var blankPdf = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            blankPdf.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            Check("pdf_without_text_does_not_claim_ocr", AssistantAttachmentContent.Read(blankPdf.Build()) is { Text: "", ExtractionNote: var pdfNote } && pdfNote!.Contains("未做OCR"));
            bool brokenPdf = false;
            try { AssistantAttachmentContent.Read("%PDF-1.7"u8.ToArray()); } catch (WeChatMediaException) { brokenPdf = true; }
            Check("damaged_pdf_reported", brokenPdf);
            using var cancelledRead = new System.Threading.CancellationTokenSource();
            cancelledRead.Cancel();
            bool pdfCancelled = false, wordCancelled = false;
            try { AssistantPdfContent.Read("%PDF-invalid"u8.ToArray(), cancelledRead.Token); }
            catch (OperationCanceledException) { pdfCancelled = true; }
            try { AssistantWordContent.Read(new byte[] { 0 }, cancelledRead.Token); }
            catch (OperationCanceledException) { wordCancelled = true; }
            Check("cancelled_documents_are_not_parsed_or_reported_corrupt", pdfCancelled && wordCancelled);
            Check("png_detected_without_decoding", AssistantAttachmentContent.Read(new byte[] {137,80,78,71,13,10,26,10}).Kind == "image/png");
            byte[] word = Package("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>正文😀</w:t><w:tab/><w:t>继续</w:t><w:br/><w:t>下一行</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>单元格</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>");
            string wordId = store.Save(identity, "word-source", 0, "example.docx", 4, word);
            var extracted = AssistantAttachmentContent.Read(store.Load(identity, wordId).Content);
            Check("word_body_from_encrypted_cache", extracted.Kind == AssistantWordContent.Kind && extracted.Text == "正文😀\t继续\n下一行\n单元格\n" && extracted.ExtractionNote != null);
            var firstWordPage = AssistantAttachmentContent.Read(word, 4);
            var nextWordPage = AssistantAttachmentContent.Read(word, offset: firstWordPage.NextOffset!.Value);
            Check("word_pages_preserve_all_text", firstWordPage.Text + nextWordPage.Text == extracted.Text);
            Check("ordinary_zip_not_claimed_as_word", AssistantAttachmentContent.Read(Package("plain", "note.txt")) is { Kind: "application/zip", Text: null });
            bool invalidXml = false;
            try { AssistantAttachmentContent.Read(Package("<broken>")); } catch (WeChatMediaException e) { invalidXml = e.Message == "invalid_document_xml"; }
            Check("damaged_word_xml_reported", invalidXml);
            bool dtdRejected = false;
            try { AssistantAttachmentContent.Read(Package("<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///nonexistent'>]><x>&y;</x>")); } catch (WeChatMediaException e) { dtdRejected = e.Message == "invalid_document_xml"; }
            Check("document_external_entities_not_resolved", dtdRejected);
            bool inflatedRejected = false;
            try { AssistantAttachmentContent.Read(Package(new string('x', 8 * 1024 * 1024 + 1))); } catch (WeChatMediaException e) { inflatedRejected = e.Message == "document_xml_too_large"; }
            Check("expanded_document_size_is_bounded", inflatedRejected);
            string escapedText = string.Concat(Enumerable.Repeat("😀<>&\n", 3000));
            byte[] escapedBytes = Encoding.UTF8.GetBytes(escapedText);
            var joined = new StringBuilder(); int escapedOffset = 0, pages = 0;
            while (true)
            {
                var page = AssistantAttachmentContent.Read(escapedBytes, offset: escapedOffset);
                var facts = AssistantToolExecutor.AttachmentFacts("synthetic", page);
                string piece = facts.GetProperty("Text").GetString()!;
                Check("escaped_page_fits_" + pages, facts.GetRawText().Length <= Assistant.Core.ToolResultJson.DefaultMaximumResultCharacters / 2
                    && piece.Length > 0 && !char.IsHighSurrogate(piece[^1]));
                joined.Append(piece); pages++;
                if (facts.GetProperty("NextOffset").ValueKind == JsonValueKind.Null) break;
                int next = facts.GetProperty("NextOffset").GetInt32();
                if (next != escapedOffset + piece.Length || pages > 20) throw new Exception("Invalid page continuation");
                escapedOffset = next;
            }
            Check("escaped_attachment_pages_keep_exact_original_text", pages > 1 && joined.ToString() == escapedText);
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(checks));
            exit(0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); exit(1); }
        return Task.FromResult(true);
    }
}
