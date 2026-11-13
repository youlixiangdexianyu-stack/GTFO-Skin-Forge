namespace ForgeCore.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        var errors = new List<string>();
        Log.Sinks = _ => errors.Add;
        var passed = 0; var failed = 0;
        void Check(bool ok, string label)
        {
            if (ok) passed++;
            else { failed++; Console.WriteLine("FAIL " + label); }
        }
        NetChecks.Run(Check, errors);

        HookChecks.Run(Check);
        AnimationChecks.Run(Check, args);
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
