using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexDashboard;

internal sealed record AutomaticPreference(string Key, string Evidence);
internal sealed record AutomaticMemoryGuard(string Stamp, string Model, long ConversationRevision);

internal static class AutomaticPreferenceText
{
    internal static readonly ImmutableArray<string> AllowedKeys =
        ["language", "tone", "detail_level", "report_order", "report_layout", "math_format", "progress_updates", "workflow", "tooling", "assistant_name", "user_name"];
    private static readonly HashSet<string> Keys = new(AllowedKeys, StringComparer.Ordinal);
    private static readonly Regex QuotedAssistantName = new("^(?:(?:以后|今后|从现在起|从今以后|请|就)\\s*)*(?:你(?:以后|今后)?(?:就)?(?:叫|名叫|的(?:名字|昵称)(?:是|叫))|我(?:以后|今后)?(?:就)?叫你|称呼你为)\\s*[“「\"']?[\\p{L}\\p{N}_·.-]{1,32}[”」\"']?[。！!]*$", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedUserName = new("^(?:(?:以后|今后|从现在起|从今以后|请|就)\\s*)*(?:我(?:就)?(?:叫|名叫|的(?:名字|昵称)(?:是|叫))|叫我|称呼我(?:为)?)\\s*[“「\"']?[\\p{L}\\p{N}_·.-]{1,32}[”」\"']?[。！!]*$", RegexOptions.CultureInvariant);
    private static bool Naming(string text, string key)
    {
        if (Regex.IsMatch(text, "什么|怎么|如何|是谁|谁叫|是否|吗|[？?]")) return false;
        return key == "assistant_name" ? QuotedAssistantName.IsMatch(text.Trim()) : key == "user_name" && QuotedUserName.IsMatch(text.Trim());
    }
    private static bool CorrectNameRole(string text, string key)
    {
        string value = Regex.Replace(text.Trim(), @"^(?:(?:以后|今后|从现在起|从今以后|请|就)\s*)*", "");
        if (Regex.IsMatch(value, "什么|怎么|如何|是谁|是否|吗|[？?]")) return false;
        return key == "assistant_name" ? Regex.IsMatch(value, @"^(?:你(?:以后|今后)?(?:就)?(?:是|叫|名叫|的(?:名字|昵称))|我(?:以后|今后)?(?:就)?叫你|称呼你为|your (?:name|nickname)|I(?:'ll| will) call you)", RegexOptions.IgnoreCase)
            : key == "user_name" && Regex.IsMatch(value, @"^(?:我(?:就)?(?:叫|名叫|的(?:名字|昵称))|叫我|称呼我|my (?:name|nickname)|call me)", RegexOptions.IgnoreCase);
    }
    private static readonly Regex Excluded = new(@"这次|本次|这轮|本轮|今天|明天|现在|暂时|临时|仅这|只在|假设|例如|比如|如果|他说|她说|原文|引用|翻译|密码|口令|密钥|身份证|银行卡|手机号|住址|病史|健康|政治|宗教|性取向|\b(?:password|token|secret|api[_ -]?key|today|this time|for now|quote|example|translate)\b|(?:忽略|绕过|跳过|自动批准).{0,12}(?:权限|审批|安全|指令)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Host-directed work must not become a lasting preference merely because
    // its SSH alias is absent from a hard-coded list. Keep ordinary Chinese
    // style feedback beginning with "让回答..." eligible for semantic extraction.
    private static readonly Regex WorkRequest = new(@"^(?:请\s*)?(?:继续|运行|执行|重新测试|修复|部署|删除|查看|查询|汇报|解释|分析|列出|帮我检查|让\s*(?:本机|服务器|主机|[A-Za-z0-9][A-Za-z0-9_.-]*))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveOrAuthority = new(@"密码|口令|密钥|身份证|银行卡|手机号|住址|病史|健康|政治|宗教|性取向|(?<![A-Za-z0-9_])(?:password|token|secret|api[_ -]?key)(?![A-Za-z0-9_])|(?:忽略|绕过|跳过|自动批准).{0,12}(?:权限|审批|安全|指令)|(?:无需|不用|不必|不要|不需要|免于|省略|取消).{0,8}(?:确认|许可|审批|授权|安全检查|安全校验)|(?:默认|自动|全部|一律|始终).{0,8}(?:同意|批准|授权)|(?:所有|任何).{0,8}(?:都已|已获|有).{0,4}(?:授权|许可)|(?<![A-Za-z])(?:skip|bypass|disable).{0,20}(?:approval|permission|confirmation)|(?<![A-Za-z])(?:always|automatically).{0,12}(?:approve|authorize)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Unquoted(string text) => Regex.Replace(Regex.Replace(text, @"(?m)^\s*>.*$", " "), "(?s)```.*?```|「.*?」|『.*?』|“.*?”|‘.*?’|\"[^\"]*\"", " ");
    internal static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[\s\p{P}]+", "");
    private static string[] Clauses(string text) => text.Split(new[] { '\n', '。', '！', '!', '？', '?', '；', ';' });
    // Keep quoted ranges intact. Deleting a quote before finding boundaries can erase
    // "this time" or attribution and turn an incomplete fragment into false evidence.
    private static IEnumerable<string> SourceClauses(string text)
    {
        var closers = new Stack<char>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (closers.Count > 0 && c == closers.Peek()) { closers.Pop(); continue; }
            bool wordApostrophe = c == '\'' && i > 0 && i + 1 < text.Length
                && char.IsAsciiLetter(text[i - 1]) && char.IsAsciiLetter(text[i + 1]);
            char closer = c switch
            {
                '“' => '”', '「' => '」', '『' => '』', '‘' => '’', '"' => '"',
                '\'' when !wordApostrophe => '\'', _ => '\0'
            };
            if (closer != '\0') { closers.Push(closer); continue; }
            if (closers.Count == 0 && c is '\n' or '。' or '！' or '!' or '？' or '?' or '；' or ';')
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text[start..];
    }
    private static string ClauseText(string text) => text.Trim().TrimEnd('。', '！', '!', '？', '?', '；', ';', '，', ',', '：', ':').Trim();
    private static bool UnsafeSource(string text) => text.Length is <= 0 or > 4000
        || text.Contains("```", StringComparison.Ordinal)
        || Regex.IsMatch(text, @"(?m)^\s*>|(?:他说|她说|对方说|原文|引用|示例|例如|比如|翻译|假设|如果|这次|本次|今天|暂时|临时)[^。！？!?；;\r\n]{0,100}[：:]\s*(?:\r?\n)|(?:他说|她说|对方说|原文|引用|示例|例如|比如|翻译|假设|如果|这次|本次|今天|暂时|临时)\s*(?:\r?\n)", RegexOptions.CultureInvariant)
        || Regex.IsMatch(text, @"不要.{0,8}(?:记住|记忆|保存)|别.{0,8}(?:记住|记忆|保存)|(?:不|无须|无需)保存|(?:忘记|删除记忆|清空记忆)", RegexOptions.CultureInvariant);
    internal static bool MayLearn(string text) => text.Length is > 0 and <= 4000 && (Clauses(Unquoted(text)).Any(Eligible)
        || Clauses(text).Any(clause => Naming(clause, "assistant_name") || Naming(clause, "user_name")));
    // No activation keyword is required. Semantic preference detection belongs
    // to the existing model request; local checks constrain its source/scope.
    private static bool Eligible(string text) => text.Trim().Length is >= 4 and <= 400 && !Excluded.IsMatch(text) && !WorkRequest.IsMatch(text.Trim());
    internal const string ProtocolMarker = "附加的偏好提取协议";
    internal const string AnswerFormat = "本次最终输出格式仍只允许一个 JSON 对象：result 是完整的自然语言回复字符串，preferences 是偏好数组。人设和自然聊天要求只作用于 result 内的文字，不改变外层协议。不要在 JSON 前后再输出一遍回复，不加代码围栏；外层字段由应用处理，不发给用户。";
    internal const string Instructions = "\n\n" + ProtocolMarker + "（微信问答）：最终只输出 JSON 对象，字段 result 和 preferences。result 必须是直接回答用户的文本字符串。" + PreferenceRules;
    internal const string IntentInstructions = "\n\n" + ProtocolMarker + "（仅任务分流）：你只分类，绝不能回答用户问题。最终只输出 JSON 对象，字段 result 和 preferences；result 必须始终是包含 intent、targetQuery、reference 三个字符串字段的对象，不能是普通回答字符串。即使用户发 hello、你叫什么或一般问题，也必须给出 chat 对象；不要介绍自己、不要寒暄。示例：{\"result\":{\"intent\":\"chat\",\"targetQuery\":\"\",\"reference\":\"unspecified\"},\"preferences\":[]}。" + PreferenceRules;
    private const string PreferenceRules = "preferences 是数组，每项只有 key 和 evidence，最多两项；无可保存偏好时必须是 []。key 只能为 language、tone、detail_level、report_order、report_layout、math_format、progress_updates、workflow、tooling、assistant_name、user_name。assistant_name 是用户给微信助手设置的昵称或别名，user_name 是用户希望你对他的称呼，两者不能混淆；'你叫赛特'、'以后叫你小桥'是 assistant_name，'叫我阿林'是 user_name。只从最后一条用户输入的 owner_message 或 user_message 中提取用户自己的、清楚表达的长期偏好及称呼设定，例如'以后汇报先说结论'、'我更喜欢分行'；不需要出现'记住'。evidence 必须是该消息逐字出现的连续原文，包含完整偏好陈述及其适用范围，不改写、不补充，最长 400 字符。直接命名语句中的引号只是昵称的标点，可以保留；他人的引文不能保存。不能从历史对话、AGENTS、记忆、任务标题、报告、dashboard_context 或 task_context 提取。不能保存问题中的猜测、别人的话、示例、引文、代码、敏感个人信息、凭据或本次临时任务要求。询问'你叫什么'是问题，不是新命名。任务状态和操作授权永不保存为偏好。偏好与工作指令混合时，只抽取独立的长期偏好分句，不抽取任务名称或指令。无法确定就返回空数组。此协议不改变任务分流标准和原文投递。result 不要宣称已经保存记忆，是否保存由应用校验决定。";
    internal static IReadOnlyList<AutomaticPreference> Read(JsonElement root, string source)
    {
        if (!root.TryGetProperty("preferences", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 2) return Array.Empty<AutomaticPreference>();
        var accepted = new List<AutomaticPreference>();
        if (UnsafeSource(source)) return accepted;
        var clauses = SourceClauses(source).ToArray();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("key", out var keyValue) || keyValue.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("evidence", out var evidenceValue) || evidenceValue.ValueKind != JsonValueKind.String) continue;
            string key = keyValue.GetString()!, evidence = evidenceValue.GetString()!.Trim();
            if (key is "assistant_name" or "user_name" && !CorrectNameRole(evidence, key)) continue;
            // Exact evidence must be a whole user-authored clause, including any
            // attribution or time restriction. Models cannot trim those away.
            bool wholeClause = clauses.Any(clause => ClauseText(clause) == ClauseText(evidence)
                && !clause.Contains('?') && !clause.Contains('？'));
            bool directNaming = Naming(evidence, key);
            bool quoted = Regex.IsMatch(evidence, "[“”「」『』‘’\"']");
            if (!Keys.Contains(key) || !source.Contains(evidence, StringComparison.Ordinal) || !wholeClause
                || SensitiveOrAuthority.IsMatch(evidence) || !(Eligible(evidence) && !quoted || directNaming)
                || accepted.Any(p => p.Key == key)) continue;
            accepted.Add(new(key, evidence));
        }
        return accepted;
    }
    internal static bool TryReadTool(JsonElement arguments, string source, out ImmutableArray<AutomaticPreference> preferences)
    {
        preferences = [];
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1
            || !arguments.TryGetProperty("preferences", out var array) || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() is < 1 or > 2) return false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return false;
            var names = item.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 2 || names.Distinct(StringComparer.Ordinal).Count() != 2
                || !item.TryGetProperty("key", out _) || !item.TryGetProperty("evidence", out _)) return false;
        }
        var accepted = Read(arguments, source);
        if (accepted.Count != array.GetArrayLength() || accepted.Any(p => !StableDeclaration(p))) return false;
        preferences = accepted.ToImmutableArray();
        return true;
    }
    private static bool StableDeclaration(AutomaticPreference preference)
    {
        string text = preference.Evidence;
        if (Regex.IsMatch(text, @"吗|是否|能否|可不可以|能不能|是不是|[？?]|\b(?:whether|could you|would you)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        if (preference.Key is "assistant_name" or "user_name") return CorrectNameRole(text, preference.Key);
        if (!Regex.IsMatch(text, @"(?:我|我们).{0,4}(?:喜欢|希望|偏好|习惯)|以后|今后|通常|一般|默认|最好|更舒服|更清楚|优先|不要|不喜欢|(?:公式|回答|汇报).{0,8}(?:用|先|保持)|\b(?:prefer|always|by default)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        string subject = preference.Key switch
        {
            "language" => @"中文|英文|英语|汉语|日语|法语|德语|语言|\b(?:Chinese|English|language)\b",
            "tone" => @"语气|口吻|风格|自然|直接|直来直去|简短|温柔|严肃|客气|亲切|熟人|委婉",
            "detail_level" => @"详细|简短|简洁|展开|篇幅|字数|句|段|长|短",
            "report_order" => @"结论|结果|依据|先|后|顺序",
            "report_layout" => @"分行|换行|段落|列表|小标题|表格|排版|栏目|格式",
            "math_format" => @"数学|公式|LaTeX|\$",
            "progress_updates" => @"进度|进展|汇报|通知|更新",
            "workflow" => @"流程|步骤|检查|验证|测试|计划|讨论|确认",
            "tooling" => @"工具|PowerShell|Python|终端|命令|编辑器|Git|Windows|Linux|macOS",
            _ => @"(?!)"
        };
        return Regex.IsMatch(text, subject, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    internal static string Answer(string raw, string source, out IReadOnlyList<AutomaticPreference> preferences)
    {
        preferences = Array.Empty<AutomaticPreference>();
        if (!raw.TrimStart().StartsWith('{'))
        {
            // Some responses emit the same prose followed by its protocol
            // envelope. Unwrap only an exact duplicate; never mine preferences
            // from this malformed output or rewrite ordinary JSON explanations.
            int index = raw.LastIndexOf('{');
            for (int attempts = 0; index >= 0 && attempts < 8; attempts++, index = index == 0 ? -1 : raw.LastIndexOf('{', index - 1))
            {
                try
                {
                    using var duplicate = JsonDocument.Parse(raw[index..]);
                    var root = duplicate.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 2
                        && root.TryGetProperty("result", out var repeated) && repeated.ValueKind == JsonValueKind.String
                        && root.TryGetProperty("preferences", out var candidates) && candidates.ValueKind == JsonValueKind.Array
                        && raw[..index].Trim() == repeated.GetString()!.Trim()) return repeated.GetString()!;
                }
                catch (JsonException) { }
            }
            return raw; // Failed extraction must not break a normal plain-text answer.
        }
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
            throw new ChatGptException("模型回答格式不完整，请重新提问。");
        preferences = Read(doc.RootElement, source); return result.GetString()!;
    }
    internal static TaskIntent Intent(string raw, string source, out IReadOnlyList<AutomaticPreference> preferences)
    {
        preferences = Array.Empty<AutomaticPreference>(); using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("任务分流必须为对象。");
        if (!doc.RootElement.TryGetProperty("result", out var result)) return TaskIntentText.Parse(raw);
        var fields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Distinct().Count() != fields.Length || fields.Any(p => p is not ("result" or "preferences"))) throw new JsonException("任务分流含有不支持的字段。");
        // A classifier can mistakenly answer the question. Discard that text and
        // its proposed memories; only the personalized Q&A path may answer.
        // This fallback can never select a task, queue work, or grant authority.
        if (result.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(result.GetString())) return new("chat", "", "unspecified");
        if (result.ValueKind != JsonValueKind.Object) throw new JsonException("任务分流 result 必须为对象。");
        var intent = TaskIntentText.Parse(result.GetRawText()); preferences = Read(doc.RootElement, source); return intent;
    }
}
