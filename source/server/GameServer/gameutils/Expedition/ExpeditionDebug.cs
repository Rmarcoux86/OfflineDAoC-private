using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// Kill and loot tracing for Expedition runs. Every decision is written to the server log at Info; a player who typed
/// /run debug also sees it in the system window.
/// </summary>
public static class ExpeditionDebug
{
    public const string ChatKey = "EXPEDITION_DEBUG_CHAT";

    private static readonly Logger Log = LoggerManager.Create(typeof(ExpeditionDebug));

    public static bool IsChatOn(GamePlayer player)
    {
        return player != null && player.TempProperties.GetProperty<bool>(ChatKey, false);
    }

    public static void Report(GamePlayer player, string line)
    {
        if (Log.IsInfoEnabled)
        {
            Log.Info($"[EXPEDITION DEBUG] {player?.Name}: {line}");
        }

        if (IsChatOn(player))
        {
            player.Out.SendMessage($"[debug] {line}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }
}
