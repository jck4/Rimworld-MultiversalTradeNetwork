using UnityEngine;
using Verse;

namespace MTN
{
    public class MTNSettings : ModSettings
    {
        private static string GetDefaultServerUrl()
        {
            // Use localhost for development mode, production URL for normal mode
            if (Prefs.DevMode)
            {
                return "http://localhost:5000";
            }
            else
            {
                return "https://mtn.jck.sh";
            }
        }

        public static string serverUrl = GetDefaultServerUrl();
        public static bool enableDebugLogging = false;
        public static int requestTimeoutSeconds = 30;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref serverUrl, "serverUrl", GetDefaultServerUrl());
            Scribe_Values.Look(ref enableDebugLogging, "enableDebugLogging", false);
            Scribe_Values.Look(ref requestTimeoutSeconds, "requestTimeoutSeconds", 30);
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("MTN Server Configuration");

            listing.Gap();

            listing.Label("Server URL:");
            serverUrl = listing.TextEntry(serverUrl, 1);
            
            listing.Gap();

            listing.Label("Request Timeout (seconds):");
            requestTimeoutSeconds = (int)listing.Slider(requestTimeoutSeconds, 5, 60);

            listing.Gap();

            listing.CheckboxLabeled("Enable Debug Logging", ref enableDebugLogging, "Log detailed information about network requests and responses");

            listing.Gap();

            if (listing.ButtonText("Reset to Defaults"))
            {
                serverUrl = GetDefaultServerUrl();
                enableDebugLogging = false;
                requestTimeoutSeconds = 30;
            }

            listing.End();
        }
    }
} 