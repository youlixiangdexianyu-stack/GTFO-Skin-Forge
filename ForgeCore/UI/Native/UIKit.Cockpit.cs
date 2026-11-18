using System;

namespace ForgeUI;

public sealed partial class UIKitMod
{
    /// <summary>A power-on message with your beats, colours and words (see <see cref="HudBootMessage"/>). Default slot: the centre
    /// banner, priority 100, sized by <paramref name="look"/>.</summary>
    /// <example><c>var link = ui.AddBootMessage("Link", new BootMessageBeats(), new BootMessageLook()); link.Prepare(words); link.Play(t);</c></example>
    public HudBootMessage? AddBootMessage(string name, BootMessageBeats beats, BootMessageLook look, HudSlot? slot = null)
    {
        if (beats == null) throw new ArgumentNullException(nameof(beats));
        if (look == null) throw new ArgumentNullException(nameof(look));
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudBootMessage(beats, look), slot ?? new HudSlot(HudRegion.CenterBanner, 100, look.Width, look.Height));
    }
}
