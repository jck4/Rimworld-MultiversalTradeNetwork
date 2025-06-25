using UnityEngine;
using Verse;
using RimWorld;
using System.Collections.Generic;
using MTN;

namespace MTN
{
    public class TradeNetworkWindow : MainTabWindow
    {
        private const float MenuSectionTopOffset = 4f;
        private const float TabVerticalOffset = 0f;
        private const float TabHorizontalOffset = 0f;
        private const float TabWidthPadding = 40f;
        private const float ContentTopOffset = 60f;
        private List<TradeRecord> colonyItems = new List<TradeRecord>();
        private List<TradeRecord> traderItems = new List<TradeRecord>();
        private Dictionary<TradeRecord, int> tradeAmounts = new Dictionary<TradeRecord, int>();
        private Dictionary<TradeRecord, float> tradePrices = new Dictionary<TradeRecord, float>();
        private Dictionary<TradeRecord, string> tradePriceBuffers = new Dictionary<TradeRecord, string>();
        private bool isSellDialog = true; 
        private List<TabRecord> tabs = new List<TabRecord>();
        private enum TradeTab : byte { Sell, Buy }
        private TradeTab curTab = TradeTab.Sell;
        private Vector2 scrollPosition_sell = Vector2.zero;
        private Vector2 scrollPosition_buy = Vector2.zero;

        public TradeNetworkWindow()
        {
            this.forcePause = true;
            this.resizeable = false;
            this.draggable = false; // MainTabWindows are not draggable
            this.absorbInputAroundWindow = false;
            RefreshSellList();
        }

        public override Vector2 InitialSize => new Vector2(880f, 650f);
        public override Vector2 RequestedTabSize => new Vector2(880f, 650f);

        public override void PreOpen()
        {
            base.PreOpen();
            tabs.Clear();
            tabs.Add(new TabRecord("Sell", delegate { curTab = TradeTab.Sell; isSellDialog = true; RefreshSellList(); }, () => curTab == TradeTab.Sell));
            tabs.Add(new TabRecord("Buy", delegate { curTab = TradeTab.Buy; isSellDialog = false; RefreshBuyList(); }, () => curTab == TradeTab.Buy));
        }

        public override void DoWindowContents(Rect rect)
        {
            // Tab area at the very top
            Rect tabRect = rect;
            tabRect.yMin += 32f;
            tabRect.yMax -= 32f;
            tabRect.height = 32f;
            TabDrawer.DrawTabs(tabRect, tabs);
            // Menu section starts just below the tabs
            Rect menuRect = rect;
            menuRect.yMin = tabRect.yMax -32f;
            Widgets.DrawMenuSection(menuRect);
            // Content area inside the menu section
            Rect contentRect = menuRect;
            contentRect.yMin += 8f;
            if (isSellDialog)
                DrawSellWindow(contentRect, ref scrollPosition_sell);
            else
                DrawBuyWindow(contentRect, ref scrollPosition_buy);
        }

        private void DrawSellWindow(Rect inRect, ref Vector2 scrollPosition)
        {
            try
            {
                float footerHeight = 60f;
                float listHeight = inRect.height - 35f - footerHeight;
                Text.Font = GameFont.Small;
                float currentX = inRect.x;
                Widgets.Label(new Rect(currentX + 5f, inRect.y, 220f, 30f), "Item");
                currentX += 220f;
                Widgets.Label(new Rect(currentX, inRect.y, 100f, 30f), "Available");
                currentX += 100f;
                Widgets.Label(new Rect(currentX, inRect.y, 100f, 30f), "Market Value");
                currentX += 100f;
                Widgets.Label(new Rect(currentX, inRect.y, 100f, 30f), "Your Price");
                currentX += 100f;
                Widgets.Label(new Rect(currentX, inRect.y, 200f, 30f), "Sell Amount");

                Rect scrollRect = new Rect(inRect.x, inRect.y + 35f, inRect.width, listHeight);
                Widgets.DrawBoxSolid(scrollRect, new Color(0.18f, 0.18f, 0.18f, 0.7f));
                float rowHeight = 40f;
                int maxRows = colonyItems?.Count ?? 0;
                Rect viewRect = new Rect(0f, 0f, scrollRect.width - 16f, maxRows * rowHeight);

                Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);
                float curY = 0f;
                if (colonyItems != null)
                {
                    foreach (var item in colonyItems)
                    {
                        if (item != null)
                        {
                            Rect rowRect = new Rect(0f, curY, viewRect.width, rowHeight);
                            DrawTradeRow(rowRect, item);
                            curY += rowHeight;
                        }
                    }
                }
                Widgets.EndScrollView();

                // Footer
                float footerY = inRect.y + inRect.height - footerHeight;
                float buttonWidth = 120f;

                // Claim Sales button
                Rect claimButtonRect = new Rect(inRect.x + 10f, footerY, buttonWidth, 40f);
                if (Widgets.ButtonText(claimButtonRect, "Claim Sales"))
                  {
                    TradeUtils.FetchAndClaimSales(
                        (int silverClaimed) => {
                            if (silverClaimed > 0)
                            {
                                Messages.Message($"Successfully claimed {silverClaimed} silver!", MessageTypeDefOf.PositiveEvent, false);
                            }
                            else
                            {
                                Messages.Message("No pending sales to claim", MessageTypeDefOf.NeutralEvent, false);
                            }
                        },
                        (string error) => {
                            Messages.Message("Failed to claim sales", MessageTypeDefOf.RejectInput, false);
                        }
                    );
                }
                TooltipHandler.TipRegion(claimButtonRect, "Claim pending sales and receive silver");

                // Sell button
                Rect sellButtonRect = new Rect(inRect.x + inRect.width - buttonWidth - 10f, footerY, buttonWidth, 40f);
                if (Widgets.ButtonText(sellButtonRect, "Sell"))
                {
                    try
                    {
                        // Create list of items to sell
                        List<TradeRecord> itemsToSell = new List<TradeRecord>();
                        foreach (var kvp in tradeAmounts)
                        {
                            if (kvp.Value > 0)
                            {
                                var item = kvp.Key;
                                var sellRecord = new TradeRecord
                                {
                                    DefName = item.DefName,
                                    Quantity = kvp.Value,
                                    Price = (int)(tradePrices.ContainsKey(item) ? tradePrices[item] : item.Price),
                                    // Don't include PlayerID or PlayerName - server will fill from JWT
                                    Quality = item.Quality
                                };
                                itemsToSell.Add(sellRecord);
                            }
                        }

                        if (itemsToSell.Count > 0)
                        {
                            TradeUtils.SellItems(itemsToSell, 
                                (success) => {
                                    TradeUtils.DeleteSoldItems(tradeAmounts);
                                    Messages.Message("TradeAcceptedMessage", MessageTypeDefOf.PositiveEvent, false);
                                    Close();
                                },
                                (error) => {
                                    Log.Error($"[MTN] Error during sell operation: {error}");
                                    Messages.Message("TradeError", MessageTypeDefOf.RejectInput, false);
                                }
                            );
                        }
                        else
                        {
                            Messages.Message("No items selected for sale", MessageTypeDefOf.RejectInput, false);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error($"[MTN] Error during sell operation: {ex.Message}");
                        Messages.Message("TradeError", MessageTypeDefOf.RejectInput, false);
                    }
                }
                TooltipHandler.TipRegion(sellButtonRect, "Sell Button");
            }
            catch (System.Exception ex)
            {
                Log.Error($"[MTN] Error loading sell window: {ex.Message}");
                Widgets.Label(new Rect(inRect.x + 10f, inRect.y + 10f, inRect.width - 20f, 30f), "Error loading sell window");
            }
        }

        private void DrawBuyWindow(Rect inRect, ref Vector2 scrollPosition)
        {
            try
            {
                float footerHeight = 60f;
                float listHeight = inRect.height - 35f - footerHeight; 
                // Column Headers
                Text.Font = GameFont.Small;
                float currentX = inRect.x;
                Widgets.Label(new Rect(currentX + 5f, inRect.y, 220f, 30f), "Item");
                currentX += 220f;
                Widgets.Label(new Rect(currentX, inRect.y, 100f, 30f), "Price");
                currentX += 100f;
                Widgets.Label(new Rect(currentX, inRect.y, 100f, 30f), "Available");
                currentX += 100f;
                Widgets.Label(new Rect(currentX, inRect.y, 100f, 30f), "Seller");
                currentX += 100f;
                Widgets.Label(new Rect(currentX, inRect.y, 200f, 30f), "Buy Amount");
                // Scroll Area
                Rect scrollRect = new Rect(inRect.x, inRect.y + 35f, inRect.width, listHeight);
                Widgets.DrawBoxSolid(scrollRect, new Color(0.18f, 0.18f, 0.18f, 0.7f));
                float rowHeight = 40f;
                int maxRows = traderItems?.Count ?? 0;
                Rect viewRect = new Rect(0f, 0f, scrollRect.width - 16f, maxRows * rowHeight);
                Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);
                float curY = 0f;
                if (traderItems != null)
                {
                    foreach (var item in traderItems)
                    {
                        if (item != null)
                        {
                            if ((int)(curY / rowHeight) % 2 == 0)
                                Widgets.DrawBoxSolid(new Rect(0f, curY, viewRect.width, rowHeight), new Color(0.22f, 0.22f, 0.22f, 0.5f));
                            else
                                Widgets.DrawBoxSolid(new Rect(0f, curY, viewRect.width, rowHeight), new Color(0.16f, 0.16f, 0.16f, 0.5f));
                            DrawBuyRow(new Rect(0f, curY, viewRect.width, rowHeight), item);
                            curY += rowHeight;
                        }
                    }
                }
                Widgets.EndScrollView();

                // Footer Buttons
                float footerY = inRect.y + inRect.height - footerHeight;
                float buttonWidth = 100f;

                // Refresh button
                Rect refreshButtonRect = new Rect(inRect.x + 10f, footerY, buttonWidth, 40f);
                if (Widgets.ButtonText(refreshButtonRect, "Refresh"))
                {
                    RefreshBuyList();
                    Messages.Message("Refreshing item list...", MessageTypeDefOf.NeutralEvent, false);
                }
                TooltipHandler.TipRegion(refreshButtonRect, "Refresh the list of items for sale");

                Rect buyButtonRect = new Rect(inRect.x + inRect.width - buttonWidth - 10f, footerY, buttonWidth, 40f);
                if (Widgets.ButtonText(buyButtonRect, "Buy"))
                {
                    try
                    {
                        Log.Message("[MTN] Buy button clicked - starting buy process");
                        
                        // Create list of items to buy
                        List<TradeRecord> itemsToBuy = new List<TradeRecord>();
                        bool hasInvalidQuantities = false;
                        
                        // Log the current trade amounts
                        Log.Message($"[MTN] Current trade amounts: {tradeAmounts.Count} items selected");
                        foreach (var kvp in tradeAmounts)
                        {
                            if (kvp.Value > 0)
                            {
                                Log.Message($"[MTN] Selected: {kvp.Key.DefName} x{kvp.Value} from {kvp.Key.PlayerName}");
                            }
                        }
                        
                        // Validate quantities before sending request
                        foreach (var kvp in tradeAmounts)
                        {
                            if (kvp.Value > 0)
                            {
                                var item = kvp.Key;
                                
                                // Check if requested quantity exceeds available quantity
                                if (kvp.Value > item.Quantity)
                                {
                                    Log.Warning($"[MTN] Client validation failed: Requested {kvp.Value} {item.DefName}, but only {item.Quantity} available");
                                    hasInvalidQuantities = true;
                                    break;
                                }
                                
                                var buyRecord = new TradeRecord
                                {
                                    DefName = item.DefName,
                                    Quantity = kvp.Value,
                                    Price = item.Price,
                                    PlayerName = item.PlayerName
                                };
                                itemsToBuy.Add(buyRecord);
                                Log.Message($"[MTN] Added to buy list: {buyRecord.DefName} x{buyRecord.Quantity} from {buyRecord.PlayerName}");
                            }
                        }
                        
                        if (hasInvalidQuantities)
                        {
                            Messages.Message("Some items are no longer available in the requested quantity. Please refresh the list.", MessageTypeDefOf.RejectInput, false);
                            return;
                        }

                        if (itemsToBuy.Count > 0)
                        {
                            Log.Message($"[MTN] Calling TradeUtils.BuyItems with {itemsToBuy.Count} items");
                            TradeUtils.BuyItems(itemsToBuy,
                                (success) => {
                                    Log.Message($"[MTN] Buy success response: {success}");
                                    // Extract total cost from server response
                                    int totalCost = 0;
                                    try
                                    {
                                        // Parse the server response to get total cost
                                        if (success.Contains("\"total_cost\":"))
                                        {
                                            int costIndex = success.IndexOf("\"total_cost\":");
                                            int start = success.IndexOf(':', costIndex) + 1;
                                            int end = success.IndexOfAny(new char[] {',', '}'}, start);
                                            string costStr = success.Substring(start, end - start).Trim();
                                            if (int.TryParse(costStr, out int cost))
                                            {
                                                totalCost = cost;
                                            }
                                        }
                                    }
                                    catch (System.Exception ex)
                                    {
                                        Log.Warning($"[MTN] Failed to parse total cost from server response: {ex.Message}");
                                    }
                                    
                                    if (totalCost > 0)
                                    {
                                        TradeUtils.RemoveSilverFromColony(totalCost);
                                    }
                                    
                                    TradeUtils.DeliverBoughtItems(tradeAmounts);
                                    Messages.Message("Purchase successful", MessageTypeDefOf.PositiveEvent, false);
                                    Close();
                                },
                                (error) => {
                                    Log.Error($"[MTN] Buy error response: {error}");
                                    // Try to extract and display the specific error message from the server
                                    string errorMessage = "Purchase failed";
                                    if (!string.IsNullOrEmpty(error))
                                    {
                                        // Look for detailed error message in the response
                                        if (error.Contains("detail"))
                                        {
                                            int detailIndex = error.IndexOf("\"detail\":");
                                            if (detailIndex >= 0)
                                            {
                                                int start = error.IndexOf('"', detailIndex + 9) + 1;
                                                int end = error.IndexOf('"', start);
                                                if (start > 0 && end > start)
                                                {
                                                    errorMessage = error.Substring(start, end - start);
                                                }
                                            }
                                        }
                                        else if (error.Contains("Not enough"))
                                        {
                                            // Extract the specific item and quantity info
                                            errorMessage = error.Replace("HTTP/1.1 400 Bad Request", "").Trim();
                                        }
                                    }
                                    
                                    Log.Error($"[MTN] Purchase failed: {error}");
                                    Messages.Message(errorMessage, MessageTypeDefOf.RejectInput, false);
                                    
                                    // Refresh the buy list to get updated quantities
                                    RefreshBuyList();
                                }
                            );
                        }
                        else
                        {
                            Log.Message("[MTN] No items selected for purchase");
                            Messages.Message("No items selected for purchase", MessageTypeDefOf.RejectInput, false);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error($"[MTN] Error during buy operation: {ex.Message}");
                        Messages.Message("TradeError", MessageTypeDefOf.RejectInput, false);
                    }
                }
                TooltipHandler.TipRegion(buyButtonRect, "Buy Button");
            }
            catch (System.Exception ex)
            {
                Log.Error($"[MTN] Error loading buy window: {ex.Message}");
                Widgets.Label(new Rect(inRect.x + 10f, inRect.y + 10f, inRect.width - 20f, 30f), "Error loading buy window");
            }
        }

        private void RefreshSellList()
        {
            colonyItems.Clear();
            tradeAmounts.Clear();
            tradePrices.Clear();
            tradePriceBuffers.Clear();
            colonyItems = TradeUtils.GetColonySellables();
            foreach (var rec in colonyItems)
            {
                tradeAmounts[rec] = 0;
                tradePrices[rec] = rec.Price;
                tradePriceBuffers[rec] = rec.Price.ToString();
            }
        }

        private void RefreshBuyList()
        {
            traderItems.Clear();
            tradeAmounts.Clear();
            TradeUtils.FetchTraderStock(
                (TradeRecord[] items) => {
                    foreach (var rec in items)
                    {
                        if (!string.IsNullOrEmpty(rec.DefName))
                        {
                            traderItems.Add(rec);
                            tradeAmounts[rec] = 0;
                        }
                        else
                        {
                            Log.Warning("[MTN] Skipping item with null DefName from server");
                        }
                    }
                },
                (string error) => {
                    Log.Error($"[MTN] Failed to fetch items for sale: {error}");
                }
            );
        }

        private void DrawTradeRow(Rect rect, TradeRecord item)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(item.DefName);
            Texture icon = def != null ? def.uiIcon : null;
            
            float currentX = rect.x;

            // Item Column (Icon + Label)
            if (icon != null)
                GUI.DrawTexture(new Rect(currentX + 5f, rect.y + 5f, 30f, 30f), icon);
            Widgets.Label(new Rect(currentX + 40f, rect.y + 5f, 180f, 30f), def?.LabelCap ?? "Unknown");
            currentX += 220f;

            // Quantity Column
            Widgets.Label(new Rect(currentX, rect.y + 5f, 60f, 30f), $"x{item.Quantity}");
            currentX += 60f;

            // Market Value Column
            if (def != null)
                Widgets.Label(new Rect(currentX, rect.y + 5f, 100f, 30f), $"${def.BaseMarketValue:0}");
            currentX += 100f;

            // Your Price Column
            float price = def != null ? def.BaseMarketValue : 0f;
            if (!tradePrices.TryGetValue(item, out price)) price = def != null ? def.BaseMarketValue : 0f;
            string priceBuffer = price.ToString();
            if (!tradePriceBuffers.TryGetValue(item, out priceBuffer)) priceBuffer = price.ToString();
            Widgets.Label(new Rect(currentX, rect.y + 5f, 10f, 30f), "$");
            Widgets.TextFieldNumeric(new Rect(currentX + 10f, rect.y + 5f, 60f, 30f), ref price, ref priceBuffer, 0f, 99999f);

            price = Mathf.Floor(price); // Round down to nearest whole number

            tradePrices[item] = price;
            tradePriceBuffers[item] = price.ToString();
            currentX += 110f;
            
            // Sell Amount Column
            int tradeAmount = 0;
            if (!tradeAmounts.TryGetValue(item, out tradeAmount)) tradeAmount = 0;

            if (Widgets.ButtonText(new Rect(currentX, rect.y + 5f, 30f, 30f), "-") && tradeAmount > 0)
                tradeAmounts[item] = Mathf.Max(tradeAmount - 1, 0);
            
            Widgets.Label(new Rect(currentX + 35f, rect.y + 5f, 30f, 30f), tradeAmount.ToString());
            
            if (Widgets.ButtonText(new Rect(currentX + 70f, rect.y + 5f, 30f, 30f), "+") && tradeAmount < item.Quantity)
                tradeAmounts[item] = Mathf.Min(tradeAmount + 1, item.Quantity);

            int newAmount = (int)Widgets.HorizontalSlider(
                new Rect(currentX + 105f, rect.y + 5f, 120f, 30f),
                tradeAmount, 0, item.Quantity, true, null, null, null, 1f
            );
            if (newAmount != tradeAmount)
                tradeAmounts[item] = newAmount;

            // Tooltip
            if (def != null)
                TooltipHandler.TipRegion(rect, def.LabelCap + "\n\n" + def.description);
        }

        private void DrawBuyRow(Rect rect, TradeRecord item)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(item.DefName);
            Texture icon = def != null ? def.uiIcon : null;

            float currentX = rect.x;

            // Item Column (Icon + Label)
            if (icon != null)
                GUI.DrawTexture(new Rect(currentX + 5f, rect.y + 5f, 30f, 30f), icon);
            Widgets.Label(new Rect(currentX + 40f, rect.y + 5f, 180f, 30f), def?.LabelCap ?? "Unknown");
            currentX += 220f;

            // Price Column - Use server-set price instead of RimWorld's base market value
            Widgets.Label(new Rect(currentX, rect.y + 5f, 100f, 30f), $"${item.Price:0}");
            currentX += 100f;
            
            // Available Column
            Widgets.Label(new Rect(currentX, rect.y + 5f, 100f, 30f), $"x{item.Quantity}");
            currentX += 100f;

            // Seller Column
            Widgets.Label(new Rect(currentX, rect.y + 5f, 100f, 30f), item.PlayerName ?? "Unknown");
            currentX += 100f;

            // Buy Amount Column
            int tradeAmount = 0;
            if (!tradeAmounts.TryGetValue(item, out tradeAmount)) tradeAmount = 0;

            if (Widgets.ButtonText(new Rect(currentX, rect.y + 5f, 30f, 30f), "-") && tradeAmount > 0)
                tradeAmounts[item] = Mathf.Max(tradeAmount - 1, 0);

            Widgets.Label(new Rect(currentX + 35f, rect.y + 5f, 30f, 30f), tradeAmount.ToString());

            if (Widgets.ButtonText(new Rect(currentX + 70f, rect.y + 5f, 30f, 30f), "+") && tradeAmount < item.Quantity)
                tradeAmounts[item] = tradeAmount + 1;

            int newAmount = (int)Widgets.HorizontalSlider(
                new Rect(currentX + 105f, rect.y + 5f, 120f, 30f),
                tradeAmount, 0, item.Quantity, true, null, null, null, 1f
            );
            if (newAmount != tradeAmount)
                tradeAmounts[item] = newAmount;

            // Tooltip
            if (def != null)
                TooltipHandler.TipRegion(rect, def.LabelCap + "\n\n" + def.description);
        }

        private void ClearTradeData()
        {
            tradeAmounts.Clear();
            tradePrices.Clear();
            tradePriceBuffers.Clear();
        }
    }
} 