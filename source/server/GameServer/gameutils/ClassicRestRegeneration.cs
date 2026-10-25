namespace DOL.GS
{
    /// <summary>One timing/stance rule for real players and NPC-backed playerbots.
    /// Native property calculators still apply level, buffs, debuffs and server modifiers.</summary>
    public static class ClassicRestRegeneration
    {
        public static int HealthAndPowerInterval(bool sitting, bool inCombat)
        {
            return (6 - (sitting ? 3 : 0) + (inCombat ? 8 : 0) - (sitting && inCombat ? 1 : 0)) * 1000;
        }

        /// <summary>
        /// Fork (owner 2026-10-25): percent of the maximum a resting tick restores after sitting out of combat for
        /// <paramref name="restingMs"/>: 2% at once, +2% every 3 seconds, 10% from 12 seconds on.
        /// </summary>
        public static int RestingPercent(long restingMs)
        {
            long steps = System.Math.Max(0, restingMs) / 3000;

            return (int) System.Math.Min(10, 2 + steps * 2);
        }

        public static int BaseEndurancePerTick(bool sitting, bool inCombat, bool moving)
        {
            return inCombat || moving ? 0 : sitting ? 4 : 1;
        }
    }
}
