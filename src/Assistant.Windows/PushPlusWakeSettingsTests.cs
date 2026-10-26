using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexDashboard;

// Settings only: current-user DPAPI on invented strings, no production files,
// window construction, HTTP, model, channel, Settings.Load or Settings.Save.
internal static class PushPlusWakeSettingsTests
{
    internal static Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--pushplus-wake-settings-test");
        if (index < 0) return Task.FromResult(false);
        try
        {
            if (args.Length != 2 || index != 0) throw new ArgumentException("explicit_new_output_required");
            Run(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); exit(1); }
        return Task.FromResult(true);
    }

    private static void Run(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)) throw new ArgumentException("absolute_output_required");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("new_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, Func<bool> action)
        {
            bool result;
            try { result = action(); } catch { result = false; }
            checks.Add(name, result);
        }

        const string first = "SYNTHETIC_WAKE_TOKEN_FIRST", second = "SYNTHETIC_WAKE_TOKEN_SECOND";
        var options = new WeChatOptions();
        Check("defaults_do_not_enable_push_or_allocate_route_or_quiet", () => !options.PushPlusEnabled
            && options.PushPlusWakeRouteId == Guid.Empty && options.TryReadWakeQuietHours(out var quiet, out _) && quiet == null);
        Check("saving_same_empty_token_does_not_allocate_route", () => !options.WritePushPlusToken("") && options.PushPlusWakeRouteId == Guid.Empty);
        Check("explicit_route_initialization_requests_persistence_without_enabling", () => options.EnsureWakeRouteId()
            && options.PushPlusWakeRouteId != Guid.Empty && !options.PushPlusEnabled && options.ProtectedPushPlusToken == "");
        Guid emptyRoute = options.PushPlusWakeRouteId;
        Check("repeat_ensure_keeps_stable_route", () => !options.EnsureWakeRouteId() && options.PushPlusWakeRouteId == emptyRoute);
        Check("first_token_change_rotates_route_without_enabling", () => options.WritePushPlusToken(first)
            && options.PushPlusWakeRouteId != emptyRoute && !options.PushPlusEnabled && options.ReadPushPlusToken() == first);
        Guid firstRoute = options.PushPlusWakeRouteId;
        string firstCipher = options.ProtectedPushPlusToken;
        Check("same_token_preserves_route_and_ciphertext", () => !options.WritePushPlusToken(first)
            && options.PushPlusWakeRouteId == firstRoute && options.ProtectedPushPlusToken == firstCipher);
        Check("normal_ui_trim_does_not_rotate_equal_token", () => !options.WritePushPlusToken(" " + first + " ")
            && options.PushPlusWakeRouteId == firstRoute && options.ProtectedPushPlusToken == firstCipher);
        Check("different_token_rotates_once_and_keeps_enable_choice", () => options.WritePushPlusToken(second)
            && options.PushPlusWakeRouteId != firstRoute && !options.PushPlusEnabled && options.ReadPushPlusToken() == second);
        Guid secondRoute = options.PushPlusWakeRouteId;
        Check("clear_token_rotates_route_and_clears_cipher", () => options.WritePushPlusToken("")
            && options.PushPlusWakeRouteId != secondRoute && options.ProtectedPushPlusToken == "" && !options.PushPlusEnabled);
        Guid clearedRoute = options.PushPlusWakeRouteId;
        Check("repeated_clear_keeps_route", () => !options.WritePushPlusToken("") && options.PushPlusWakeRouteId == clearedRoute);

        options.WritePushPlusToken(first);
        options.PushPlusEnabled = true;
        Check("new_token_never_overrides_enabled_choice", () => options.WritePushPlusToken(second) && options.PushPlusEnabled);
        Check("serialized_dpapi_roundtrip_keeps_token_route_and_no_plaintext", () =>
        {
            string json = JsonSerializer.Serialize(options, Settings.Json);
            string file = Path.Combine(directory, "synthetic-settings.json");
            File.WriteAllText(file, json);
            var copy = JsonSerializer.Deserialize<WeChatOptions>(File.ReadAllText(file), Settings.Json)!;
            return !json.Contains(first, StringComparison.Ordinal) && !json.Contains(second, StringComparison.Ordinal)
                && copy.ReadPushPlusToken() == second && copy.PushPlusWakeRouteId == options.PushPlusWakeRouteId && copy.PushPlusEnabled;
        });
        Check("legacy_json_has_empty_route_without_implicit_migration", () =>
        {
            var old = JsonSerializer.Deserialize<WeChatOptions>("{\"pushPlusEnabled\":true}", Settings.Json)!;
            return old.PushPlusEnabled && old.PushPlusWakeRouteId == Guid.Empty
                && old.TryReadWakeQuietHours(out var quiet, out _) && quiet == null;
        });
        Check("legacy_protected_token_ensure_preserves_cipher_and_enable", () =>
        {
            var old = new WeChatOptions { ProtectedPushPlusToken = LocalSecret.Protect(first), PushPlusEnabled = true };
            string cipher = old.ProtectedPushPlusToken;
            return old.EnsureWakeRouteId() && old.PushPlusEnabled && old.ProtectedPushPlusToken == cipher && old.ReadPushPlusToken() == first;
        });

        foreach (string damaged in new[] { "not-base64", Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }) })
        {
            string kind = damaged == "not-base64" ? "format" : "dpapi";
            Check("unreadable_" + kind + "_normal_save_rejects_without_mutation", () =>
            {
                var bad = new WeChatOptions { ProtectedPushPlusToken = damaged, PushPlusWakeRouteId = Guid.NewGuid(), PushPlusEnabled = true };
                Guid route = bad.PushPlusWakeRouteId;
                return Throws(() => bad.WritePushPlusToken(first)) && bad.ProtectedPushPlusToken == damaged
                    && bad.PushPlusWakeRouteId == route && bad.PushPlusEnabled;
            });
        }
        Check("unreadable_token_cannot_be_implicitly_cleared", () =>
        {
            var bad = new WeChatOptions { ProtectedPushPlusToken = "not-base64", PushPlusWakeRouteId = Guid.NewGuid() };
            Guid route = bad.PushPlusWakeRouteId;
            return Throws(() => bad.WritePushPlusToken("")) && bad.ProtectedPushPlusToken == "not-base64" && bad.PushPlusWakeRouteId == route;
        });
        Check("explicit_unreadable_replacement_rotates_route_without_enabling", () =>
        {
            var bad = new WeChatOptions { ProtectedPushPlusToken = "not-base64", PushPlusWakeRouteId = Guid.NewGuid() };
            Guid route = bad.PushPlusWakeRouteId;
            bad.ReplaceUnreadablePushPlusToken(first);
            return bad.PushPlusWakeRouteId != route && bad.ReadPushPlusToken() == first && !bad.PushPlusEnabled
                && !bad.WritePushPlusToken(first);
        });
        Check("replacement_method_refuses_readable_token", () =>
        {
            var readable = new WeChatOptions(); readable.WritePushPlusToken(first);
            string cipher = readable.ProtectedPushPlusToken; Guid route = readable.PushPlusWakeRouteId;
            return Throws(() => readable.ReplaceUnreadablePushPlusToken(second))
                && readable.ProtectedPushPlusToken == cipher && readable.PushPlusWakeRouteId == route;
        });
        Check("explicit_unreadable_replacement_requires_nonempty_input", () =>
        {
            var bad = new WeChatOptions { ProtectedPushPlusToken = "not-base64" };
            return Throws(() => bad.ReplaceUnreadablePushPlusToken(" ")) && bad.ProtectedPushPlusToken == "not-base64" && bad.PushPlusWakeRouteId == Guid.Empty;
        });
        Check("null_saved_cipher_rejects_normal_save_and_explicit_recovery_works", () =>
        {
            var bad = JsonSerializer.Deserialize<WeChatOptions>("{\"protectedPushPlusToken\":null}", Settings.Json)!;
            if (!Throws(() => bad.WritePushPlusToken(first)) || bad.PushPlusWakeRouteId != Guid.Empty) return false;
            bad.ReplaceUnreadablePushPlusToken(first);
            return bad.ReadPushPlusToken() == first && bad.PushPlusWakeRouteId != Guid.Empty && !bad.PushPlusEnabled;
        });
        foreach (string value in new[] { new string('x', 513), "SYNTHETIC\nTOKEN" })
            Check(value.Length > 512 ? "oversize_token_has_no_partial_write" : "control_character_token_has_no_partial_write", () =>
            {
                var valid = new WeChatOptions(); valid.WritePushPlusToken(first);
                string cipher = valid.ProtectedPushPlusToken; Guid route = valid.PushPlusWakeRouteId;
                return Throws(() => valid.WritePushPlusToken(value)) && valid.ProtectedPushPlusToken == cipher && valid.PushPlusWakeRouteId == route;
            });

        var quietOptions = new WeChatOptions();
        Check("valid_daily_quiet_hours_capture_exact_zone", () => quietOptions.TrySetWakeQuietHours("09:00", "17:00", "UTC", out _)
            && quietOptions.TryReadWakeQuietHours(out var quiet, out _) && quiet == new PushPlusWakeQuietHours("09:00", "17:00", "UTC")
            && !quietOptions.PushPlusEnabled && quietOptions.PushPlusWakeRouteId == Guid.Empty);
        Check("overnight_quiet_is_valid", () => quietOptions.TrySetWakeQuietHours("22:00", "08:00", "UTC", out _)
            && quietOptions.TryReadWakeQuietHours(out var quiet, out _) && quiet?.Start == "22:00" && quiet.End == "08:00");
        var badQuiet = new (string? Start, string? End, string? Zone)[]
        {
            ("24:00", "08:00", "UTC"), ("22:60", "08:00", "UTC"), ("22:00", "22:00", "UTC"),
            ("9:00", "17:00", "UTC"), ("22:00", null, "UTC"), ("22:00", "08:00", null),
            (null, null, "UTC"), ("22:00", "08:00", "SYNTHETIC_INVALID_ZONE"),
            (" 2:00", "08:00", "UTC"), ("22:00", "08:00", " UTC ")
        };
        for (int i = 0; i < badQuiet.Length; i++)
        {
            var value = badQuiet[i];
            Check("invalid_quiet_rejects_atomically_and_corrupt_load_fails_closed_" + i, () =>
            {
                var valid = new WeChatOptions(); valid.TrySetWakeQuietHours("22:00", "08:00", "UTC", out _);
                if (valid.TrySetWakeQuietHours(value.Start, value.End, value.Zone, out var error) || string.IsNullOrEmpty(error)
                    || !valid.TryReadWakeQuietHours(out var retained, out _) || retained != new PushPlusWakeQuietHours("22:00", "08:00", "UTC")) return false;
                var damaged = new WeChatOptions { PushPlusWakeQuietStart = value.Start, PushPlusWakeQuietEnd = value.End, PushPlusWakeTimeZoneId = value.Zone };
                return !damaged.TryReadWakeQuietHours(out var invalid, out var readError) && invalid == null && !string.IsNullOrEmpty(readError);
            });
        }
        Check("quiet_roundtrip_preserves_saved_timezone", () =>
        {
            var copy = JsonSerializer.Deserialize<WeChatOptions>(JsonSerializer.Serialize(quietOptions, Settings.Json), Settings.Json)!;
            return copy.TryReadWakeQuietHours(out var quiet, out _) && quiet == new PushPlusWakeQuietHours("22:00", "08:00", "UTC");
        });
        Check("disable_quiet_clears_both_times_and_timezone_together", () => quietOptions.TrySetWakeQuietHours(null, null, null, out _)
            && quietOptions.PushPlusWakeQuietStart == null && quietOptions.PushPlusWakeQuietEnd == null && quietOptions.PushPlusWakeTimeZoneId == null
            && quietOptions.TryReadWakeQuietHours(out var quiet, out _) && quiet == null);

        int failures = checks.Count(item => !item.Value);
        File.WriteAllText(Path.Combine(directory, "pushplus-wake-settings-checks.json"), JsonSerializer.Serialize(new
        { count = checks.Count, failures, actualDpapi = true, networkUsed = false, productionDataRead = false, actualModelUsed = false, checks }, Settings.Json));
        if (failures != 0) throw new InvalidOperationException("synthetic_wake_settings_checks_failed");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or CryptographicException) { return true; }
    }
}
