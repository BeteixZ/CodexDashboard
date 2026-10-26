using System.Security.Cryptography;
using Assistant.Core;
using CodexDashboard;

if (args.Length != 2 || args[0] is not ("--reject-archive" or "--read-legacy") || !File.Exists(args[1])) return 2;
string path = Path.GetFullPath(args[1]);
var before = SHA256.HashData(await File.ReadAllBytesAsync(path)); bool rejected = false;
try
{
    await using var ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(path), new WindowsStateProtector());
    var identities = await ledger.ListConversationIdentitiesAsync();
    if (args[0] != "--read-legacy" || identities.Length != 1
        || (await ledger.ReadConversationAsync(identities[0])).Inbox.Length != 1) return 3;
}
catch (LedgerException error) when (error.Code == LedgerErrors.CorruptStorage)
{ rejected = true; }
var after = SHA256.HashData(await File.ReadAllBytesAsync(path));
if (rejected != (args[0] == "--reject-archive") || !before.SequenceEqual(after)) return 4;
Console.WriteLine(args[0] == "--reject-archive" ? "PASS frozen reader rejects archive without changing journal" : "PASS frozen reader reads legacy without changing journal");
return 0;
