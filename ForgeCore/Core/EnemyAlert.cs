namespace ForgeCore;

/// <summary>How alert an enemy is, coarsest to most: off (dead, no AI or switched off), asleep, awake without having noticed anyone, and hunting.
/// Libraries colour and filter by it; <c>Enemies.Alert</c> reads it from the game.</summary>
public enum EnemyAlert : byte { Off, Sleeping, Awake, Hunting }

/// <summary>The one rule that turns what the game says of an enemy into an <see cref="EnemyAlert"/>.</summary>
public static class EnemyAlerts
{
    // The game's ES_StateEnum numbers read here (Hibernate 14, HibernateWakeUp 15, StuckInGlue 17).
    private const int Hibernate = 14, HibernateWakeUp = 15, StuckInGlue = 17;

    /// <summary>The alert of an enemy whose AI mode reads as <paramref name="mode"/> (asleep for <c>AgentMode.Hibernate</c> and so on) and
    /// whose locomotion state is <paramref name="locomotion"/> (the game's <c>ES_StateEnum</c> number, 0 when not known). The locomotion is
    /// what the body does and reaches every machine at once; a client's copy of the AI mode stays asleep for seconds, sometimes for good,
    /// after the enemy got up (11-06 recordings: median 2.2 s, a tenth of them over 190 s). So the hibernation state is asleep, its wake-up
    /// state is awake, and a sleeper whose body left both hunts; an enemy stuck in glue keeps the alert of its mode.</summary>
    public static EnemyAlert Settle(EnemyAlert mode, int locomotion)
    {
        if (mode == EnemyAlert.Off || locomotion == 0) return mode;
        if (locomotion == Hibernate) return EnemyAlert.Sleeping;
        if (locomotion == HibernateWakeUp) return EnemyAlert.Awake;
        return mode == EnemyAlert.Sleeping && locomotion != StuckInGlue ? EnemyAlert.Hunting : mode;
    }
}
