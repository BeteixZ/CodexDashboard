using System;
namespace CodexDashboard;
internal static class AssistantAttachmentFailure
{
    internal static string Describe(Exception error) => error switch
    {
        OperationCanceledException => "附件下载超时了。连接恢复后可以再试；如果仍失败，请重新发送附件。",
        ChatGptException model => model.Kind switch
        {
            ChatGptFailureKind.Authorization => "图片分析的模型授权不可用。请在电脑助手里检查登录状态。",
            ChatGptFailureKind.UsageLimit or ChatGptFailureKind.RateLimited => "图片分析暂时受到模型用量或频率限制，请稍后再试。",
            _ => "模型这次没有完成图片分析。请检查电脑助手中的模型状态后重试。"
        },
        WeChatMediaException media => media.Message switch
        {
            "unsupported_model_image_format" => "图片分析目前支持PNG或JPEG。PDF请用“读取附件”读取文字层，或另发需要分析的截图。",
            "invalid_image_content" => "这张图片无法解码。请重新保存为PNG或JPEG后发送。",
            "image_too_large" => "图片超过当前8 MiB上限，请缩小文件后发送。",
            "image_pixel_limit" => "图片超过当前2000万像素上限，请缩小图片或裁剪需要分析的区域。",
            "pdf_password_required" => "这份PDF需要密码才能读取。请在本机另存一份可直接打开的副本后发送，不用把密码发给助手。",
            "invalid_pdf_document" => "这份PDF的格式无法解析。请先确认本机能正常打开，再重新导出PDF或改发文字。",
            "pdf_too_many_pages" => "这份PDF超过当前1000页的读取上限。请只导出需要的页后发送。",
            "pdf_text_too_large" or "document_xml_too_large" => "这份文档展开后的文字超过读取上限。请拆分文档或只发送需要的部分。",
            "invalid_document_archive" or "invalid_document_xml" or "document_body_missing" => "这份Word文档的正文无法解析。请重新另存为.docx，或改发纯文字。",
            "media_too_large" => "附件超过当前8 MiB的读取上限。请压缩、拆分，或只发送需要的部分。",
            "media_download_rejected" => "微信没有允许下载这份附件，链接可能已失效。请重新发送附件。",
            "media_network_failure" => "下载附件时连接失败了。连接恢复后可以再次读取。",
            "media_decryption_failed" or "media_length_mismatch" => "下载的附件没能通过解密或完整性核对。请重新发送原文件。",
            _ => "这份附件暂时无法读取，请核对编号或重新发送。"
        },
        _ => "这份附件暂时无法读取，请核对编号或重新发送。"
    };
}
