using Steamworks;
using Verse;
using System;
using System.Text;
using UnityEngine;
using System.Collections.Generic; // Required for Dictionary
using System.IO;

namespace MTN
{
    [StaticConstructorOnStartup]
    public static class SteamAuthUtils
    {
        private static HAuthTicket currentAuthTicket = HAuthTicket.Invalid;
        private static byte[] authTicketData = new byte[1024];
        private static uint authTicketSize = 0;
        private static string cachedAuthTicket = null;
        private static string cachedJwtToken = null;
        private static DateTime jwtExpiryTime = DateTime.MinValue;
        
        // File path for caching JWT token
        private static string JwtCacheFilePath => Path.Combine(GenFilePaths.SaveDataFolderPath, "MTN_JWT_Cache.txt");

        static SteamAuthUtils()
        {
            // Load cached JWT token on startup
            LoadCachedJwtToken();
        }

        public static void InitAuthTicket()
        {
            // Check if we already have a valid cached JWT token
            if (!string.IsNullOrEmpty(cachedJwtToken) && DateTime.UtcNow < jwtExpiryTime)
            {
                Log.Message("[MTN] Using cached JWT token, no need to re-authenticate");
                return;
            }

            if (!IsSteamAvailable())
            {
                Log.Error("[MTN] Steam is not available. Cannot initialize auth.");
                cachedAuthTicket = null;
                return;
            }

            try
            {
                // Cancel any existing ticket first
                if (currentAuthTicket != HAuthTicket.Invalid)
                {
                    SteamUser.CancelAuthTicket(currentAuthTicket);
                    currentAuthTicket = HAuthTicket.Invalid;
                    authTicketData = new byte[1024];
                    authTicketSize = 0;
                }

                // Get new auth ticket
                currentAuthTicket = SteamUser.GetAuthSessionTicket(authTicketData, authTicketData.Length, out authTicketSize);

                if (currentAuthTicket == HAuthTicket.Invalid)
                {
                    Log.Error("[MTN] Failed to get Steam auth ticket");
                    cachedAuthTicket = null;
                    return;
                }

                // Convert ticket to hex string (not base64)
                cachedAuthTicket = BitConverter.ToString(authTicketData, 0, (int)authTicketSize).Replace("-", "").ToLower();
                // Authenticate with server and get JWT token
                AuthenticateWithServer();
            }
            catch (Exception ex)
            {
                Log.Error($"[MTN] Exception while getting Steam auth ticket: {ex.Message}");
                cachedAuthTicket = null;
            }
        }

        private static void AuthenticateWithServer()
        {
            if (string.IsNullOrEmpty(cachedAuthTicket))
            {
                Log.Warning("[MTN] No auth ticket available for server authentication");
                return;
            }

            try
            {
                string playerName = GetCurrentSteamName();
                string loginData = $"{{\"authTicket\":\"{cachedAuthTicket}\",\"playerName\":\"{playerName}\"}}";
                
                Log.Message("[MTN] Authenticating with server...");
                
                // Send authentication request to server with retry logic
                SendAuthRequestWithRetry(loginData, 0);
            }
            catch (Exception ex)
            {
                Log.Error($"[MTN] Exception during server authentication: {ex.Message}");
            }
        }

        private static void SendAuthRequestWithRetry(string loginData, int attempt)
        {
            const int maxAttempts = 3;
            const float retryDelaySeconds = 2.0f;

            NetworkUtils.SendAuthRequest(loginData, (response) =>
            {
                try
                {
                    // Parse JWT response
                    if (response.Contains("\"token\""))
                    {
                        // Extract token from response
                        int tokenStart = response.IndexOf("\"token\":\"") + 9;
                        int tokenEnd = response.IndexOf("\"", tokenStart);
                        if (tokenStart > 8 && tokenEnd > tokenStart)
                        {
                            cachedJwtToken = response.Substring(tokenStart, tokenEnd - tokenStart);
                            
                            // Extract expiry time (24 hours from now)
                            jwtExpiryTime = DateTime.UtcNow.AddHours(24);
                            
                            Log.Message($"[MTN] Successfully authenticated with server. JWT token cached.");
                            Log.Message($"[MTN] Token expires: {jwtExpiryTime}");

                            // Save JWT token to file for caching
                            SaveJwtTokenToFile(cachedJwtToken);
                        }
                    }
                    else
                    {
                        Log.Warning("[MTN] Server authentication failed - no token in response");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[MTN] Error parsing server authentication response: {ex.Message}");
                }
            }, (error) =>
            {
                if (attempt < maxAttempts - 1)
                {
                    Log.Warning($"[MTN] Authentication attempt {attempt + 1} failed: {error}. Retrying in {retryDelaySeconds} seconds...");
                    // Schedule retry with delay
                    LongEventHandler.QueueLongEvent(() => 
                    {
                        SendAuthRequestWithRetry(loginData, attempt + 1);
                    }, "MTN_Auth_Retry", false, null);
                }
                else
                {
                    Log.Error($"[MTN] Server authentication failed after {maxAttempts} attempts: {error}");
                }
            });
        }

        public static string GetSteamAuthTicket()
        {
            return cachedAuthTicket;
        }

        public static string GetJwtToken()
        {
            // Check if we have a valid cached token
            if (!string.IsNullOrEmpty(cachedJwtToken) && DateTime.UtcNow < jwtExpiryTime)
            {
                return cachedJwtToken;
            }
            
            // Token is expired or missing, re-authenticate
            if (DateTime.UtcNow >= jwtExpiryTime)
            {
                Log.Warning("[MTN] JWT token expired, re-authenticating...");
            }
            else
            {
                Log.Warning("[MTN] No valid JWT token found, authenticating...");
            }
            
            cachedJwtToken = null;
            InitAuthTicket(); // This will trigger re-authentication
            return null;
        }

        public static bool HasAuthTicket => !string.IsNullOrEmpty(cachedAuthTicket);

        public static bool HasValidJwtToken => !string.IsNullOrEmpty(cachedJwtToken) && DateTime.UtcNow < jwtExpiryTime;

        public static CSteamID GetCurrentSteamID()
        {
            if (!IsSteamAvailable())
            {
                Log.Error("[MTN] Steam is not available. Cannot get Steam ID.");
                return CSteamID.Nil;
            }

            return SteamUser.GetSteamID();
        }

        public static string GetCurrentSteamName()
        {
            if (!IsSteamAvailable())
            {
                Log.Error("[MTN] Steam is not available. Cannot get Steam name.");
                return "Unknown";
            }

            return SteamUtility.SteamPersonaName;
        }

        public static void CleanupAuthTicket()
        {
            if (currentAuthTicket != HAuthTicket.Invalid)
            {
                SteamUser.CancelAuthTicket(currentAuthTicket);
                currentAuthTicket = HAuthTicket.Invalid;
                authTicketData = new byte[1024];
                authTicketSize = 0;
                cachedAuthTicket = null;
                cachedJwtToken = null;
                jwtExpiryTime = DateTime.MinValue;
                Log.Message("[MTN] Auth ticket and JWT token cleaned up");
            }
        }

        public static bool IsSteamAvailable()
        {
            try
            {
                var steamID = SteamUser.GetSteamID();
                return steamID != CSteamID.Nil;
            }
            catch
            {
                return false;
            }
        }

        private static void SaveJwtTokenToFile(string token)
        {
            try
            {
                long expirationTimestamp = (long)(DateTime.UtcNow.AddHours(24) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                string jsonResponse = $"{{\"token\":\"{token}\",\"expires_at\":{expirationTimestamp}}}";
                File.WriteAllText(JwtCacheFilePath, jsonResponse);
                Log.Message("[MTN] JWT token cached to file");
            }
            catch (Exception ex)
            {
                Log.Error($"[MTN] Error saving JWT token to file: {ex.Message}");
            }
        }

        private static void LoadCachedJwtToken()
        {
            if (File.Exists(JwtCacheFilePath))
            {
                try
                {
                    string cachedData = File.ReadAllText(JwtCacheFilePath);
                    if (!string.IsNullOrEmpty(cachedData))
                    {
                        // Parse JWT token
                        int tokenStart = cachedData.IndexOf("\"token\":\"") + 9;
                        int tokenEnd = cachedData.IndexOf("\"", tokenStart);
                        if (tokenStart > 8 && tokenEnd > tokenStart)
                        {
                            cachedJwtToken = cachedData.Substring(tokenStart, tokenEnd - tokenStart);
                            
                            // Parse expiration timestamp
                            int expiresStart = cachedData.IndexOf("\"expires_at\":") + 12;
                            int expiresEnd = cachedData.IndexOf("}", expiresStart);
                            if (expiresStart > 11 && expiresEnd > expiresStart)
                            {
                                string expiresStr = cachedData.Substring(expiresStart, expiresEnd - expiresStart);
                                if (long.TryParse(expiresStr, out long expirationTimestamp))
                                {
                                    // Convert Unix timestamp to DateTime
                                    jwtExpiryTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expirationTimestamp);
                                    
                                    // Check if token is still valid
                                    if (DateTime.UtcNow < jwtExpiryTime)
                                    {
                                        Log.Message($"[MTN] Successfully loaded cached JWT token.");
                                        Log.Message($"[MTN] Token expires: {jwtExpiryTime}");
                                    }
                                    else
                                    {
                                        Log.Warning("[MTN] Cached JWT token has expired, clearing...");
                                        ClearExpiredToken();
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[MTN] Error loading cached JWT token: {ex.Message}");
                }
            }
        }

        public static void UpdateJwtTokenExpiration()
        {
            if (!string.IsNullOrEmpty(cachedJwtToken))
            {
                // Extend expiration by 24 hours from now
                jwtExpiryTime = DateTime.UtcNow.AddHours(24);
                
                // Update the cached file with new expiration
                SaveJwtTokenToFile(cachedJwtToken);
                
                Log.Message($"[MTN] JWT token expiration renewed. New expiry: {jwtExpiryTime}");
            }
        }

        public static void ClearExpiredToken()
        {
            cachedJwtToken = null;
            jwtExpiryTime = DateTime.MinValue;
            
            try
            {
                if (File.Exists(JwtCacheFilePath))
                {
                    File.Delete(JwtCacheFilePath);
                    Log.Message("[MTN] Cleared expired JWT token from cache");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MTN] Error clearing expired JWT token: {ex.Message}");
            }
        }
    }
} 