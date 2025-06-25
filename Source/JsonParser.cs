using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MTN
{
    public static class JsonParser
    {
        public static TradeRecord[] ParseTradeRecords(string json)
        {
            try
            {
                Log.Message($"[MTN] Parsing JSON: {json}");
                
                List<TradeRecord> records = new List<TradeRecord>();
                
                // Handle empty array case
                if (json.Contains("\"records\":[]") || json.Contains("\"items\":[]"))
                {
                    Log.Message("[MTN] Detected empty array, returning 0 items");
                    return new TradeRecord[0];
                }
                
                // Find the array start and end
                int arrayStart = json.IndexOf('[');
                int arrayEnd = json.LastIndexOf(']');
                
                if (arrayStart >= 0 && arrayEnd >= 0 && arrayEnd > arrayStart)
                {
                    string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
                    
                    // Split by },{ but handle the first and last items
                    string[] itemStrings = arrayContent.Split(new string[] { "},{" }, StringSplitOptions.None);
                    
                    for (int i = 0; i < itemStrings.Length; i++)
                    {
                        string itemStr = itemStrings[i];
                        // Remove { and } from first and last items
                        if (i == 0) itemStr = itemStr.TrimStart('{');
                        if (i == itemStrings.Length - 1) itemStr = itemStr.TrimEnd('}');
                        
                        TradeRecord record = ParseTradeRecord(itemStr);
                        if (record != null && !string.IsNullOrEmpty(record.DefName))
                        {
                            records.Add(record);
                            Log.Message($"[MTN] Parsed item: {record.DefName} x{record.Quantity} from {record.PlayerName}");
                        }
                    }
                }
                
                Log.Message($"[MTN] Manual parsing successful: {records.Count} items");
                return records.ToArray();
            }
            catch (Exception ex)
            {
                Log.Error($"[MTN] JSON parsing exception: {ex.Message}");
                throw new Exception($"Failed to parse JSON: {ex.Message}");
            }
        }
        
        private static TradeRecord ParseTradeRecord(string itemJson)
        {
            try
            {
                TradeRecord record = new TradeRecord();
                string[] fields = itemJson.Split(',');
                
                foreach (string field in fields)
                {
                    string[] kv = field.Split(':');
                    if (kv.Length == 2)
                    {
                        string key = kv[0].Replace("\"", "").Trim();
                        string value = kv[1].Replace("\"", "").Trim();
                        
                        switch (key)
                        {
                            case "DefName":
                                record.DefName = value;
                                break;
                            case "Quantity":
                                int.TryParse(value, out record.Quantity);
                                break;
                            case "Price":
                                int.TryParse(value, out record.Price);
                                break;
                            case "PlayerName":
                                record.PlayerName = value;
                                break;
                            case "Quality":
                                record.Quality = value;
                                break;
                        }
                    }
                }
                
                return record;
            }
            catch (Exception ex)
            {
                Log.Error($"[MTN] Failed to parse individual record: {ex.Message}");
                return null;
            }
        }

        public static ClaimSalesResponse ParseClaimSalesResponse(string json)
        {
            try
            {
                Log.Message($"[MTN] Parsing claim sales response: {json}");
                
                // Try JsonUtility first
                ClaimSalesResponse response = JsonUtility.FromJson<ClaimSalesResponse>(json);
                if (response != null)
                {
                    Log.Message($"[MTN] JsonUtility parsing successful: {response.total_claimed} silver claimed");
                    return response;
                }
                
                // Fallback to manual parsing
                Log.Warning("[MTN] JsonUtility failed, using manual parsing for claim response");
                
                response = new ClaimSalesResponse();
                
                // Extract status
                if (json.Contains("\"status\":"))
                {
                    int statusStart = json.IndexOf("\"status\":") + 9;
                    int statusEnd = json.IndexOf('"', statusStart + 1);
                    if (statusEnd > statusStart)
                    {
                        response.status = json.Substring(statusStart + 1, statusEnd - statusStart - 1);
                    }
                }
                
                // Extract total_claimed
                if (json.Contains("\"total_claimed\":"))
                {
                    int claimedStart = json.IndexOf("\"total_claimed\":") + 15;
                    int claimedEnd = json.IndexOfAny(new char[] { ',', '}' }, claimedStart);
                    if (claimedEnd > claimedStart)
                    {
                        string claimedStr = json.Substring(claimedStart, claimedEnd - claimedStart).Trim();
                        int.TryParse(claimedStr, out response.total_claimed);
                    }
                }
                
                // Extract claimed_sales_count
                if (json.Contains("\"claimed_sales_count\":"))
                {
                    int countStart = json.IndexOf("\"claimed_sales_count\":") + 21;
                    int countEnd = json.IndexOfAny(new char[] { ',', '}' }, countStart);
                    if (countEnd > countStart)
                    {
                        string countStr = json.Substring(countStart, countEnd - countStart).Trim();
                        int.TryParse(countStr, out response.claimed_sales_count);
                    }
                }
                
                Log.Message($"[MTN] Manual parsing successful: {response.total_claimed} silver claimed");
                return response;
            }
            catch (Exception ex)
            {
                Log.Error($"[MTN] Failed to parse claim sales response: {ex.Message}");
                return new ClaimSalesResponse { status = "error", total_claimed = 0, claimed_sales_count = 0 };
            }
        }
    }
} 