using ForgeAudio;
using ForgeAudio.Tests;

var failures = 0;
var checks = 0;
void Check(bool condition, string name)
{
    checks++;
    if (condition) return;
    failures++;
    Console.WriteLine("FAIL " + name);
}

// Expected rejection cases are asserted below; keep their structured results without filling test stderr.
AudioLog.Sink = (_, _) => { };
CatalogChecks.Run(Check);
DecodeChecks.Run(Check);

Console.WriteLine(failures == 0
    ? "ForgeAudio core: " + checks + " checks passed, 0 failed"
    : "ForgeAudio core: " + checks + " checks run, " + failures + " failed");
return failures == 0 ? 0 : 1;
