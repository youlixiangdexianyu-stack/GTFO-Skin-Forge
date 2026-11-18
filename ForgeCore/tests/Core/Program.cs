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
        FileHashChecks.Run(Check);
        ProgressionChecks.Run(Check, errors);
        LobbyChecks.Run(Check);
        PredictionChecks.Run(Check);
        AnimationChecks.Run(Check, args);
        ProceduralChecks.Run(Check);
        ChainChecks.Run(Check);
        LookChecks(Check);
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>A stand-in for a look provider (a character-model mod): it reads only its own descriptions.</summary>
    private sealed class FakeLooks
    {
        public string? Dress(byte[] description) => description.Length == 1 ? "model-" + description[0] : null;
    }

    /// <summary>The player-looks contract users rely on: with a provider, a look it described reaches it and is worn as it says; with none, with
    /// another provider's look, or after it withdrew, there is nothing to wear and the user draws the game's own body; a second provider is refused.</summary>
    private static void LookChecks(Action<bool, string> check)
    {
        var router = new ForgeVisual.LookRouter<FakeLooks>();
        var look = new ForgeVisual.PlayerLook("Test.Looks", new byte[] { 4 });
        var none = router.For(look);
        var fake = new FakeLooks();
        var first = router.Provide("Test.Looks", fake);
        var second = router.Provide("Other.Looks", new FakeLooks());
        var worn = router.For(look)?.Dress(look.Data);
        var foreign = router.For(new ForgeVisual.PlayerLook("Other.Looks", new byte[] { 4 }));
        router.Withdraw(fake);
        check(none == null && first && !second && router.Id == null && worn == "model-4" && foreign == null && router.For(look) == null,
            "player looks: the provider's own look is worn, no provider or another's look gives the game's body, a second provider is refused");
    }
}
