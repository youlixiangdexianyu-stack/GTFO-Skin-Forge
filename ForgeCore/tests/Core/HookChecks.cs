using System.Reflection;
using System.Reflection.Emit;

namespace ForgeCore.Tests;

/// <summary>One game method, one package: a second owner is refused and the startup check finds a method two Forge packages patch
/// (detection); a hook subscription never outlives its world or reaches a subscriber after it failed.</summary>
internal static class HookChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Owners(check);
        Leases(check);
    }

    private static void Target() { }
    private static void Other() { }

    // A static method compiled into an assembly of the given name, as a package's patch callback would be.
    private static MethodInfo Callback(string assembly)
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assembly), AssemblyBuilderAccess.Run).DefineDynamicModule(assembly);
        var type = module.DefineType("Callbacks", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        type.DefineMethod("Postfix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator().Emit(OpCodes.Ret);
        return type.CreateType()!.GetMethod("Postfix")!;
    }

    private static void Owners(Action<bool, string> check)
    {
        var target = typeof(HookChecks).GetMethod(nameof(Target), BindingFlags.NonPublic | BindingFlags.Static)!;
        var other = typeof(HookChecks).GetMethod(nameof(Other), BindingFlags.NonPublic | BindingFlags.Static)!;
        check(HookOwners.Claim("ForgeMap", target, "set") == null && HookOwners.Claim("ForgeMap", target, "set") == ""
            && HookOwners.Claim("ForgeMap", target, "changed") == "native-hook-declaration-changed", "an owner claims once, again is a no-op, a changed set refuses");
        check(HookOwners.Claim("ForgeEnemy", target, "set") == "native-hook-writer-conflict existing=ForgeMap"
            && HookOwners.Refusal("ForgeEnemy", target) == "native-hook-writer-conflict existing=ForgeMap", "a second owner is refused");
        HookOwners.Release("ForgeEnemy", target);
        check(HookOwners.OwnerOf(target) == "ForgeMap", "another package cannot release the owner's claim");
        HookOwners.Claim("ForgeMap", other, "set");

        MethodInfo map = Callback("ForgeMap.Native"), enemy = Callback("ForgeEnemy.Native"), foreign = Callback("SomeoneElse");
        var records = new Dictionary<MethodBase, (string, MethodInfo)[]>
        {
            [target] = new[] { (HookOwners.OwnPrefix + "forge.module.gtfo.map", map), ("NAinfini.ForgeEnemy", enemy) },
            [other] = new[] { (HookOwners.OwnPrefix + "forge.module.gtfo.map", map), (HookOwners.ObservePrefix + "ForgeEnemy", enemy), ("someone", foreign) },
        };
        var shared = HookOwners.Shared(method => records[method]);
        check(shared.Count == 1 && shared[0] == typeof(HookChecks).FullName + "." + nameof(Target) + ": ForgeEnemy, ForgeMap",
            "the startup check names the one method two Forge packages patch; an observer or another mod is not a second package");
        HookOwners.Release("ForgeMap", target); HookOwners.Release("ForgeMap", other);
    }

    private static void Leases(Action<bool, string> check)
    {
        long world = 1;
        var failures = new List<string>();
        var fanout = new HookFanout<int>(() => world, (consumer, _) => failures.Add(consumer), budget: 2);
        var received = new List<string>();
        var old = fanout.Subscribe("old", 1, value => received.Add("old" + value));
        Refused(() => fanout.Subscribe("old", 1, _ => { }), "native-hook-consumer-duplicate", check);
        var bad = fanout.Subscribe("bad", 1, _ => throw new InvalidOperationException());
        Refused(() => fanout.Subscribe("third", 1, _ => { }), "native-hook-subscriber-budget", check);
        check(fanout.Publish(1, 1) == HookDispatch.Delivered && bad.Status == "observer-failed" && failures.SequenceEqual(new[] { "bad" })
            && received.SequenceEqual(new[] { "old1" }), "a failing subscriber is ended and reported once; the others still run");
        world = 2;
        check(fanout.Publish(1, 1) == HookDispatch.WorldEpochStale && !old.IsActive && old.Status == "world-epoch-ended",
            "a lease of an old world ends by itself and its world's calls are not delivered");
        Refused(() => fanout.Subscribe("late", 1, _ => { }), "native-hook-world-epoch-stale", check);
        var next = fanout.Subscribe("old", 2, value => received.Add("new" + value));
        check(fanout.Publish(2, 2) == HookDispatch.Delivered && received.Last() == "new2" && next.IsActive, "the new world's subscriber receives only new calls");
        next.Dispose();
        check(next.Status == "released" && fanout.Publish(3, 2) == HookDispatch.NoSubscribers, "a released lease receives nothing");
    }

    private static void Refused(Action action, string reason, Action<bool, string> check)
    {
        try { action(); check(false, "expected " + reason); }
        catch (InvalidOperationException error) { check(error.Message == reason, "refuses " + reason); }
    }
}
