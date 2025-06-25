using System.Collections.Generic;
using RimWorld;
using Verse;
using System.Linq;
using UnityEngine;
using Steamworks;
using Verse.Steam;
using MTN;

namespace MTN
{
    public static class TradeUtils
    {
        public static List<ThingDef> GetSellableCoreMaterialDefs(Map map)
        {
            var result = new List<ThingDef>();
            var things = map.listerThings.AllThings;
            var grouped = things
                .Where(t => t.def != ThingDefOf.Silver &&
                            t.def.category == ThingCategory.Item &&
                            !t.def.IsCorpse && !t.def.IsFilth)
                .GroupBy(t => t.def);
            foreach (var group in grouped)
            {
                result.Add(group.Key);
            }
            return result;
        }

        public static List<TradeRecord> GetColonySellables()
        {
            Dictionary<string, TradeRecord> aggregatedItems = new Dictionary<string, TradeRecord>();
            foreach (Map map in Find.Maps)
            {
                foreach (Thing item in map.listerThings.AllThings)
                {
                    if (item.def.tradeability.PlayerCanSell() && item.def.category == ThingCategory.Item && !item.IsForbidden(Faction.OfPlayer))
                    {
                        string defName = item.def.defName;
                        if (aggregatedItems.ContainsKey(defName))
                        {
                            aggregatedItems[defName].Quantity += item.stackCount;
                        }
                        else
                        {
                            aggregatedItems[defName] = new TradeRecord
                            {
                                DefName = defName,
                                Quantity = item.stackCount,
                                Price = (int)item.MarketValue,
                                Quality = item.TryGetQuality(out QualityCategory quality) ? quality.ToString() : ""
                            };
                        }
                    }
                }
            }
            return aggregatedItems.Values.ToList();
        }

        public static void FetchTraderStock(System.Action<TradeRecord[]> onSuccess, System.Action<string> onError)
        {
            NetworkUtils.FetchItemsForSale(
                (TradeRecord[] records) => {
                    onSuccess?.Invoke(records);
                },
                (string error) => {
                    onError?.Invoke(error);
                }
            );
        }

        public static string SerializeSellableItems(List<TradeRecord> allItems, Dictionary<TradeRecord, int> amounts, Dictionary<TradeRecord, float> prices)
        {
            var selectedItems = new List<TradeRecord>();
            foreach (var item in allItems)
            {
                if (amounts.ContainsKey(item) && amounts[item] > 0)
                {
                    var selectedItem = new TradeRecord
                    {
                        DefName = item.DefName,
                        Quantity = amounts[item],
                        Price = Mathf.RoundToInt(prices[item]),
                        PlayerName = item.PlayerName,
                        Quality = item.Quality
                    };
                    selectedItems.Add(selectedItem);
                }
            }
            // Create payload with just the items (JWT auth is in headers)
            var payload = new TradeRecordList
            {
                items = selectedItems.ToArray()
            };
            return UnityEngine.JsonUtility.ToJson(payload);
        }

        public static void DeleteSoldItems(Dictionary<TradeRecord, int> soldItems)
        {
            foreach (var soldItemEntry in soldItems)
            {
                int amountToDelete = soldItemEntry.Value;
                if (amountToDelete <= 0) continue;
                TradeRecord record = soldItemEntry.Key;
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(record.DefName);
                if (def == null)
                {
                    Log.Warning($"[MTN] Could not find ThingDef for {record.DefName}. Skipping.");
                    continue;
                }
                int remainingAmount = amountToDelete;
                foreach (Map map in Find.Maps)
                {
                    if (remainingAmount <= 0) break;
                    List<Thing> thingsOnMap = new List<Thing>(map.listerThings.ThingsOfDef(def));
                    foreach (Thing thing in thingsOnMap)
                    {
                        if (thing.IsForbidden(Faction.OfPlayer))
                        {
                            continue;
                        }
                        int amountToTake = Mathf.Min(remainingAmount, thing.stackCount);
                        thing.SplitOff(amountToTake).Destroy(DestroyMode.Vanish);
                        remainingAmount -= amountToTake;
                        if (remainingAmount <= 0)
                        {
                            break;
                        }
                    }
                }
                if (remainingAmount > 0)
                {
                    Log.Warning($"[MTN] Could not delete all items. {remainingAmount} of {record.DefName} remained.");
                }
            }
        }

        public static void DeliverBoughtItems(Dictionary<TradeRecord, int> boughtItems)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                Log.Error("[MTN] Cannot deliver bought items, current map is null.");
                return;
            }
            IntVec3 dropSpot = DropCellFinder.RandomDropSpot(map);
            List<Thing> thingsToDrop = new List<Thing>();
            foreach (var boughtItemEntry in boughtItems)
            {
                int amountToDrop = boughtItemEntry.Value;
                if (amountToDrop <= 0) continue;
                TradeRecord record = boughtItemEntry.Key;
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(record.DefName);
                if (def == null)
                {
                    Log.Warning($"[MTN] Could not find ThingDef for {record.DefName}. Skipping.");
                    continue;
                }
                int remainingAmount = amountToDrop;
                while (remainingAmount > 0)
                {
                    Thing item = ThingMaker.MakeThing(def, null);
                    int amountInStack = Mathf.Min(remainingAmount, def.stackLimit);
                    item.stackCount = amountInStack;
                    thingsToDrop.Add(item);
                    remainingAmount -= amountInStack;
                }
            }
            if (thingsToDrop.Any())
            {
                DropPodUtility.DropThingsNear(dropSpot, map, thingsToDrop);
                Messages.Message("TradeGoodsArrived", MessageTypeDefOf.PositiveEvent, false);
            }
            else
            {
                // No items to drop, no log needed
            }
        }

        private static string SerializeToJson(object obj)
        {
            try
            {
                return UnityEngine.JsonUtility.ToJson(obj);
            }
            catch (System.Exception ex)
            {
                Log.Error($"[MTN] JSON serialization failed: {ex.Message}");
                return null;
            }
        }

        public static int GetColonySilver()
        {
            int totalSilver = 0;
            foreach (Map map in Find.Maps)
            {
                ThingDef silverDef = DefDatabase<ThingDef>.GetNamedSilentFail("Silver");
                if (silverDef == null) continue;
                foreach (Thing silverStack in map.listerThings.ThingsOfDef(silverDef))
                {
                    if (!silverStack.IsForbidden(Faction.OfPlayer))
                        totalSilver += silverStack.stackCount;
                }
            }
            return totalSilver;
        }

        public static void BuyItems(List<TradeRecord> itemsToBuy, System.Action<string> onSuccess, System.Action<string> onError)
        {
            if (itemsToBuy == null || itemsToBuy.Count == 0)
            {
                onError?.Invoke("No items specified for purchase");
                return;
            }
            int totalCost = 0;
            foreach (var item in itemsToBuy)
            {
                totalCost += item.Price * item.Quantity;
            }
            int colonySilver = GetColonySilver();
            if (colonySilver < totalCost)
            {
                Messages.Message($"Not enough silver! Need {totalCost}, but only have {colonySilver}.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            
            // Send item identifiers instead of indices to prevent race conditions
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"items\":[");
            for (int i = 0; i < itemsToBuy.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var item = itemsToBuy[i];
                // Escape quotes in seller name to prevent JSON parsing issues
                string escapedSellerName = item.PlayerName?.Replace("\"", "\\\"") ?? "";
                sb.Append($"{{\"def_name\":\"{item.DefName}\",\"quantity\":{item.Quantity},\"seller_name\":\"{escapedSellerName}\"}}");
            }
            sb.Append($"],\"client_silver\":{colonySilver}");
            sb.Append("}");
            string json = sb.ToString();
            
            Log.Message($"[MTN] Sending buy request JSON: {json}");
            NetworkUtils.SendBuyRequest(json, onSuccess, onError);
        }

        public static void SellItems(List<TradeRecord> itemsToSell, System.Action<string> onSuccess, System.Action<string> onError)
        {
            if (itemsToSell == null || itemsToSell.Count == 0)
            {
                onError?.Invoke("No items to sell");
                return;
            }
            var list = new TradeRecordList { items = itemsToSell.ToArray() };
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"records\":[");
            for (int i = 0; i < list.items.Length; i++)
            {
                if (i > 0) sb.Append(",");
                var record = list.items[i];
                sb.Append($"{{\"DefName\":\"{record.DefName}\",\"Quantity\":{record.Quantity},\"Price\":{record.Price},\"PlayerName\":\"{record.PlayerName}\",\"Quality\":\"{record.Quality}\"}}");
            }
            sb.Append("]}");
            string json = sb.ToString();
            NetworkUtils.SendTradeRequest(json, onSuccess, onError);
        }

        public static void FetchAndClaimSales(System.Action<int> onSilverClaimed, System.Action<string> onError)
        {
            NetworkUtils.ClaimPendingSales(
                (success) => {
                    try
                    {
                        Log.Message($"[MTN] Claim sales response: {success}");
                        
                        ClaimSalesResponse response = JsonParser.ParseClaimSalesResponse(success);
                        
                        if (response != null && response.status == "success")
                        {
                            if (response.total_claimed > 0)
                            {
                                Log.Message($"[MTN] Successfully claimed {response.total_claimed} silver from {response.claimed_sales_count} sales");
                                DeliverSilverToColony(response.total_claimed);
                                onSilverClaimed?.Invoke(response.total_claimed);
                            }
                            else
                            {
                                Log.Message("[MTN] No pending sales to claim");
                                onSilverClaimed?.Invoke(0);
                            }
                        }
                        else
                        {
                            Log.Warning($"[MTN] Claim sales failed or returned unexpected response: {success}");
                            onError?.Invoke("Failed to claim sales: Invalid response");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error($"[MTN] Failed to process claim sales response: {ex.Message}");
                        onError?.Invoke($"Failed to process claim sales: {ex.Message}");
                    }
                },
                (error) => {
                    Log.Error($"[MTN] Claim sales network error: {error}");
                    onError?.Invoke(error);
                }
            );
        }

        public static void DeliverSilverToColony(int amount)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                Log.Error("[MTN] Cannot deliver silver, current map is null");
                return;
            }
            ThingDef silverDef = DefDatabase<ThingDef>.GetNamedSilentFail("Silver");
            if (silverDef == null)
            {
                Log.Warning("[MTN] Could not find Silver ThingDef");
                return;
            }
            List<Thing> silverToDrop = new List<Thing>();
            int remainingAmount = amount;
            while (remainingAmount > 0)
            {
                Thing silverStack = ThingMaker.MakeThing(silverDef, null);
                int amountInStack = Mathf.Min(remainingAmount, silverDef.stackLimit);
                silverStack.stackCount = amountInStack;
                silverToDrop.Add(silverStack);
                remainingAmount -= amountInStack;
            }
            IntVec3 dropSpot = DropCellFinder.RandomDropSpot(map);
            DropPodUtility.DropThingsNear(dropSpot, map, silverToDrop);
            Messages.Message($"Received {amount} silver from sales", MessageTypeDefOf.PositiveEvent, false);
        }

        public static void RemoveSilverFromColony(int amount)
        {
            if (amount <= 0)
            {
                Log.Warning("[MTN] RemoveSilverFromColony called with zero or negative amount");
                return;
            }
            int remainingAmount = amount;
            int totalRemoved = 0;
            foreach (Map map in Find.Maps)
            {
                if (remainingAmount <= 0) break;
                ThingDef silverDef = DefDatabase<ThingDef>.GetNamedSilentFail("Silver");
                if (silverDef == null)
                {
                    Log.Warning("[MTN] Could not find Silver ThingDef");
                    continue;
                }
                List<Thing> silverStacks = new List<Thing>(map.listerThings.ThingsOfDef(silverDef));
                foreach (Thing silverStack in silverStacks)
                {
                    if (silverStack.IsForbidden(Faction.OfPlayer))
                        continue;
                    int amountToTake = Mathf.Min(remainingAmount, silverStack.stackCount);
                    silverStack.SplitOff(amountToTake).Destroy(DestroyMode.Vanish);
                    remainingAmount -= amountToTake;
                    totalRemoved += amountToTake;
                    if (remainingAmount <= 0)
                        break;
                }
            }
        }
    }
} 