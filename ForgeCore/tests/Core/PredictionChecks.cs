namespace ForgeCore.Tests;

/// <summary>The core algorithm of prediction: every prediction is closed exactly once (settled, out of time or cleared), and what was
/// shown plus every closure equals what the authority settled, in amount and in kills, in any order of requests, settlements,
/// losses and timeouts. One detection case: a settlement that names more requests than are open says so.</summary>
internal static class PredictionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var book = new PredictionBook();
        book.Known(1, 100f, 1, 0f);
        book.Predict(1, 10f, 0f, 0f, out _, out _, out _);
        book.Predict(1, 20f, 0f, 0f, out _, out _, out _);
        book.Predict(1, 30f, 0f, 0f, out var before, out _, out _);
        var two = book.Settle(1, 2, 28f, false, 72f, 2, 0.3f);
        check(before == 70f && two.AmountDelta == -2f && two.KillDelta == 0 && two.Unmatched == 0 && book.Open == 1 && book.Remaining(1, out var left) && left == 42f,
            "prediction: a settlement closes the subject's oldest predictions and what is left is the told level less the open ones");

        book = new PredictionBook();
        book.Known(1, 50f, 1, 0f);
        book.Predict(1, 30f, 0f, 0f, out _, out _, out var early);
        book.Predict(1, 30f, 0f, 0f, out _, out var last, out var kill);
        book.Predict(1, 30f, 0f, 0f, out _, out var spent, out var again);
        var dead = book.Settle(1, 3, 50f, true, 0f, 2, 0.3f);
        check(!early && kill && last == 20f && !again && spent == 0f && dead.AmountDelta == 0f && dead.KillDelta == 0 && !dead.ShowKill && book.Open == 0,
            "prediction: the request that takes the last predicts the kill once, and the settled kill changes nothing shown");

        book = new PredictionBook();
        book.Known(1, 50f, 1, 0f);
        book.Predict(1, 60f, 0f, 0f, out _, out _, out kill);
        var lived = book.Settle(1, 1, 40f, false, 10f, 2, 0.3f);
        book.Predict(2, 10f, 100f, 0f, out _, out _, out var none);
        var surprise = book.Settle(2, 1, 10f, true, 0f, 3, 0.3f);
        check(kill && lived.AmountDelta == -10f && lived.KillDelta == -1 && !none && surprise.KillDelta == 1 && surprise.ShowKill,
            "prediction: a predicted kill that did not happen is taken back, one that was not predicted is added and shown");

        book = new PredictionBook();
        book.Predict(1, 60f, 50f, 0f, out _, out _, out kill);
        var expired = book.Expire(PredictionBook.TimeoutSeconds + 0.1f, out var key, out var gone);
        var late = book.Settle(1, 1, 50f, true, 0f, 1, 4f);
        check(kill && expired && key == 1 && gone.AmountDelta == -50f && gone.KillDelta == -1 && late.AmountDelta == 50f && late.KillDelta == 1 && !late.ShowKill && late.Unmatched == 1
            && !book.Expire(100f, out _, out _),
            "prediction: one out of time is taken back, and its late settlement is counted without being shown again");

        book = new PredictionBook();
        book.Predict(1, 10f, 80f, 0f, out before, out _, out _);
        book.Known(1, 60f, 0, 0f);
        book.Known(1, 70f, 65535, 0f);
        book.Known(1, 55f, 1, 0f);
        book.Remaining(1, out left);
        check(before == 80f && left == 45f,
            "prediction: a level this machine read is replaced by any the authority tells, and an older stamp is ignored");

        // The authority settled the first of two requests and told the level it left; the report overtakes the settlement.
        book = new PredictionBook();
        book.Known(1, 100f, 1, 0f, 7);
        book.Predict(1, 10f, 0f, 0f, out _, out _, out _);
        book.Predict(1, 20f, 0f, 0f, out _, out _, out _);
        book.Known(1, 90f, 3, 0.1f, 8);
        book.Remaining(1, out var overtaken);
        book.Predict(1, 5f, 0f, 0.1f, out before, out _, out _);
        var behind = book.Settle(1, 1, 10f, false, 90f, 2, 0.2f, 8);
        book.Remaining(1, out left);
        check(overtaken == 70f && before == 70f && behind.AmountDelta == 0f && left == 65f,
            "prediction: a level told after a settlement that has not come yet is not reduced by the requests it already includes");

        // A client far behind: the first request ran out of time here, the second is still open, and the level the
        // authority then tells includes only the first. The second is still to come off, and each settlement closes its own.
        book = new PredictionBook();
        book.Known(1, 100f, 1, 0f, 7);
        book.Predict(1, 10f, 0f, 0f, out _, out _, out _);
        book.Predict(1, 20f, 0f, 2.9f, out _, out _, out _);
        var ranOut = book.Expire(3f, out _, out _);
        book.Known(1, 90f, 2, 3f, 8);
        book.Remaining(1, out var told);
        var first = book.Settle(1, 1, 10f, false, 90f, 2, 3.1f, 8);
        book.Remaining(1, out var between);
        var second = book.Settle(1, 1, 20f, false, 70f, 3, 3.2f, 9);
        book.Remaining(1, out left);
        check(ranOut && told == 70f && first.AmountDelta == 10f && first.Unmatched == 1 && between == 70f && book.Open == 0
                && second.AmountDelta == 0f && second.Unmatched == 0 && left == 70f,
            "prediction: a level that includes only a request that ran out of time here still has the open one taken off");

        book = new PredictionBook();
        var held = true;
        for (var i = 0; i < PredictionBook.Entries; i++) held &= book.Predict(1, 1f, 1000f, 0f, out _, out _, out _);
        var over = book.Predict(1, 1f, 1000f, 0f, out _, out _, out _);
        var cleared = book.Clear();
        var rows = true;
        for (var i = 0; i < PredictionBook.Subjects; i++) rows &= book.Predict(100 + i, 1f, 10f, 0f, out _, out _, out _);
        var full = book.Predict(7, 1f, 10f, 0f, out _, out _, out _);
        check(held && !over && cleared.AmountDelta == -PredictionBook.Entries && book.Open == PredictionBook.Subjects && rows && !full,
            "prediction: past its capacity a prediction is not held, and clearing takes back everything open");

        var more = new PredictionBook();
        more.Predict(1, 10f, 100f, 0f, out _, out _, out _);
        check(more.Settle(1, 3, 30f, false, 70f, 1, 0.1f).Unmatched == 2, "prediction detection: a settlement naming more requests than are open reports the unmatched ones");

        Interleaved(check);
    }

    // Requests on a few subjects in random order with random settlements (the authority's own amounts and kills), lost requests and
    // waits past the timeout: what the screen holds (predictions plus closures) must end equal to what the authority settled.
    private static void Interleaved(Action<bool, string> check)
    {
        var random = new Random(20261116);
        var book = new PredictionBook();
        var queues = new Queue<float>[6];
        var keys = new long[6];
        long next = 1;
        for (var i = 0; i < queues.Length; i++) { queues[i] = new Queue<float>(); keys[i] = next++; }
        double shown = 0, settled = 0;
        int kills = 0, deaths = 0;
        ushort stamp = 0;
        var now = 0f;
        var ok = true;
        void Close(PredictionClosure closure) { shown += closure.AmountDelta; kills += closure.KillDelta; }
        void Expire() { while (book.Expire(now, out _, out var closure)) Close(closure); }
        for (var step = 0; step < 20000; step++)
        {
            var at = random.Next(queues.Length);
            var roll = random.Next(100);
            if (roll < 50)
            {
                if (queues[at].Count >= 30) continue;
                var amount = random.Next(1, 40);
                ok &= book.Predict(keys[at], amount, random.Next(1, 200), now, out _, out var predicted, out var kill);
                shown += predicted;
                if (kill) kills++;
                // One request in ten never reaches the authority.
                if (random.Next(10) != 0) queues[at].Enqueue(amount);
            }
            else if (roll < 85)
            {
                if (queues[at].Count == 0) continue;
                var hits = random.Next(1, Math.Min(4, queues[at].Count) + 1);
                float amount = 0;
                for (var i = 0; i < hits; i++) amount += random.Next(0, (int)queues[at].Dequeue() + 1);
                var killed = random.Next(8) == 0;
                Close(book.Settle(keys[at], hits, amount, killed, killed ? 0f : random.Next(1, 200), ++stamp, now));
                settled += amount;
                if (!killed) continue;
                deaths++;
                // The authority settles nothing more on the dead; the slot goes to a new subject.
                book.Forget(keys[at]);
                queues[at].Clear();
                keys[at] = next++;
            }
            else
            {
                now += random.Next(4) == 0 ? PredictionBook.TimeoutSeconds : 0.05f;
                Expire();
            }
        }
        now += PredictionBook.TimeoutSeconds + 1f;
        Expire();
        check(ok && book.Open == 0 && Math.Abs(shown - settled) < 1e-3 && kills == deaths,
            "prediction: in any order of requests, settlements, losses and timeouts, what was shown ends equal to what was settled (" + shown + "/" + settled + ", " + kills + "/" + deaths + ")");
        Close(book.Clear());
        check(Math.Abs(shown - settled) < 1e-3 && kills == deaths, "prediction: clearing a settled book changes nothing");
    }
}
