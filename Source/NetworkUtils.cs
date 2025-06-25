#pragma warning disable 0649 // Suppress warning about 'forsale' field being unassigned

using UnityEngine.Networking;
using Verse;
using MTN;
using System.Collections.Generic;
using System;
using UnityEngine;

namespace MTN
{
    public static class NetworkUtils
    {
        private static string GetServerUrl(string endpoint)
        {
            string baseUrl = MTNSettings.serverUrl.TrimEnd('/');
            return $"{baseUrl}/{endpoint.TrimStart('/')}";
        }

        private static void HandleSuccessfulResponse()
        {
            // Renew JWT token expiration on successful API calls
            SteamAuthUtils.UpdateJwtTokenExpiration();
        }

        private static void HandleAuthError(System.Action retryAction, System.Action<string> onError, string error)
        {
            // Check if it's a 401 error (token expired)
            if (error.Contains("401") || error.Contains("Unauthorized"))
            {
                Log.Warning("[MTN] JWT token appears to be expired, attempting to refresh...");
                
                // Clear the expired token
                SteamAuthUtils.ClearExpiredToken();
                
                // Try to re-authenticate
                SteamAuthUtils.InitAuthTicket();
                
                // Wait a moment for authentication to complete, then retry
                LongEventHandler.QueueLongEvent(() => 
                {
                    if (SteamAuthUtils.HasValidJwtToken)
                    {
                        Log.Message("[MTN] Token refreshed, retrying request...");
                        retryAction?.Invoke();
                    }
                    else
                    {
                        Log.Error("[MTN] Failed to refresh token, cannot retry request");
                        onError?.Invoke("Authentication failed - please restart RimWorld");
                    }
                }, "MTN_Token_Refresh", false, null);
            }
            else
            {
                // Not an auth error, pass through to original error handler
                onError?.Invoke(error);
            }
        }

        public static void SendSalesToServer(string json)
        {
            string url = GetServerUrl("/trade");
            var request = new UnityWebRequest(url, "POST");
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            string jwtToken = SteamAuthUtils.GetJwtToken();
            if (!string.IsNullOrEmpty(jwtToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {jwtToken}");
            }
            var asyncOp = request.SendWebRequest();
            asyncOp.completed += (op) =>
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    Log.Message("[MTN] Successfully sent sales to server");
                    HandleSuccessfulResponse();
                }
                else
                {
                    Log.Error("[MTN] Failed to send sales to server: " + request.error);
                }
                request.Dispose();
            };
        }

        public static void SendAuthRequest(string json, System.Action<string> onSuccess, System.Action<string> onError)
        {
            string url = GetServerUrl("/auth/login");
            var request = new UnityWebRequest(url, "POST");
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            // Send the request asynchronously
            var asyncOp = request.SendWebRequest();
            asyncOp.completed += (op) =>
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    onSuccess?.Invoke(request.downloadHandler.text);
                }
                else
                {
                    onError?.Invoke(request.error);
                }
                request.Dispose();
            };
        }

        public static void FetchItemsForSale(System.Action<TradeRecord[]> onSuccess, System.Action<string> onError)
        {
            void MakeRequest()
            {
                string url = GetServerUrl("/forsale");
                var request = UnityWebRequest.Get(url);
                request.downloadHandler = new DownloadHandlerBuffer();
                
                string jwtToken = SteamAuthUtils.GetJwtToken();
                if (!string.IsNullOrEmpty(jwtToken))
                {
                    request.SetRequestHeader("Authorization", $"Bearer {jwtToken}");
                }
                var asyncOp = request.SendWebRequest();
                asyncOp.completed += (op) =>
                {
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            string json = request.downloadHandler.text;
                            json = json.Trim();
                            Log.Message($"[MTN] Received JSON from server: {json}");
                            
                            try
                            {
                                Log.Message($"[MTN] Parsing JSON manually: {json}");
                                
                                TradeRecord[] records = JsonParser.ParseTradeRecords(json);
                                HandleSuccessfulResponse();
                                onSuccess?.Invoke(records);
                            }
                            catch (System.Exception ex)
                            {
                                Log.Error($"[MTN] Failed to parse items for sale: {ex.Message}");
                                onError?.Invoke($"Failed to parse items for sale: {ex.Message}");
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Log.Error($"[MTN] Exception parsing items for sale: {ex.Message}");
                            onError?.Invoke($"Failed to parse items for sale: {ex.Message}");
                        }
                    }
                    else
                    {
                        Log.Error("[MTN] FetchItemsForSale: HTTP error " + request.error);
                        HandleAuthError(MakeRequest, onError, request.error);
                    }
                    request.Dispose();
                };
            }
            
            MakeRequest();
        }

        public static void SendTradeRequest(string json, System.Action<string> onSuccess, System.Action<string> onError)
        {
            string url = GetServerUrl("/trade");
            var request = new UnityWebRequest(url, "POST");
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            string jwtToken = SteamAuthUtils.GetJwtToken();
            if (!string.IsNullOrEmpty(jwtToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {jwtToken}");
            }
            var asyncOp = request.SendWebRequest();
            asyncOp.completed += (op) =>
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    HandleSuccessfulResponse();
                    onSuccess?.Invoke(request.downloadHandler.text);
                }
                else
                {
                    Log.Error("[MTN] SendTradeRequest: HTTP error " + request.error);
                    onError?.Invoke(request.error);
                }
                request.Dispose();
            };
        }

        public static void SendBuyRequest(string json, System.Action<string> onSuccess, System.Action<string> onError)
        {
            void MakeRequest()
            {
                string url = GetServerUrl("/buy");
                var request = new UnityWebRequest(url, "POST");
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                string jwtToken = SteamAuthUtils.GetJwtToken();
                if (!string.IsNullOrEmpty(jwtToken))
                {
                    request.SetRequestHeader("Authorization", $"Bearer {jwtToken}");
                }
                var asyncOp = request.SendWebRequest();
                asyncOp.completed += (op) =>
                {
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        HandleSuccessfulResponse();
                        onSuccess?.Invoke(request.downloadHandler.text);
                    }
                    else
                    {
                        Log.Error("[MTN] SendBuyRequest: HTTP error " + request.error);
                        HandleAuthError(MakeRequest, onError, request.error);
                    }
                    request.Dispose();
                };
            }
            
            MakeRequest();
        }

        public static void FetchPendingSales(System.Action<string> onSuccess, System.Action<string> onError)
        {
            string url = GetServerUrl("/sales/pending");
            var request = UnityWebRequest.Get(url);
            request.downloadHandler = new DownloadHandlerBuffer();
            
            // Add JWT authentication header if available
            string jwtToken = SteamAuthUtils.GetJwtToken();
            if (!string.IsNullOrEmpty(jwtToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {jwtToken}");
            }
            
            var asyncOp = request.SendWebRequest();
            asyncOp.completed += (op) =>
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    HandleSuccessfulResponse();
                    onSuccess?.Invoke(request.downloadHandler.text);
                }
                else
                {
                    onError?.Invoke(request.error);
                }
                request.Dispose();
            };
        }

        public static void ClaimPendingSales(System.Action<string> onSuccess, System.Action<string> onError)
        {
            string url = GetServerUrl("/sales/claim");
            var request = new UnityWebRequest(url, "POST");
            
            // Send an empty JSON object to satisfy server requirements for POST request
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes("{}");
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            
            // Add JWT authentication header if available
            string jwtToken = SteamAuthUtils.GetJwtToken();
            if (!string.IsNullOrEmpty(jwtToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {jwtToken}");
            }
            
            var asyncOp = request.SendWebRequest();
            asyncOp.completed += (op) =>
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    onSuccess?.Invoke(request.downloadHandler.text);
                }
                else
                {
                    onError?.Invoke(request.error);
                }
                request.Dispose();
            };
        }
    }
}

#pragma warning restore 0649
