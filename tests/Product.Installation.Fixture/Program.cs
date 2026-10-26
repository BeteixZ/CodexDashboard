if (args.Length != 2) return 2;
File.WriteAllText(args[0], "synthetic fixture ready");
var deadline = DateTime.UtcNow.AddSeconds(60);
while (!File.Exists(args[1]) && DateTime.UtcNow < deadline) await Task.Delay(50);
return 0;
