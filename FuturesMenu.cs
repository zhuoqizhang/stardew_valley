using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace MyFirstMod
{
    /// <summary>
    /// UI for placing placeholder beer futures contracts (Tab 1) and delivering matured contracts against
    /// the player's own inventory (Tab 2). Contract signing/fulfillment and quest-log sync live in
    /// ContractManager; this class only reads state to draw and dispatches read-only inventory clicks.
    /// </summary>
    public class FuturesMenu : IClickableMenu
    {
        private enum Tab
        {
            TradeFutures,
            DailyDelivery
        }

        private const int MenuWidth = 850;
        private const int MenuHeight = 650;
        private const int RowHeight = 96;
        private const int IconSlotSize = 64;

        private const int TabButtonWidth = 200;
        private const int TabButtonHeight = 64;
        private const int DeliveryRowHeight = 68;
        private const int MaxVisibleDeliveryRows = 4;

        private readonly IMonitor monitor;
        private readonly ContractManager contractManager;
        private readonly ITranslationHelper translation;
        private readonly Item beerItem;
        private readonly Item paleAleItem;
        private readonly ClickableComponent beerRowComponent;
        private readonly ClickableComponent paleAleRowComponent;
        private readonly ClickableComponent paleAleTestRowComponent;
        private readonly ClickableComponent beerTestRowComponent;
        private readonly SDate dueDate;

        private readonly ClickableComponent tradeTabComponent;
        private readonly ClickableComponent deliveryTabComponent;
        private readonly Rectangle deliveryListBounds;
        private readonly InventoryMenu deliveryInventoryMenu;
        private readonly List<TemporaryAnimatedSprite> deliveryAnimations = new List<TemporaryAnimatedSprite>();

        private Tab currentTab = Tab.TradeFutures;
        private bool isBeerRowHovered;
        private bool isPaleAleRowHovered;
        private bool isPaleAleTestRowHovered;
        private bool isBeerTestRowHovered;
        private string clickFeedbackText = "";

        /// <summary>
        /// True while waiting for the host to respond to a RequestSignContractMessage (plan.md section 7,
        /// farmhand signing flow) - blocks further sign clicks so a farmhand can't queue up multiple
        /// requests before the first one's confirmation arrives. Never set for the host's own click, since
        /// SignContractAsHost resolves synchronously (see the *Contract methods below).
        /// </summary>
        private bool isPendingSignRequest;

        /// <summary>
        /// True while waiting for the host to respond to a RequestDeliverMessage (plan.md section 7,
        /// farmhand delivery flow, "方案1预先锁定式验证") - blocks further delivery clicks so a farmhand
        /// can't fire off multiple concurrent requests before the first one resolves. Never set for the
        /// host's own click, which resolves synchronously (see OnInventoryItemClicked).
        /// </summary>
        private bool isPendingDeliveryRequest;

        /// <summary>Which item a farmhand's in-flight RequestDeliverMessage was for - needed at DeliverApproved time to find a fresh matching stack, since the original click's x/y may no longer point at the same item by the time the host's reply arrives.</summary>
        private string pendingDeliveryItemId;

        /// <summary>Original click position for the in-flight delivery request, reused for the debris animation's origin once approved - see OnDeliverApprovedReceived.</summary>
        private int pendingDeliveryClickX;
        private int pendingDeliveryClickY;

        public FuturesMenu(IMonitor monitor, ContractManager contractManager, ITranslationHelper translation)
            : base(
                Game1.uiViewport.Width / 2 - MenuWidth / 2,
                Game1.uiViewport.Height / 2 - MenuHeight / 2,
                MenuWidth,
                MenuHeight,
                showUpperRightCloseButton: true)
        {
            this.monitor = monitor;
            this.contractManager = contractManager;
            this.translation = translation;
            this.beerItem = ItemRegistry.Create("(O)" + ContractManager.BeerItemId);
            this.paleAleItem = ItemRegistry.Create("(O)" + ContractManager.PaleAleItemId);
            this.dueDate = DateHelper.GetNextNextFriday();

            Rectangle rowBounds = new Rectangle(
                xPositionOnScreen + 32,
                yPositionOnScreen + 96,
                width - 64,
                RowHeight);
            this.beerRowComponent = new ClickableComponent(rowBounds, "BeerRow");

            Rectangle paleAleRowBounds = new Rectangle(
                rowBounds.X,
                rowBounds.Bottom + 16,
                rowBounds.Width,
                RowHeight);
            this.paleAleRowComponent = new ClickableComponent(paleAleRowBounds, "PaleAleRow");

            Rectangle paleAleTestRowBounds = new Rectangle(
                paleAleRowBounds.X,
                paleAleRowBounds.Bottom + 16,
                paleAleRowBounds.Width,
                RowHeight);
            this.paleAleTestRowComponent = new ClickableComponent(paleAleTestRowBounds, "PaleAleTestRow");

            Rectangle beerTestRowBounds = new Rectangle(
                paleAleTestRowBounds.X,
                paleAleTestRowBounds.Bottom + 16,
                paleAleTestRowBounds.Width,
                RowHeight);
            this.beerTestRowComponent = new ClickableComponent(beerTestRowBounds, "BeerTestRow");

            this.tradeTabComponent = new ClickableComponent(
                new Rectangle(xPositionOnScreen + 16, yPositionOnScreen - TabButtonHeight + 8, TabButtonWidth, TabButtonHeight),
                "TradeTab");
            this.deliveryTabComponent = new ClickableComponent(
                new Rectangle(xPositionOnScreen + 16 + TabButtonWidth + 8, yPositionOnScreen - TabButtonHeight + 8, TabButtonWidth, TabButtonHeight),
                "DeliveryTab");

            this.deliveryListBounds = new Rectangle(
                xPositionOnScreen + 32,
                yPositionOnScreen + 88,
                width - 64,
                DeliveryRowHeight * MaxVisibleDeliveryRows);

            // Default InventoryMenu layout: 36 slots, 3 rows of 12, 64px each -> 768px wide.
            int inventoryX = xPositionOnScreen + (width - 64 * 12) / 2;
            int inventoryY = deliveryListBounds.Bottom + 24;
            this.deliveryInventoryMenu = new InventoryMenu(inventoryX, inventoryY, playerInventory: true, highlightMethod: HighlightDeliverableItem);

            // Plan.md section 7: fires once a signed contract (this client's own host-side signing, or a
            // farmhand's request the host just confirmed) is applied to local state, letting this menu clear
            // its "waiting for host" feedback and show the real price/margin regardless of which flow
            // produced it. Unsubscribed in cleanupBeforeExit so a closed menu doesn't linger as a listener.
            this.contractManager.ContractSignedApplied += OnContractSignedApplied;

            // Plan.md section 7 delivery flow: fire on the requester's own client once the host replies to
            // a RequestDeliverMessage. Also unsubscribed in cleanupBeforeExit.
            this.contractManager.DeliverApprovedReceived += OnDeliverApprovedReceived;
            this.contractManager.DeliverRejectedReceived += OnDeliverRejectedReceived;
        }

        /// <summary>Unsubscribes from ContractManager's events so a closed menu instance doesn't keep receiving them for the lifetime of the ContractManager (which outlives any single menu open/close).</summary>
        protected override void cleanupBeforeExit()
        {
            contractManager.ContractSignedApplied -= OnContractSignedApplied;
            contractManager.DeliverApprovedReceived -= OnDeliverApprovedReceived;
            contractManager.DeliverRejectedReceived -= OnDeliverRejectedReceived;
            base.cleanupBeforeExit();
        }

        /// <summary>
        /// Shared confirmation handler for both signing flows (plan.md section 7): the host's own click
        /// resolves synchronously through SignContractAsHost, and a farmhand's click resolves asynchronously
        /// once the host's ContractSignedMessage broadcast arrives - either way, this is what clears the
        /// "waiting" state and shows the real price/margin. Note: since contracts aren't attributed to a
        /// specific signer (plan.md section 7 - no PlayerId-based filtering), if another player signs a
        /// contract while THIS client also has a request in flight, this could clear the waiting state and
        /// show feedback for that unrelated contract instead of this client's own - a known cosmetic-only
        /// limitation (no money/inventory correctness is affected either way), not fixed in this phase.
        /// </summary>
        private void OnContractSignedApplied(FuturesContract contract)
        {
            isPendingSignRequest = false;

            string itemName = GetItemDisplayName(contract.ItemId);
            int countInGroup = contractManager.CountPending(contract.ItemId, contract.DueDate);

            clickFeedbackText = translation.Get("signing.success", new { item = itemName, price = contract.AgreedPrice, margin = contract.Margin, count = countInGroup }).ToString();
            monitor?.Log($"FuturesMenu: contract [{contract.ContractId}] confirmed signed, cleared pending state.", LogLevel.Info);
        }

        /// <summary>
        /// Plan.md section 7 delivery flow: the host approved this farmhand's RequestDeliverMessage. Does
        /// the actual consumption here rather than at request time - re-finds a fresh matching stack by
        /// pendingDeliveryItemId (not the original click's x/y, which may be stale by now) and defensively
        /// re-checks it still exists, since time has passed since the original click (network round-trip).
        /// If the item is gone, reports DeliverFailed instead of touching money/inventory - the host then
        /// releases its lock for the next requester in line.
        /// </summary>
        private void OnDeliverApprovedReceived(DeliverApprovedMessage message)
        {
            isPendingDeliveryRequest = false;

            Item item = FindFirstStackOfItem(pendingDeliveryItemId);
            if (item == null)
            {
                contractManager.ReportDeliverFailed(message.ContractId);
                clickFeedbackText = translation.Get("delivery.failed").ToString();
                monitor?.Log($"FuturesMenu: approved delivery [{message.ContractId}] but item={pendingDeliveryItemId} is no longer in inventory - reported DeliverFailed.", LogLevel.Warn);
                return;
            }

            int slotIndex = Game1.player.Items.IndexOf(item);
            Game1.player.Items[slotIndex] = item.ConsumeStack(1);

            // Plan.md 5.5 (margin-as-prepayment): margin was already paid to the player at signing, so
            // delivery pays the remainder, not the full agreed price. Margin isn't transmitted on the wire
            // anywhere (not in DeliverApprovedMessage, not in FuturesContractSaveData) - it's always this
            // same deterministic function of agreedPrice, recomputed locally here exactly like every other
            // client (including the contract's original signer) computed it.
            int margin = SettlementMath.ComputeMargin(message.AgreedPrice);
            int payout = SettlementMath.ComputeDeliveryPayout(message.AgreedPrice, margin);
            Game1.player.Money += payout;

            contractManager.ConfirmDeliver(message.ContractId);

            Game1.playSound("sell");
            SpawnDeliveryDebris(pendingDeliveryClickX, pendingDeliveryClickY);

            clickFeedbackText = translation.Get("delivery.success", new { item = GetItemDisplayName(pendingDeliveryItemId), payout, price = message.AgreedPrice, margin }).ToString();
            monitor?.Log($"FuturesMenu: delivered contract [{message.ContractId}] item={pendingDeliveryItemId} agreedPrice={message.AgreedPrice}G margin={margin}G payout={payout}G", LogLevel.Info);
        }

        /// <summary>Plan.md section 7 delivery flow: the host rejected this farmhand's RequestDeliverMessage - nothing eligible to deliver (none due today for that item, or someone else just claimed the last one). No inventory/money was ever touched.</summary>
        private void OnDeliverRejectedReceived(DeliverRejectedMessage message)
        {
            isPendingDeliveryRequest = false;
            clickFeedbackText = translation.Get("delivery.rejected").ToString();
            monitor?.Log($"FuturesMenu: delivery request for item={message.ItemId} rejected by host ({message.Reason}).", LogLevel.Info);
        }

        /// <summary>Finds the first inventory stack containing at least 1 of itemId - used at DeliverApproved time instead of the original click's slot, since a fresh lookup by id is the only reliable way to find the item after a network round-trip may have reshuffled/consumed stacks.</summary>
        private static Item FindFirstStackOfItem(string itemId)
        {
            foreach (Item item in Game1.player.Items)
            {
                if (item != null && item.ItemId == itemId && item.Stack > 0)
                {
                    return item;
                }
            }

            return null;
        }

        private Item GetItem(string itemId)
        {
            return itemId == ContractManager.BeerItemId ? beerItem : paleAleItem;
        }

        private string GetItemDisplayName(string itemId)
        {
            return GetItem(itemId)?.DisplayName ?? itemId;
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
            DrawTabButtons(b);

            string titleText = currentTab == Tab.TradeFutures ? translation.Get("menu.title-trade").ToString() : translation.Get("menu.tab-delivery").ToString();
            Vector2 titleSize = Game1.dialogueFont.MeasureString(titleText);
            Vector2 titlePosition = new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 32);
            Utility.drawTextWithShadow(b, titleText, Game1.dialogueFont, titlePosition, Game1.textColor);

            if (currentTab == Tab.TradeFutures)
            {
                DrawBeerRow(b);
                DrawPaleAleRow(b);
                DrawPaleAleTestRow(b);
                DrawBeerTestRow(b);

                if (!string.IsNullOrEmpty(clickFeedbackText))
                {
                    Vector2 feedbackPosition = new Vector2(beerRowComponent.bounds.X, beerTestRowComponent.bounds.Bottom + 24);
                    Utility.drawTextWithShadow(b, clickFeedbackText, Game1.smallFont, feedbackPosition, Color.DarkGreen);
                }
            }
            else
            {
                DrawDeliveryTab(b);
            }

            upperRightCloseButton?.draw(b);

            base.draw(b);
            drawMouse(b);
        }

        private void DrawTabButtons(SpriteBatch b)
        {
            DrawTabButton(b, tradeTabComponent, translation.Get("menu.tab-trade").ToString(), currentTab == Tab.TradeFutures);
            DrawTabButton(b, deliveryTabComponent, translation.Get("menu.tab-delivery").ToString(), currentTab == Tab.DailyDelivery);
        }

        /// <summary>Mirrors vanilla GameMenu's raised-tab look: the active tab sits flush with the panel below it, inactive tabs are nudged down and dimmed.</summary>
        private void DrawTabButton(SpriteBatch b, ClickableComponent tab, string label, bool isActive)
        {
            Rectangle bounds = tab.bounds;
            int yOffset = isActive ? 0 : 8;
            Rectangle drawBounds = new Rectangle(bounds.X, bounds.Y + yOffset, bounds.Width, bounds.Height - yOffset);

            drawTextureBox(b, drawBounds.X, drawBounds.Y, drawBounds.Width, drawBounds.Height, isActive ? Color.White : Color.Gray);

            Vector2 textSize = Game1.smallFont.MeasureString(label);
            Vector2 textPosition = new Vector2(
                drawBounds.X + (drawBounds.Width - textSize.X) / 2f,
                drawBounds.Y + (drawBounds.Height - textSize.Y) / 2f);
            Utility.drawTextWithShadow(b, label, Game1.smallFont, textPosition, isActive ? Game1.textColor : Color.DimGray);
        }

        /// <summary>Draws the beer entry the same way ShopMenu draws a for-sale row: icon + name + right-aligned price, with a lightened box on hover.</summary>
        private void DrawBeerRow(SpriteBatch b)
        {
            Rectangle bounds = beerRowComponent.bounds;

            drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White);

            if (isBeerRowHovered)
            {
                b.Draw(Game1.staminaRect, bounds, Color.Wheat * 0.4f);
            }

            float iconScale = isBeerRowHovered ? 1.1f : 1f;
            Vector2 iconPosition = new Vector2(bounds.X + 16, bounds.Y + (bounds.Height - IconSlotSize) / 2f);
            beerItem?.drawInMenu(b, iconPosition, iconScale, 1f, 0.9f, StackDrawType.Hide);

            string nameText = translation.Get("menu.row-name", new { item = beerItem?.DisplayName ?? translation.Get("item.beer").ToString(), date = DateHelper.FormatContractDate(dueDate) }).ToString();
            Vector2 namePosition = new Vector2(
                bounds.X + 16 + IconSlotSize + 16,
                bounds.Y + (bounds.Height - Game1.smallFont.MeasureString(nameText).Y) / 2f);
            Utility.drawTextWithShadow(b, nameText, Game1.smallFont, namePosition, Game1.textColor);

            string priceText = $"{ContractManager.GetNativeSellPrice(ContractManager.BeerItemId)}G";
            Vector2 priceSize = Game1.smallFont.MeasureString(priceText);
            Vector2 pricePosition = new Vector2(bounds.Right - 16 - priceSize.X, bounds.Y + (bounds.Height - priceSize.Y) / 2f);
            Utility.drawTextWithShadow(b, priceText, Game1.smallFont, pricePosition, Game1.textColor);
        }

        /// <summary>Pale Ale entry with the normal GetNextNextFriday() due date, drawn the same way as the beer row.</summary>
        private void DrawPaleAleRow(SpriteBatch b)
        {
            Rectangle bounds = paleAleRowComponent.bounds;

            drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White);

            if (isPaleAleRowHovered)
            {
                b.Draw(Game1.staminaRect, bounds, Color.Wheat * 0.4f);
            }

            float iconScale = isPaleAleRowHovered ? 1.1f : 1f;
            Vector2 iconPosition = new Vector2(bounds.X + 16, bounds.Y + (bounds.Height - IconSlotSize) / 2f);
            paleAleItem?.drawInMenu(b, iconPosition, iconScale, 1f, 0.9f, StackDrawType.Hide);

            string nameText = translation.Get("menu.row-name", new { item = paleAleItem?.DisplayName ?? translation.Get("item.pale-ale").ToString(), date = DateHelper.FormatContractDate(dueDate) }).ToString();
            Vector2 namePosition = new Vector2(
                bounds.X + 16 + IconSlotSize + 16,
                bounds.Y + (bounds.Height - Game1.smallFont.MeasureString(nameText).Y) / 2f);
            Utility.drawTextWithShadow(b, nameText, Game1.smallFont, namePosition, Game1.textColor);

            string priceText = $"{ContractManager.GetNativeSellPrice(ContractManager.PaleAleItemId)}G";
            Vector2 priceSize = Game1.smallFont.MeasureString(priceText);
            Vector2 pricePosition = new Vector2(bounds.Right - 16 - priceSize.X, bounds.Y + (bounds.Height - priceSize.Y) / 2f);
            Utility.drawTextWithShadow(b, priceText, Game1.smallFont, pricePosition, Game1.textColor);
        }

        /// <summary>Test-only Pale Ale entry: due date is always "signed day + 1", computed fresh here (not cached) so it stays correct if the menu is left open across an in-game day boundary.</summary>
        private void DrawPaleAleTestRow(SpriteBatch b)
        {
            Rectangle bounds = paleAleTestRowComponent.bounds;

            drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White);

            if (isPaleAleTestRowHovered)
            {
                b.Draw(Game1.staminaRect, bounds, Color.Wheat * 0.4f);
            }

            float iconScale = isPaleAleTestRowHovered ? 1.1f : 1f;
            Vector2 iconPosition = new Vector2(bounds.X + 16, bounds.Y + (bounds.Height - IconSlotSize) / 2f);
            paleAleItem?.drawInMenu(b, iconPosition, iconScale, 1f, 0.9f, StackDrawType.Hide);

            SDate testDueDate = SDate.Now().AddDays(1);
            string nameText = translation.Get("menu.row-name", new { item = paleAleItem?.DisplayName ?? translation.Get("item.pale-ale").ToString(), date = DateHelper.FormatContractDate(testDueDate) }).ToString()
                + translation.Get("menu.test-suffix").ToString();
            Vector2 namePosition = new Vector2(
                bounds.X + 16 + IconSlotSize + 16,
                bounds.Y + (bounds.Height - Game1.smallFont.MeasureString(nameText).Y) / 2f);
            Utility.drawTextWithShadow(b, nameText, Game1.smallFont, namePosition, Game1.textColor);

            string priceText = $"{ContractManager.GetNativeSellPrice(ContractManager.PaleAleItemId)}G";
            Vector2 priceSize = Game1.smallFont.MeasureString(priceText);
            Vector2 pricePosition = new Vector2(bounds.Right - 16 - priceSize.X, bounds.Y + (bounds.Height - priceSize.Y) / 2f);
            Utility.drawTextWithShadow(b, priceText, Game1.smallFont, pricePosition, Game1.textColor);
        }

        /// <summary>Test-only Beer entry: due date is always "signed day + 1", same purpose as DrawPaleAleTestRow - lets a same-night, different-item default (or delivery) be tested next to the Pale Ale test row.</summary>
        private void DrawBeerTestRow(SpriteBatch b)
        {
            Rectangle bounds = beerTestRowComponent.bounds;

            drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White);

            if (isBeerTestRowHovered)
            {
                b.Draw(Game1.staminaRect, bounds, Color.Wheat * 0.4f);
            }

            float iconScale = isBeerTestRowHovered ? 1.1f : 1f;
            Vector2 iconPosition = new Vector2(bounds.X + 16, bounds.Y + (bounds.Height - IconSlotSize) / 2f);
            beerItem?.drawInMenu(b, iconPosition, iconScale, 1f, 0.9f, StackDrawType.Hide);

            SDate testDueDate = SDate.Now().AddDays(1);
            string nameText = translation.Get("menu.row-name", new { item = beerItem?.DisplayName ?? translation.Get("item.beer").ToString(), date = DateHelper.FormatContractDate(testDueDate) }).ToString()
                + translation.Get("menu.test-suffix").ToString();
            Vector2 namePosition = new Vector2(
                bounds.X + 16 + IconSlotSize + 16,
                bounds.Y + (bounds.Height - Game1.smallFont.MeasureString(nameText).Y) / 2f);
            Utility.drawTextWithShadow(b, nameText, Game1.smallFont, namePosition, Game1.textColor);

            string priceText = $"{ContractManager.GetNativeSellPrice(ContractManager.BeerItemId)}G";
            Vector2 priceSize = Game1.smallFont.MeasureString(priceText);
            Vector2 pricePosition = new Vector2(bounds.Right - 16 - priceSize.X, bounds.Y + (bounds.Height - priceSize.Y) / 2f);
            Utility.drawTextWithShadow(b, priceText, Game1.smallFont, pricePosition, Game1.textColor);
        }

        /// <summary>One merged preview row: N Pending contracts that share (ItemId, DueDate) and, for today's rows, the same deliverable/queued status (design doc 10.2 "列表展示的合并规则").</summary>
        private class DeliveryRowGroup
        {
            public string ItemId;
            public SDate DueDate;
            public int AgreedPrice;
            public int Count;
            public bool IsDueToday;
            public bool CanDeliver;
        }

        /// <summary>
        /// Draws the Pending-contract preview list (due-today rows get a FIFO "可交割/排队等待" preview per
        /// design doc 5.2) and the embedded player inventory below it. All state here is re-derived from
        /// ContractManager.Contracts and Game1.player.Items every frame, so nothing needs manual "refresh"
        /// bookkeeping after a delivery changes contract statuses or inventory counts.
        /// </summary>
        private void DrawDeliveryTab(SpriteBatch b)
        {
            SDate today = SDate.Now();
            List<DeliveryRowGroup> rows = BuildDeliveryRows(today);

            if (rows.Count == 0)
            {
                Vector2 emptyPosition = new Vector2(deliveryListBounds.X, deliveryListBounds.Y);
                Utility.drawTextWithShadow(b, translation.Get("menu.empty-delivery-list").ToString(), Game1.smallFont, emptyPosition, Game1.textColor);
            }
            else
            {
                int visibleCount = Math.Min(rows.Count, MaxVisibleDeliveryRows);
                for (int i = 0; i < visibleCount; i++)
                {
                    Rectangle rowBounds = new Rectangle(
                        deliveryListBounds.X,
                        deliveryListBounds.Y + i * DeliveryRowHeight,
                        deliveryListBounds.Width,
                        DeliveryRowHeight - 4);
                    DrawContractRow(b, rowBounds, rows[i]);
                }

                if (rows.Count > visibleCount)
                {
                    string moreText = translation.Get("menu.more-rows-hidden", new { count = rows.Count - visibleCount }).ToString();
                    Vector2 morePosition = new Vector2(deliveryListBounds.X, deliveryListBounds.Y + visibleCount * DeliveryRowHeight);
                    Utility.drawTextWithShadow(b, moreText, Game1.smallFont, morePosition, Game1.textColor);
                }
            }

            deliveryInventoryMenu.draw(b);

            if (!string.IsNullOrEmpty(clickFeedbackText))
            {
                Vector2 feedbackPosition = new Vector2(
                    deliveryInventoryMenu.xPositionOnScreen,
                    deliveryInventoryMenu.yPositionOnScreen + deliveryInventoryMenu.height + 12);
                Utility.drawTextWithShadow(b, clickFeedbackText, Game1.smallFont, feedbackPosition, Color.DarkGreen);
            }

            if (!string.IsNullOrEmpty(deliveryInventoryMenu.hoverText))
            {
                drawHoverText(b, deliveryInventoryMenu.hoverText, Game1.smallFont, 0, 0, -1, deliveryInventoryMenu.hoverTitle);
            }

            for (int i = deliveryAnimations.Count - 1; i >= 0; i--)
            {
                if (deliveryAnimations[i].update(Game1.currentGameTime))
                {
                    deliveryAnimations.RemoveAt(i);
                }
                else
                {
                    deliveryAnimations[i].draw(b, localPosition: true);
                }
            }
        }

        /// <summary>
        /// Groups Pending contracts by (ItemId, DueDate) into merged "xN" rows, sorted by DueDate then by
        /// the group's earliest SignedDate (FIFO). A due-today group is split into up to two rows - one for
        /// the FIFO-earliest contracts that current stock can cover ("可交割"), one for the remainder
        /// ("库存不足，排队等待") - only when both statuses are actually present in that group; otherwise it
        /// stays a single row. Non-today groups always stay a single greyed-out row.
        /// </summary>
        private List<DeliveryRowGroup> BuildDeliveryRows(SDate today)
        {
            List<DeliveryRowGroup> rows = new List<DeliveryRowGroup>();

            var groups = contractManager.Contracts
                .Where(c => c.Status == ContractStatus.Pending)
                .GroupBy(c => new { c.ItemId, Day = c.DueDate.DaysSinceStart })
                .OrderBy(g => g.Key.Day)
                .ThenBy(g => g.Min(c => c.SignedDate.DaysSinceStart));

            foreach (var group in groups)
            {
                List<FuturesContract> ordered = group.OrderBy(c => c.SignedDate.DaysSinceStart).ToList();
                SDate dueDateForGroup = ordered[0].DueDate;
                int agreedPrice = ordered[0].AgreedPrice;
                bool isDueToday = dueDateForGroup.Equals(today);

                if (!isDueToday)
                {
                    rows.Add(new DeliveryRowGroup
                    {
                        ItemId = group.Key.ItemId,
                        DueDate = dueDateForGroup,
                        AgreedPrice = agreedPrice,
                        Count = ordered.Count,
                        IsDueToday = false
                    });
                    continue;
                }

                int stock = GetInventoryStock(group.Key.ItemId);
                int deliverableCount = Math.Min(stock, ordered.Count);
                int queuedCount = ordered.Count - deliverableCount;

                if (deliverableCount > 0)
                {
                    rows.Add(new DeliveryRowGroup
                    {
                        ItemId = group.Key.ItemId,
                        DueDate = dueDateForGroup,
                        AgreedPrice = agreedPrice,
                        Count = deliverableCount,
                        IsDueToday = true,
                        CanDeliver = true
                    });
                }

                if (queuedCount > 0)
                {
                    rows.Add(new DeliveryRowGroup
                    {
                        ItemId = group.Key.ItemId,
                        DueDate = dueDateForGroup,
                        AgreedPrice = agreedPrice,
                        Count = queuedCount,
                        IsDueToday = true,
                        CanDeliver = false
                    });
                }
            }

            return rows;
        }

        /// <summary>Single-line layout: icon + "name xN @priceG [status]" on the left, due date right-aligned. Non-today rows are greyed out with no status text.</summary>
        private void DrawContractRow(SpriteBatch b, Rectangle bounds, DeliveryRowGroup row)
        {
            Color boxTint = row.IsDueToday ? Color.White : Color.Gray;
            drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, boxTint);

            Item icon = ItemRegistry.Create("(O)" + row.ItemId);
            Vector2 iconPosition = new Vector2(bounds.X + 12, bounds.Y + (bounds.Height - IconSlotSize) / 2f);
            icon.drawInMenu(b, iconPosition, 1f, row.IsDueToday ? 1f : 0.5f, 0.9f, StackDrawType.Hide);

            Color textColor;
            string statusSuffix;
            if (!row.IsDueToday)
            {
                textColor = Color.DimGray;
                statusSuffix = "";
            }
            else if (row.CanDeliver)
            {
                textColor = Color.DarkGreen;
                statusSuffix = " " + translation.Get("menu.status-deliverable").ToString();
            }
            else
            {
                textColor = Color.DarkRed;
                statusSuffix = " " + translation.Get("menu.status-queued").ToString();
            }

            string nameText = translation.Get("menu.delivery-row-name", new { item = icon.DisplayName, count = row.Count, price = row.AgreedPrice, status = statusSuffix }).ToString();
            Vector2 namePosition = new Vector2(
                bounds.X + 12 + IconSlotSize + 12,
                bounds.Y + (bounds.Height - Game1.smallFont.MeasureString(nameText).Y) / 2f);
            Utility.drawTextWithShadow(b, nameText, Game1.smallFont, namePosition, textColor);

            string dueText = translation.Get("menu.due-date", new { date = DateHelper.FormatContractDate(row.DueDate) }).ToString();
            Vector2 dueSize = Game1.smallFont.MeasureString(dueText);
            Vector2 duePosition = new Vector2(bounds.Right - 16 - dueSize.X, bounds.Y + (bounds.Height - dueSize.Y) / 2f);
            Utility.drawTextWithShadow(b, dueText, Game1.smallFont, duePosition, textColor);
        }

        /// <summary>highlightMethod for the embedded inventory: dims every slot except the varieties with at least one Pending contract due today.</summary>
        private bool HighlightDeliverableItem(Item item)
        {
            if (item == null)
            {
                return false;
            }

            SDate today = SDate.Now();
            return contractManager.Contracts.Any(c =>
                c.Status == ContractStatus.Pending &&
                c.DueDate.Equals(today) &&
                c.ItemId == item.ItemId);
        }

        private static int GetInventoryStock(string itemId)
        {
            int total = 0;
            foreach (Item item in Game1.player.Items)
            {
                if (item != null && item.ItemId == itemId)
                {
                    total += item.Stack;
                }
            }

            return total;
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);
            isBeerRowHovered = currentTab == Tab.TradeFutures && beerRowComponent.bounds.Contains(x, y);
            isPaleAleRowHovered = currentTab == Tab.TradeFutures && paleAleRowComponent.bounds.Contains(x, y);
            isPaleAleTestRowHovered = currentTab == Tab.TradeFutures && paleAleTestRowComponent.bounds.Contains(x, y);
            isBeerTestRowHovered = currentTab == Tab.TradeFutures && beerTestRowComponent.bounds.Contains(x, y);

            if (currentTab == Tab.DailyDelivery)
            {
                deliveryInventoryMenu.hover(x, y, null);
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && readyToClose() && upperRightCloseButton.containsPoint(x, y))
            {
                if (playSound)
                {
                    Game1.playSound("bigDeSelect");
                }
                exitThisMenu();
                return;
            }

            if (tradeTabComponent.bounds.Contains(x, y))
            {
                currentTab = Tab.TradeFutures;
                if (playSound)
                {
                    Game1.playSound("smallSelect");
                }
                return;
            }

            if (deliveryTabComponent.bounds.Contains(x, y))
            {
                currentTab = Tab.DailyDelivery;
                if (playSound)
                {
                    Game1.playSound("smallSelect");
                }
                return;
            }

            if (currentTab == Tab.TradeFutures)
            {
                if (isPendingSignRequest)
                {
                    // A farmhand's sign request is still waiting on the host - ignore further clicks
                    // entirely (no sound, no re-request) rather than letting them queue up.
                    return;
                }

                if (beerRowComponent.bounds.Contains(x, y))
                {
                    SignBeerContract();

                    if (playSound)
                    {
                        Game1.playSound("smallSelect");
                    }
                }
                else if (paleAleRowComponent.bounds.Contains(x, y))
                {
                    SignPaleAleContract();

                    if (playSound)
                    {
                        Game1.playSound("smallSelect");
                    }
                }
                else if (paleAleTestRowComponent.bounds.Contains(x, y))
                {
                    SignPaleAleTestContract();

                    if (playSound)
                    {
                        Game1.playSound("smallSelect");
                    }
                }
                else if (beerTestRowComponent.bounds.Contains(x, y))
                {
                    SignBeerTestContract();

                    if (playSound)
                    {
                        Game1.playSound("smallSelect");
                    }
                }
            }
            else
            {
                OnInventoryItemClicked(x, y);
            }
        }

        /// <summary>
        /// Plan.md section 7 ("方案B请求-审批式"): the host resolves signing synchronously (no local state
        /// exists to create ahead of time, so there's nothing to show the player yet beyond "requesting");
        /// a farmhand sends a request and waits - OnContractSignedApplied fills in the real feedback text
        /// once the host's confirmation arrives. Shared by all four sign buttons below.
        /// </summary>
        private void SignContract(string itemId, ContractDueDateKind dueDateKind, string itemDisplayNameForFeedback)
        {
            if (isPendingSignRequest)
            {
                return;
            }

            if (Context.IsMainPlayer)
            {
                contractManager.SignContractAsHost(itemId, dueDateKind, Game1.player.UniqueMultiplayerID);
                // OnContractSignedApplied already fired synchronously by this point and set clickFeedbackText.
            }
            else
            {
                isPendingSignRequest = true;
                clickFeedbackText = translation.Get("signing.pending", new { item = itemDisplayNameForFeedback }).ToString();
                contractManager.RequestSignContract(itemId, dueDateKind);
            }
        }

        /// <summary>Beer row click: normal GetNextNextFriday() due date.</summary>
        private void SignBeerContract()
        {
            SignContract(ContractManager.BeerItemId, ContractDueDateKind.NextNextFriday, translation.Get("item.beer").ToString());
        }

        /// <summary>Pale Ale row click: normal GetNextNextFriday() due date.</summary>
        private void SignPaleAleContract()
        {
            SignContract(ContractManager.PaleAleItemId, ContractDueDateKind.NextNextFriday, translation.Get("item.pale-ale").ToString());
        }

        /// <summary>Test-only Pale Ale row click: due date is "signed day + 1" instead of GetNextNextFriday(), so a delivery can be tested the very next day.</summary>
        private void SignPaleAleTestContract()
        {
            string itemName = translation.Get("item.pale-ale").ToString() + translation.Get("menu.test-suffix").ToString();
            SignContract(ContractManager.PaleAleItemId, ContractDueDateKind.SignedDatePlusOneDay, itemName);
        }

        /// <summary>Test-only Beer row click: due date is "signed day + 1", same purpose as SignPaleAleTestContract - lets a same-night, multi-item default/delivery batch be tested.</summary>
        private void SignBeerTestContract()
        {
            string itemName = translation.Get("item.beer").ToString() + translation.Get("menu.test-suffix").ToString();
            SignContract(ContractManager.BeerItemId, ContractDueDateKind.SignedDatePlusOneDay, itemName);
        }

        /// <summary>
        /// Design doc 10.2's delivery interaction, now host-authoritative (plan.md section 7, "方案1预先锁定
        /// 式验证"): clicking an inventory item never fulfills a contract directly - only host FIFO
        /// arbitration (TryLockNextDeliverableContractAsHost) decides which contract a delivery counts
        /// against. The host's own click resolves synchronously (no network round-trip, same pattern as
        /// SignContract's host branch); a farmhand's click sends RequestDeliverMessage and waits for
        /// OnDeliverApprovedReceived/OnDeliverRejectedReceived. This deliberately never calls
        /// InventoryMenu.leftClick/rightClick - those pick up a whole stack or half-stack to carry on the
        /// cursor, which doesn't match "always consume exactly 1" - so item lookup goes through the
        /// read-only getItemAt, and the stack is trimmed by hand via Item.ConsumeStack(1), the same
        /// primitive InventoryMenu.rightClick itself uses internally.
        /// </summary>
        private void OnInventoryItemClicked(int x, int y)
        {
            if (isPendingDeliveryRequest)
            {
                return;
            }

            Item item = deliveryInventoryMenu.getItemAt(x, y);
            if (item == null)
            {
                return;
            }

            if (Context.IsMainPlayer)
            {
                if (contractManager.TryLockNextDeliverableContractAsHost(item.ItemId, out string contractId, out int agreedPrice))
                {
                    int slotIndex = deliveryInventoryMenu.getInventoryPositionOfClick(x, y);
                    IList<Item> items = deliveryInventoryMenu.actualInventory;
                    items[slotIndex] = item.ConsumeStack(1);

                    // Plan.md 5.5 (margin-as-prepayment): margin was already paid to the player at
                    // signing, so delivery pays the remainder, not the full agreed price.
                    int margin = SettlementMath.ComputeMargin(agreedPrice);
                    int payout = SettlementMath.ComputeDeliveryPayout(agreedPrice, margin);
                    Game1.player.Money += payout;
                    contractManager.HandleDeliverConfirmedAsHost(contractId);

                    Game1.playSound("sell");
                    SpawnDeliveryDebris(x, y);

                    clickFeedbackText = translation.Get("delivery.success", new { item = GetItemDisplayName(item.ItemId), payout, price = agreedPrice, margin }).ToString();
                    monitor?.Log($"FuturesMenu: [HOST] delivered contract [{contractId}] item={item.ItemId} agreedPrice={agreedPrice}G margin={margin}G payout={payout}G", LogLevel.Info);
                }
                else
                {
                    clickFeedbackText = translation.Get("delivery.rejected").ToString();
                }
            }
            else
            {
                isPendingDeliveryRequest = true;
                pendingDeliveryItemId = item.ItemId;
                pendingDeliveryClickX = x;
                pendingDeliveryClickY = y;
                clickFeedbackText = translation.Get("delivery.pending", new { item = GetItemDisplayName(item.ItemId) }).ToString();
                contractManager.RequestDeliver(item.ItemId);
            }
        }

        /// <summary>Same "TileSheets\debris" particle burst ShopMenu uses on a sell-click, reused here for visual consistency.</summary>
        private void SpawnDeliveryDebris(int x, int y)
        {
            Vector2 origin = deliveryInventoryMenu.snapToClickableComponent(x, y) + new Vector2(32f, 32f);
            for (int i = 0; i < 2; i++)
            {
                deliveryAnimations.Add(new TemporaryAnimatedSprite("TileSheets\\debris", new Rectangle(Game1.random.Next(2) * 16, 64, 16, 16), 9999f, 1, 999, origin, flicker: false, flipped: false)
                {
                    alphaFade = 0.025f,
                    motion = new Vector2(Game1.random.Next(-3, 4), -4f),
                    acceleration = new Vector2(0f, 0.5f),
                    delayBeforeAnimationStart = i * 25,
                    scale = 2f
                });
            }
        }
    }
}
