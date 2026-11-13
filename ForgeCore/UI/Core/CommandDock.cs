using System;

namespace ForgeUI;

/// <summary>Sizes of a command list in its two looks, 1080p pixels: the compact idle rows and the table shown while a code is typed or a
/// command is armed; in between, sizes blend linearly by <see cref="CommandMotion.Table"/>.</summary>
internal static class CommandGeometry
{
    public const float IdleRow = 30f, IdleGap = 4f, TableRow = 38f, TableGap = 5f, Header = 26f, Hint = 28f;
    public const float Pad = 10f, Block = 6f, BlockGap = 8f, NameTail = 16f, IdleCell = 52f, TableCell = 62f, CellInset = 3f, Cut = 8f;
    public const float Key = 26f, KeyStep = 30f, KeyPad = 8f, KeyGap = 16f, IdleName = 16f, TableName = 19f;

    public static float Row(float table) => IdleRow + (TableRow - IdleRow) * table;
    public static float Gap(float table) => IdleGap + (TableGap - IdleGap) * table;
    public static float Cell(float table) => IdleCell + (TableCell - IdleCell) * table;
    public static float NamePx(float table) => IdleName + (TableName - IdleName) * table;

    /// <summary>The width the key cells of a <paramref name="steps"/> long code take, with the space before the names.</summary>
    public static float Codes(int steps) => steps <= 0 ? 0f : KeyPad + steps * KeyStep - (KeyStep - Key) + KeyGap;

    /// <summary>The list's width: the code column, the category block and the widest name, the value cell.</summary>
    public static float Width(float codes, float name, float table) => codes + Pad + Block + BlockGap + name + NameTail + Cell(table);
}

/// <summary>
/// The animated shape of a command list: how far it has turned from idle rows into the table (<see cref="Table"/>), how much of each row
/// shows (an armed command shows its row alone) and how wide the code column is. Every value eases towards its target at one rate from
/// the frame the form changes, so the list's height and width are continuous and the final size is known on that first frame
/// (<see cref="TargetHeight"/>). The first step after <see cref="Reset"/> lands on the targets at once.
/// </summary>
internal sealed class CommandMotion
{
    /// <summary>The easing rate (about 95 % of the way in 0.17 s).</summary>
    public const float Rate = 18f;
    private const float Settle = 0.002f;
    private readonly float[] _shown;
    private readonly float[] _shownTarget;
    private int _count;
    private bool _primed;
    private float _tableTarget, _noteTarget;

    public CommandMotion(int maxRows)
    {
        _shown = new float[Math.Max(1, maxRows)];
        _shownTarget = new float[_shown.Length];
    }

    /// <summary>0 idle rows, 1 the table.</summary>
    public float Table { get; private set; }

    /// <summary>0..1: how much of the line under the list a notice holds open while the list is idle (the table always has the line).</summary>
    public float Note { get; private set; }

    /// <summary>The code column's width now.</summary>
    public float Codes { get; private set; }

    public int Count => _count;

    /// <summary>How much of row <paramref name="index"/> shows, 0..1.</summary>
    public float Shown(int index) => (uint)index < (uint)_count ? _shown[index] : 0f;

    /// <summary>True once every value is at its target.</summary>
    public bool Settled { get; private set; } = true;

    /// <summary>The next step lands on its targets at once (the list is shown again, or the HUD was rebuilt).</summary>
    public void Reset() => _primed = false;

    /// <summary>Moves every value towards the form's targets: <paramref name="table"/> for the table, <paramref name="held"/> the only row
    /// that shows (-1 all), <paramref name="codes"/> the code column's width, <paramref name="note"/> a notice under the list.</summary>
    public void Step(bool table, int count, int held, float codes, bool note, float dt)
    {
        count = Math.Clamp(count, 0, _shown.Length);
        _tableTarget = table ? 1f : 0f;
        _noteTarget = note ? 1f : 0f;
        for (var i = 0; i < count; i++) _shownTarget[i] = held < 0 || i == held ? 1f : 0f;
        // A row that comes into the list starts shown; the list's height follows its count at once only when rows are added or removed,
        // which is a change of the data, not of the form.
        for (var i = _count; i < count; i++) _shown[i] = _shownTarget[i];
        _count = count;
        if (!_primed)
        {
            _primed = true;
            Table = _tableTarget;
            Note = _noteTarget;
            Codes = codes;
            for (var i = 0; i < count; i++) _shown[i] = _shownTarget[i];
            Settled = true;
            return;
        }
        var settled = true;
        Table = Approach(Table, _tableTarget, dt, ref settled);
        Note = Approach(Note, _noteTarget, dt, ref settled);
        Codes = Approach(Codes, codes, dt, ref settled, 0.05f);
        for (var i = 0; i < count; i++) _shown[i] = Approach(_shown[i], _shownTarget[i], dt, ref settled);
        Settled = settled;
    }

    private static float Approach(float value, float target, float dt, ref bool settled, float snap = Settle)
    {
        var next = Ease.Damp(value, target, Rate, dt);
        if (Math.Abs(next - target) <= snap) return target;
        settled = false;
        return next;
    }

    /// <summary>The height now.</summary>
    public float Height => HeightOf(Table, Note, _shown);

    /// <summary>The height the list settles at for the current targets (known on the frame the form changes).</summary>
    public float TargetHeight => HeightOf(_tableTarget, _noteTarget, _shownTarget);

    private float HeightOf(float table, float note, float[] shown)
    {
        var rows = 0f;
        var step = CommandGeometry.Row(table) + CommandGeometry.Gap(table);
        for (var i = 0; i < _count; i++) rows += step * shown[i];
        if (rows > 0f) rows -= CommandGeometry.Gap(table);
        return CommandGeometry.Header * table + CommandGeometry.Hint * Math.Max(table, note) + rows;
    }

    /// <summary>The height of <paramref name="count"/> rows in the table with its header and hint: the tallest the list gets.</summary>
    public static float TallestHeight(int count) =>
        CommandGeometry.Header + CommandGeometry.Hint + Math.Max(0, count) * (CommandGeometry.TableRow + CommandGeometry.TableGap) - (count > 0 ? CommandGeometry.TableGap : 0f);
}

/// <summary>
/// Where a command list stands: against the right edge of the screen, its middle on the middle of the screen, clear of everything else
/// on the HUD (the game's weapon list and vitals, other elements). Coordinates are relative to the middle of the screen, y up.
/// <see cref="Right"/> picks the column once from the list's largest size, so opening the table never moves it sideways: the screen's
/// right edge while the column there has room for the tallest list between what is below and above the middle, else just left of
/// whatever stands in the way. <see cref="Centre"/> then keeps the list's middle on the screen's middle unless something below or above
/// would be overlapped; a list that cannot fit keeps clear of what is below it.
/// </summary>
internal static class CommandDock
{
    /// <summary>Space kept between the list and anything else.</summary>
    public const float Gap = 12f;

    /// <summary>The highest bottom and lowest top the list may reach in the column [<paramref name="left"/>, <paramref name="right"/>]:
    /// above everything there whose middle is below the screen's middle, under everything whose middle is above it, within
    /// [<paramref name="bottom"/>, <paramref name="top"/>];
    /// <paramref name="floorBy"/> and <paramref name="ceilingBy"/> are the indices of the obstacles that set them, -1 for the screen.</summary>
    public static void Limits(ReadOnlySpan<HudBox> obstacles, float left, float right, float bottom, float top, out float floor, out float ceiling,
        out int floorBy, out int ceilingBy)
    {
        floor = bottom;
        ceiling = top;
        floorBy = ceilingBy = -1;
        for (var i = 0; i < obstacles.Length; i++)
        {
            var o = obstacles[i];
            if (o.Width <= 0f || o.Height <= 0f || o.Right <= left || o.Left >= right) continue;
            if (o.Bottom + o.Top <= 0f)
            {
                if (o.Top + Gap > floor) { floor = o.Top + Gap; floorBy = i; }
            }
            else if (o.Bottom - Gap < ceiling) { ceiling = o.Bottom - Gap; ceilingBy = i; }
        }
    }

    /// <summary>The list's right edge: <paramref name="right"/> (the screen's edge less its margin) while a list of
    /// <paramref name="width"/> x <paramref name="height"/> fits in the column there, else moved left past whatever crosses the middle
    /// band, until it fits.</summary>
    public static float Right(ReadOnlySpan<HudBox> obstacles, float right, float bottom, float top, float width, float height)
    {
        for (var step = 0; step < 8; step++)
        {
            Limits(obstacles, right - width, right, bottom, top, out var floor, out var ceiling, out _, out _);
            if (ceiling - floor >= height) return right;
            var next = right;
            foreach (var o in obstacles)
                if (o.Width > 0f && o.Height > 0f && o.Left < right && o.Right > right - width && o.Bottom < height / 2f + Gap && o.Top > -height / 2f - Gap)
                    next = Math.Min(next, o.Left - Gap);
            if (next >= right) return right;
            right = next;
        }
        return right;
    }

    /// <summary>The list's middle: the screen's middle, raised over <paramref name="floor"/> or lowered under
    /// <paramref name="ceiling"/>; when it cannot fit between them it stays clear of the floor.</summary>
    public static float Centre(float height, float floor, float ceiling)
    {
        var centre = 0f;
        if (centre - height / 2f < floor) centre = floor + height / 2f;
        if (centre + height / 2f > ceiling) centre = Math.Max(floor + height / 2f, ceiling - height / 2f);
        return centre;
    }
}
