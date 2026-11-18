
using System;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HighSpeedHero : Robot
    {
        // === ORIGINAL INPUTS ===

        [Parameter("Base Lot", DefaultValue = 0.01)]
        public double BaseLot { get; set; }

        [Parameter("Lot Multiplier", DefaultValue = 2.0)]
        public double LotMultiplier { get; set; }

        [Parameter("Max Martingale Levels", DefaultValue = 4)]
        public int MaxMartingaleLevels { get; set; }

        [Parameter("Side TP USD", DefaultValue = 2.0)]
        public double SideTP_USD { get; set; }

        [Parameter("Cooldown Seconds", DefaultValue = 10)]
        public int CooldownSeconds { get; set; }

        [Parameter("Recovery Multiplier", DefaultValue = 2.5)]
        public double RecoveryMultiplier { get; set; }

        [Parameter("Max DD USD", DefaultValue = 0)]
        public double MaxDD_USD { get; set; }

        [Parameter("Max DD Percent", DefaultValue = 0)]
        public double MaxDD_Percent { get; set; }

        [Parameter("Increase On Loss", DefaultValue = true)]
        public bool IncreaseOnLoss { get; set; }

        [Parameter("Increase On Profit", DefaultValue = false)]
        public bool IncreaseOnProfit { get; set; }

        [Parameter("Recovery On Shift", DefaultValue = true)]
        public bool RecoveryOnShift { get; set; }

        [Parameter("Magic Number", DefaultValue = 20251013)]
        public int MagicNumber { get; set; }

        [Parameter("Slippage Points", DefaultValue = 30)]
        public int Slippage { get; set; }

        [Parameter("Max Spread Points", DefaultValue = 25)]
        public double MaxSpreadPoints { get; set; }

        [Parameter("Velocity Pips", DefaultValue = 8)]
        public int VelocityPips { get; set; }

        [Parameter("Use Current Candle", DefaultValue = true)]
        public bool UseCurrentCandle { get; set; }

        [Parameter("Max Open Trades", DefaultValue = 3)]
        public int MaxOpenTrades { get; set; }

        [Parameter("Trade Buy", DefaultValue = true)]
        public bool TradeBuy { get; set; }

        [Parameter("Trade Sell", DefaultValue = true)]
        public bool TradeSell { get; set; }

        // === INTERNAL VARIABLES ===

        private Bars _m1Bars;
        private DateTime _lastCloseTime = DateTime.MinValue;
        private string _label;

        private Position[] MyPositions
        {
            get
            {
                return Positions.FindAll(_label, SymbolName);
            }
        }

        // === START ===

        protected override void OnStart()
        {
            _label = "HS-Hero-" + MagicNumber;

            _m1Bars = MarketData.GetBars(TimeFrame.Minute);

            Print("HighSpeed Hero v1.50 started.");
            Print("Side TP = {0}", SideTP_USD);
            Print("Recovery multiplier = {0}", RecoveryMultiplier);
            Print("Cooldown = {0} seconds", CooldownSeconds);
            Print("Symbol = {0}", SymbolName);
            Print("WARNING: DEMO TESTING ONLY.");
        }

        // === TICK ===

        protected override void OnTick()
        {
            if (!Account.IsLive && !Account.IsDemo)
                return;

            double spreadPoints = (Symbol.Ask - Symbol.Bid) / Symbol.TickSize;

            if (spreadPoints > MaxSpreadPoints)
                return;

            // 1. Manage BUY and SELL sides independently
            ManageSides();

            // 2. Maximum total positions
            if (CountMyPositions() >= MaxOpenTrades)
                return;

            // 3. Cooldown after side profit close
            if (_lastCloseTime != DateTime.MinValue)
            {
                double elapsed = (Server.Time - _lastCloseTime).TotalSeconds;

                if (elapsed < CooldownSeconds)
                {
                    double remaining = CooldownSeconds - elapsed;

                    Chart.DrawStaticText(
                        "HS_COOLDOWN",
                        "COOLDOWN: " + remaining.ToString("F0") + "s",
                        VerticalAlignment.Top,
                        HorizontalAlignment.Left,
                        Color.Yellow
                    );

                    return;
                }
            }

            Chart.RemoveObject("HS_COOLDOWN");

            // 4. M1 velocity entry
            CheckVelocityEntry();
        }

        // === MANAGE SIDES ===

        private void ManageSides()
        {
            double buyProfit = 0;
            double sellProfit = 0;
            int buyCount = 0;
            int sellCount = 0;
            double totalProfit = 0;

            foreach (Position position in MyPositions)
            {
                double profit = position.GrossProfit + position.Swap;

                totalProfit += profit;

                if (position.TradeType == TradeType.Buy)
                {
                    buyProfit += profit;
                    buyCount++;
                }
                else
                {
                    sellProfit += profit;
                    sellCount++;
                }
            }

            // Close BUY side when target is reached
            if (buyCount > 0 && buyProfit >= SideTP_USD)
            {
                CloseSide(TradeType.Buy, "BUY side TP");
                _lastCloseTime = Server.Time;
            }

            // Close SELL side when target is reached
            if (sellCount > 0 && sellProfit >= SideTP_USD)
            {
                CloseSide(TradeType.Sell, "SELL side TP");
                _lastCloseTime = Server.Time;
            }

            // Emergency full close in account currency
            if (MaxDD_USD > 0 && totalProfit <= -MaxDD_USD)
            {
                CloseAllPositions("Max DD USD hit");
                return;
            }

            if (MaxDD_Percent > 0 && Account.Balance > 0)
            {
                double ddPercent = -totalProfit / Account.Balance * 100.0;

                if (ddPercent >= MaxDD_Percent)
                {
                    CloseAllPositions("Max DD percent hit");
                    return;
                }
            }

            string cooldownText = "";

            if (_lastCloseTime != DateTime.MinValue)
            {
                double elapsed = (Server.Time - _lastCloseTime).TotalSeconds;

                if (elapsed < CooldownSeconds)
                {
                    cooldownText = " | COOLDOWN " +
                        (CooldownSeconds - elapsed).ToString("F0") + "s";
                }
            }

            string status =
                "HIGH SPEED HERO v1.50\n" +
                "BUY: " + buyProfit.ToString("F2") +
                " (" + buyCount + ")\n" +
                "SELL: " + sellProfit.ToString("F2") +
                " (" + sellCount + ")\n" +
                "TARGET: " + SideTP_USD.ToString("F2") +
                cooldownText;

            Chart.DrawStaticText(
                "HS_STATUS",
                status,
                VerticalAlignment.Top,
                HorizontalAlignment.Left,
                Color.White
            );
        }

        // === CLOSE ONE SIDE ===

        private void CloseSide(TradeType side, string reason)
        {
            foreach (Position position in MyPositions)
            {
                if (position.TradeType != side)
                    continue;

                TradeResult result = ClosePosition(position);

                if (!result.IsSuccessful)
                {
                    Print("Close failed #{0}: {1}",
                        position.Id, result.Error);
                }
            }

            Print("Closed {0} side: {1}", side, reason);
        }

        // === CLOSE ALL ===

        private void CloseAllPositions(string reason)
        {
            foreach (Position position in MyPositions)
            {
                TradeResult result = ClosePosition(position);

                if (!result.IsSuccessful)
                {
                    Print("Close failed #{0}: {1}",
                        position.Id, result.Error);
                }
            }

            Print("All positions close requested: {0}", reason);
        }

        // === VELOCITY ENTRY ===

        private void CheckVelocityEntry()
        {
            if (_m1Bars == null || _m1Bars.Count < 2)
                return;

            int shift = UseCurrentCandle ? 0 : 1;
            int index = _m1Bars.Count - 1 - shift;

            if (index < 0)
                return;

            double candleOpen = _m1Bars.OpenPrices[index];
            double candleClose = _m1Bars.ClosePrices[index];

            double bodyPips =
                Math.Abs(candleClose - candleOpen) / Symbol.PipSize;

            if (bodyPips < VelocityPips)
                return;

            bool bullish = candleClose > candleOpen;
            bool bearish = candleClose < candleOpen;

            if (bullish && TradeBuy)
            {
                double lot = CalculateLot(TradeType.Buy);

                if (lot > 0)
                    OpenPosition(TradeType.Buy, lot);
            }
            else if (bearish && TradeSell)
            {
                double lot = CalculateLot(TradeType.Sell);

                if (lot > 0)
                    OpenPosition(TradeType.Sell, lot);
            }
        }

        // === MARTINGALE + STRONG RECOVERY ===

        private double CalculateLot(TradeType type)
        {
            double lot = BaseLot;

            int sameDirCount = 0;
            double sameDirProfit = 0;

            int oppositeCount = 0;
            double oppositeProfit = 0;

            TradeType oppositeType =
                type == TradeType.Buy
                    ? TradeType.Sell
                    : TradeType.Buy;

            foreach (Position position in MyPositions)
            {
                double profit = position.GrossProfit + position.Swap;

                if (position.TradeType == type)
                {
                    sameDirCount++;
                    sameDirProfit += profit;
                }
                else if (position.TradeType == oppositeType)
                {
                    oppositeCount++;
                    oppositeProfit += profit;
                }
            }

            int level = 0;

            // 1. Normal same-direction martingale
            if (sameDirCount > 0)
            {
                bool shouldIncrease = false;

                if (IncreaseOnLoss && sameDirProfit < 0)
                    shouldIncrease = true;

                if (IncreaseOnProfit && sameDirProfit > 0)
                    shouldIncrease = true;

                if (shouldIncrease)
                    level = Math.Min(sameDirCount, MaxMartingaleLevels);
            }

            // 2. Strong recovery on direction shift
            bool isRecovery = false;

            if (RecoveryOnShift &&
                oppositeCount > 0 &&
                oppositeProfit < 0)
            {
                isRecovery = true;

                int recoveryLevel =
                    Math.Min(oppositeCount + 1, MaxMartingaleLevels);

                level = Math.Max(level, recoveryLevel);
            }

            if (level > 0)
            {
                lot = BaseLot * Math.Pow(LotMultiplier, level);
            }

            // Extra recovery multiplier
            if (isRecovery)
            {
                lot *= RecoveryMultiplier;
            }

            // Convert lots into cTrader volume units
            double volume = Symbol.QuantityToVolumeInUnits(lot);

            // Broker volume limits
            volume = Math.Max(Symbol.VolumeInUnitsMin,
                Math.Min(Symbol.VolumeInUnitsMax, volume));

            volume = Symbol.NormalizeVolumeInUnits(
                volume,
                RoundingMode.Down
            );

            // Margin check
            double requiredMargin =
                Symbol.GetEstimatedMargin(type, volume);

            if (requiredMargin > Account.FreeMargin)
            {
                Print("Not enough margin. Required: {0}",
                    requiredMargin);

                return 0;
            }

            return volume;
        }

        // === OPEN POSITION ===

        private void OpenPosition(TradeType type, double volume)
        {
            double basePrice =
                type == TradeType.Buy
                    ? Symbol.Ask
                    : Symbol.Bid;

            // Convert original slippage points into pips
            double marketRangePips =
                Slippage * Symbol.TickSize / Symbol.PipSize;

            TradeResult result = ExecuteMarketRangeOrder(
                type,
                SymbolName,
                volume,
                marketRangePips,
                basePrice,
                _label,
                (double?)null,
                (double?)null,
                "HS-Hero"
            );

            if (!result.IsSuccessful)
            {
                Print("Open failed: {0}. Volume: {1}",
                    result.Error, volume);
            }
            else
            {
                Print("Opened {0}. Volume units: {1}",
                    type, volume);
            }
        }

        // === COUNT POSITIONS ===

        private int CountMyPositions()
        {
            return MyPositions.Length;
        }
    }
}
