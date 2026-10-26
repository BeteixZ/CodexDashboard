using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexDashboard;
internal static class AssistantImageTests
{
    private sealed class Stub(Func<HttpRequestMessage,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return send(r); } }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--image-input-test", var root]) return false;
        Directory.CreateDirectory(root); var checks = new Dictionary<string,bool>();
        void Check(string key, bool ok) { checks[key] = ok; if (!ok) throw new InvalidOperationException(key); }
        try
        {
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,255,255},4))); encoder.Save(stream);
            byte[] bytes = stream.ToArray(); var image = AssistantImageInput.FromBytes(bytes);
            Check("valid_png_has_verified_dimensions", image.Width == 1 && image.Height == 1);
            Check("image_debug_text_omits_payload", !image.ToString().Contains("base64") && !image.ToString().Contains(image.DataUrl));
            bool bad = false; try { AssistantImageInput.FromBytes(new byte[]{137,80,78,71,13,10,26,10}); } catch (WeChatMediaException) { bad = true; }
            Check("signature_only_is_not_valid_image", bad);
            bool text = false; try { AssistantImageInput.FromBytes(Encoding.UTF8.GetBytes("not an image")); } catch (WeChatMediaException) { text = true; }
            Check("text_cannot_be_sent_as_image", text);
            bool pdfRejectedWithoutParsing = false;
            try { AssistantImageInput.FromBytes("%PDF-invalid"u8.ToArray()); }
            catch (WeChatMediaException error) { pdfRejectedWithoutParsing = error.Message == "unsupported_model_image_format"; }
            Check("non_image_pdf_is_rejected_without_document_parsing", pdfRejectedWithoutParsing);
            string vaultPath = Path.Combine(root,"synthetic-vault.state");
            new ChatGptVault { Accounts = new() { new ChatGptAccount { ClientId="synthetic",Subject="test",AccessToken="SYNTHETIC",RefreshToken="SYNTHETIC_REFRESH",ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),Scope=ChatGptAuth.Scopes } } }.Save(vaultPath);
            var settings = new Settings(); settings.ChatGpt.ActiveAccount="synthetic"; settings.ChatGpt.Model="synthetic-model"; settings.ChatGpt.Enabled=true;
            int calls=0; bool hold = false;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var model = new ChatGptAssistant(settings,()=>{},new ChatGptAuth(vaultPath),new Stub(async request =>
            {
                calls++; using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var payload=json.RootElement;
                var parts=payload.GetProperty("input")[0].GetProperty("content");
                Check("actual_request_contains_image_and_question",parts[0].GetProperty("type").GetString()=="input_text" && parts[0].GetProperty("text").GetString()!.Contains("这是什么颜色")
                    && parts[1].GetProperty("type").GetString()=="input_image" && parts[1].GetProperty("image_url").GetString()==image.DataUrl && !payload.GetProperty("store").GetBoolean());
                if (hold) { entered.TrySetResult(); await release.Task; }
                return new(HttpStatusCode.OK) { Content=new StringContent("data: "+JsonSerializer.Serialize(new {type="response.completed",response=new {status="completed",output=new[]{new{type="message",role="assistant",content=new[]{new{type="output_text",text="合成图片回答"}}}}}})+"\n\n",Encoding.UTF8,"text/event-stream") };
            }));
            Check("image_transport_uses_existing_response_parser",await model.DescribeImageAsync("这是什么颜色",image,default)=="合成图片回答" && calls==1);
            bool invalidated=false;
            try { await model.DescribeImageAsync("旧图片",image,default,()=>throw new InvalidOperationException("synthetic context changed")); } catch(InvalidOperationException){invalidated=true;}
            Check("stale_context_does_not_call_model",invalidated && calls==1);
            hold = true;
            var occupying = model.DescribeImageAsync("这是什么颜色", image, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool current = true;
            var queued = model.DescribeImageAsync("这是什么颜色", image, default, () => { if (!current) throw new InvalidOperationException("changed while waiting"); });
            current = false; release.TrySetResult(); await occupying;
            bool rejectedAfterWait = false;
            try { await queued; } catch (InvalidOperationException) { rejectedAfterWait = true; }
            Check("context_rechecked_after_model_gate_wait", rejectedAfterWait && calls == 2);
            File.WriteAllText(Path.Combine(root,"checks.json"),JsonSerializer.Serialize(checks)); exit(0);
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());exit(1);}
        return true;
    }
}
