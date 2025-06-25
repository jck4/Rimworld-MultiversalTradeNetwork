// Main.cs
using Verse;
using RimWorld;
using MTN;
using UnityEngine;

namespace MultiversalTradeMod
{
    public class Main : Mod
    {
        private MTNSettings settings;

        public Main(ModContentPack content) : base(content)
        {
            // Initialize settings
            this.settings = GetSettings<MTNSettings>();
            
            // Initialize Steam auth ticket at game/mod startup
            SteamAuthUtils.InitAuthTicket();
            // Check if Steam is available
            if (SteamAuthUtils.IsSteamAvailable())
            {
                var steamID = SteamAuthUtils.GetCurrentSteamID();
                var steamName = SteamAuthUtils.GetCurrentSteamName();
            }
            else
            {
                Log.Error("[MTN] Steam authentication not available");
            }
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            // Clean up any active auth tickets when settings are saved
            SteamAuthUtils.CleanupAuthTicket();
        }

        public override string SettingsCategory()
        {
            return "MTN Server Settings";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            MTNSettings.DoSettingsWindowContents(inRect);
        }
    }
}
