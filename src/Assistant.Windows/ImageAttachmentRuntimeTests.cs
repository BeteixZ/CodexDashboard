using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core;

namespace CodexDashboard;
internal static class ImageAttachmentRuntimeTests
{
    private sealed class Stub(byte[] bytes, Action count) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { ct.ThrowIfCancellationRequested(); count(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent(bytes)}); } }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if(args is not ["--image-attachment-runtime-test",var root])return false;
        Directory.CreateDirectory(root);var checks=new Dictionary<string,bool>();
        void Check(string key,bool ok){checks[key]=ok;if(!ok)throw new InvalidOperationException(key);}
        try
        {
            using var stream=new MemoryStream();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,255,255},4)));encoder.Save(stream);byte[] bytes=stream.ToArray();
            int downloads=0,analyses=0;
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attachments=new AssistantInboundAttachments(root,()=>new WeChatMediaClient(new Stub(bytes,()=>downloads++)),enableCapture:true);
            await using var f=await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(root,"runtime"),attachments:attachments,
                imageAnalyzer:async (question,image,ct)=>{analyses++;Check("analyzer_receives_exact_image_and_question",image.DataUrl=="data:image/png;base64,"+Convert.ToBase64String(bytes)&&question=="图片中的颜色");if(analyses==3){pending.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}return analyses == 2 ? new string('字', 15999) + "😀后文" : "合成图片描述";});
            var batch=JsonNode.Parse(f.Batch("image-source",""))!;
            batch["msgs"]![0]!["item_list"]!.AsArray().Add(new JsonObject { ["type"]=2,["image_item"]=new JsonObject{["media"]=new JsonObject{["encrypt_query_param"]="synthetic-image"}}});
            using var doc=JsonDocument.Parse(batch.ToJsonString());await f.Channel.ProcessUpdatesAsync(doc.RootElement,default);
            await ConversationHistoryRuntimeTests.Settled(f,"image-source");
            string id=(await f.ReadAsync()).Inbox.Single().Body[AssistantInboundAttachments.Marker.Length..].Split('\n')[0];
            Check("image_receipt_does_not_automatically_invoke_model",analyses==0&&downloads==0);
            bool offered=false,received=false,unicodeSafe=false;
            f.Script.Handler=(request,ct)=>
            {
                offered|=request.Tools.Any(t=>t.Name=="analyze_image");
                if(request.Observations.IsEmpty)return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("image-read","analyze_image",JsonSerializer.Serialize(new{attachmentId=id,question="图片中的颜色"}))));
                string raw=request.Observations.Last().ResultJson;
                using var json=JsonDocument.Parse(raw);var facts=json.RootElement.GetProperty("facts");
                received=facts.GetProperty("text").GetString()=="合成图片描述"&&!facts.GetProperty("currentAuthorization").GetBoolean()&&!facts.GetProperty("independentlyVerified").GetBoolean()&&!raw.Contains("base64")&&!raw.Contains("encrypt_query_param");
                if (analyses == 2) unicodeSafe = facts.GetProperty("text").GetString() == new string('字', 15999)
                    && facts.GetProperty("truncated").GetBoolean();
                return Task.FromResult(ToolModelResponse.Reply("图片分析回放完成。"));
            };
            await f.InputAsync("ask-image","看看图片 "+id);await ConversationHistoryRuntimeTests.Settled(f,"ask-image");
            Check("image_tool_loop_returns_interpretation_without_bytes_or_authority",offered&&received&&analyses==1&&downloads==1&&(await f.ReadAsync()).Operations.IsEmpty);
            await f.InputAsync("ask-again","再看一下图片 "+id);await ConversationHistoryRuntimeTests.Settled(f,"ask-again");
            Check("repeat_analysis_reuses_encrypted_image_cache",analyses==2&&downloads==1);
            Check("long_image_answer_does_not_split_surrogate_pair", unicodeSafe);
            int sentBeforeStop = f.Sent.Count;
            await f.InputAsync("stop-image", "继续看图片 " + id);
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Runtime.StopSessionAsync();
            Check("session_stop_drains_image_analysis_without_late_reply", analyses == 3 && f.Sent.Count == sentBeforeStop);
            File.WriteAllText(Path.Combine(root,"checks.json"),JsonSerializer.Serialize(checks));exit(0);
        }
        catch(Exception error){File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString());exit(1);}
        return true;
    }
}
