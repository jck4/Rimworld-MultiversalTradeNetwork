using System;
using UnityEngine;

namespace MTN
{
    [Serializable]
    public class TradeRecord
    {
        public string DefName;
        public int Quantity;
        public int Price;
        public string PlayerName;
        public string Quality;

        public TradeRecord() 
        {
            // Initialize string fields to prevent null serialization issues
            DefName = "";
            PlayerName = "";
            Quality = "";
        }
    }

    [Serializable]
    public class TradeRecordList
    {
        public TradeRecord[] items;

        public string ToJson()
        {
            return JsonUtility.ToJson(this);
        }

        public static TradeRecordList FromJson(string json)
        {
            return JsonUtility.FromJson<TradeRecordList>(json);
        }
    }

    [Serializable]
    public class ClaimSalesResponse
    {
        public string status;
        public int total_claimed;
        public int claimed_sales_count;
    }

    // Add additional model classes here as needed, using the same pattern.
} 