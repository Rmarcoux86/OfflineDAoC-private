using System;
using DOL.GS.PacketHandler;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// Act 5 (owner 2026-10-27): Preesh (the Gauntlet's final champion) sends the player a taunt, as a /send, foreshadowing
/// the SmuG waiting after Darkness Falls. One is ready every <see cref="CooldownMs"/>; it goes out with the player's next
/// kill (the first kill in act 5 gets one straight away).
/// </summary>
public static class GauntletTaunts
{
    public const string Sender = "Preesh";
    public const long CooldownMs = 10 * 60 * 1000;

    private const string NextTauntProperty = "expedition.gauntlet.nextTaunt";

    /// <summary>The taunts; {0} is the monster just killed, {1} the player's name.</summary>
    public static readonly string[] Lines =
    [
        "Oh, you think you're a big man killing that {0}? Just wait till you find me.",
        "Cute. Try that again when it parries and stuns you back.",
        "That {0} was a warm-up. We're the main event.",
        "Keep farming, {1}. The SmuG aren't going anywhere.",
        "I'd clap, but I'm busy polishing my mace.",
        "You call that a fight? I've seen apprentice necyomancers do better.",
        "Bring that energy to the arena. You'll need all of it.",
        "Every demon you drop is one less excuse when I drop you.",
        "Ios says hi. Check behind you. Actually, don't bother, you won't see her coming.",
        "Drunkstains bet you'd die before Legion. I took the over. Don't make me regret it.",
        "Nice swing. Shame you'll spend most of our fight stunned.",
        "Remember to bow when you get to us. It'll be the last polite thing you do.",
        "Toothmouth wants to know if you're bringing a healer. You'll want one.",
        "Gwyraan's been dotting a training dummy for an hour waiting on you. Hurry up.",
        "Killed a {0}. Wow. Want a medal or a realm rank?",
        "We're watching, {1}. Every pull.",
    ];

    /// <summary>The taunt with its blanks filled; the line index wraps.</summary>
    public static string Line(int index, string monster, string player)
    {
        string line = Lines[((index % Lines.Length) + Lines.Length) % Lines.Length];

        return string.Format(line, string.IsNullOrWhiteSpace(monster) ? "thing" : monster, player ?? string.Empty);
    }

    /// <summary>A kill the expedition tracker credited to the player in act 5: maybe a taunt.</summary>
    public static void OnKill(GamePlayer player, ActPlan plan, GameLiving target)
    {
        if (player == null || plan == null || plan.Act != ActGenerator.ActCount)
        {
            return;
        }

        long now = GameLoop.GameLoopTime;

        if (now < player.TempProperties.GetProperty<long>(NextTauntProperty))
        {
            return;
        }

        player.TempProperties.SetProperty(NextTauntProperty, now + CooldownMs);
        ChatUtil.SendSendMessage(player, "Social.ReceiveMessage.Private.Sends", Sender, Line(Random.Shared.Next(Lines.Length), target?.Name, player.Name));
    }
}
