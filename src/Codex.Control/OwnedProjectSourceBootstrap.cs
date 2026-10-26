using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codex.Control;

internal static class OwnedProjectSourceBootstrap
{
    // Runs in the same SSH process, then replaces itself with the configured App Server.
    // The source hash uses exactly the collector's machine/account/opened-home identity tuple.
    internal const string Script = """
import hashlib,json,os,re,sys
source=None
home,exe=sys.argv[1:3]
try:
    with open('/etc/machine-id',encoding='ascii') as f: machine=f.read().strip()
    if not re.fullmatch('[0-9a-f]{32}',machine) or machine=='0'*32: raise ValueError('machine')
    fd=os.open(home,os.O_PATH|os.O_DIRECTORY|os.O_CLOEXEC)
    try:
        info=os.fstat(fd)
        canonical=os.readlink('/proc/self/fd/'+str(fd))
        current=os.stat(home)
        if not canonical.startswith('/') or canonical.endswith(' (deleted)') or any(ord(c)<32 for c in canonical) or (current.st_dev,current.st_ino)!=(info.st_dev,info.st_ino): raise ValueError('directory')
        identity=format(info.st_dev,'x')+':'+format(info.st_ino,'x')
        value=json.dumps([machine,os.getuid(),canonical,identity],separators=(',',':'))
        source=hashlib.sha256(value.encode('utf-8')).hexdigest()
    finally: os.close(fd)
except (OSError,ValueError,AttributeError,UnicodeError): pass
print(json.dumps({'projectSourceIdentity':source}),flush=True)
env=os.environ.copy()
env['CODEX_HOME']=home
os.execve(exe,[exe,'app-server','--listen','stdio://'],env)
""";

    internal static async Task<string?> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(18));
        using var line = new MemoryStream(); var one = new byte[1];
        while (await stream.ReadAsync(one, timeout.Token) == 1 && one[0] != '\n')
        {
            if (line.Length >= 256) throw new IOException("project_source_bootstrap_too_large");
            line.WriteByte(one[0]);
        }
        using var document = JsonDocument.Parse(line.ToArray()); var value = document.RootElement.GetProperty("projectSourceIdentity");
        if (value.ValueKind == JsonValueKind.Null) return null;
        string? source = value.GetString();
        return source != null && Regex.IsMatch(source, "\\A[0-9a-f]{64}\\z") ? source : throw new IOException("invalid_project_source_bootstrap");
    }
}
