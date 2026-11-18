
using System;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HighSpeedHero : Robot
    {
        // === INPUTS ===

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
        public long MagicNumber { get; set; }

        [Parameter("Slippage", DefaultValue = 30)]
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

        // === GLOBALS ===

        private Bars _m1Bars;
        private DateTime _lastCloseTime = DateTime.MinValue;
        private string _label;

        protected override void OnStart()
        {
            _label = "HS-Hero-" + MagicNumber;
            _m1Bars = MarketData.GetBars(TimeFrame.Minute);

            Print("HighSpeed Hero v1.50");
            Print("SideTP=${0} | Recovery x{1} | Cooldown={2}s",
                SideTP_USD, RecoveryMultiplier, CooldownSeconds);
        }

        protected override void OnTick()
        {
            double spreadPoints = Symbol.Spread / Symbol.TickSize;

            if (spreadPoints > MaxSpreadPoints)
                return;

            // 1. Manage each side independently
            ManageSides();

            // 2. Hard limit
            if (CountMyPositions() >= MaxOpenTrades)
                return;

            // 3. Cooldown after profit take
            if (_lastCloseTime != DateTime.MinValue)
            {
                double elapsed = (Server.Time - _lastCloseTime).TotalSeconds;

                if (elapsed < CooldownSeconds)
                {
                    Print("Cooldown active... {0}s left",
                        CooldownSeconds - (int)elapsed);
                    return;
                }
            }

            // 4. Velocity entry
            CheckVelocityEntry();
        }

        // === MANAGE SIDES ===

        private void ManageSides()
        {
            double buyProfit = 0.0;
            double sellProfit = 0.0;
            int buyCount = 0;
            int sellCount = 0;
            double totalProfit = 0.0;

            foreach (var position in GetMyPositions())
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

            // Close BUY side on good profit
            if (buyCount > 0 && buyProfit >= SideTP_USD)
            {
                CloseSide(TradeType.Buy,
                    "BUY side TP $" + SideTP_USD.ToString("F2"));

                _lastCloseTime = Server.Time;
            }

            // Close SELL side on good profit
            if (sellCount > 0 && sellProfit >= SideTP_USD)
            {
                CloseSide(TradeType.Sell,
                    "SELL side TP $" + SideTP_USD.ToString("F2"));

                _lastCloseTime = Server.Time;
            }

            // Emergency full close
            if (MaxDD_USD > 0 && totalProfit <= -MaxDD_USD)
            {
                CloseAllPositions("Max DD $ hit");
                return;
            }

            if (MaxDD_Percent > 0 && Account.Balance > 0)
            {
                double ddPct =
                    -totalProfit / Account.Balance * 100.0;

                if (ddPct >= MaxDD_Percent)
                {
                    CloseAllPositions("Max DD % hit");
                    return;
                }
            }

            string cooldownTxt = "";

            if (_lastCloseTime != DateTime.MinValue)
            {
                double elapsed =
                    (Server.Time - _lastCloseTime).TotalSeconds;

                if (elapsed < CooldownSeconds)
                {
                    cooldownTxt = " | COOLDOWN " +
                        (CooldownSeconds - (int)elapsed) + "s";
                }
            }

            Print("BUY: ${0:F2} ({1}) | SELL: ${2:F2} ({3}) | Target: ${4:F2}{5}",
                buyProfit, buyCount,
                sellProfit, sellCount,
                SideTP_USD, cooldownTxt);
        }

        // === CLOSE ONE SIDE ===

        private void CloseSide(TradeType side, string reason)
        {
            foreach (var position in GetMyPositions())
            {
                if (position.TradeType != side)
                    continue;

                TradeResult result = ClosePosition(position);

                if (!result.IsSuccessful)
                {
                    Print("Close failed #{0} Error={1}",
                        position.Id, result.Error);
                }
            }

            Print("Closed {0} -> {1}", side, reason);
        }

        // === CLOSE ALL ===

        private void CloseAllPositions(string reason)
        {
            foreach (var position in GetMyPositions())
            {
                TradeResult result = ClosePosition(position);

                if (!result.IsSuccessful)
                {
                    Print("Close failed #{0} Error={1}",
                        position.Id, result.Error);
                }
            }

            Print("All positions closed -> {0}", reason);
        }

        // === VELOCITY ENTRY ===

        private void CheckVelocityEntry()
        {
            int shift = UseCurrentCandle ? 0 : 1;
            int index = _m1Bars.Count - 1 - shift;

            if (index < 0)
                return;

            double open = _m1Bars.OpenPrices[index];
            double close = _m1Bars.ClosePrices[index];

            double bodyPips =
                Math.Abs(close - open) / Symbol.PipSize;

            if (bodyPips < VelocityPips)
                return;

            bool bullish = close > open;
            bool bearish = close < open;

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

        // === STRONG RECOVERY MARTINGALE ===

        private double CalculateLot(TradeType type)
        {
            double lot = BaseLot;

            int sameDirCount = 0;
            double sameDirProfit = 0.0;

            int oppositeCount = 0;
            double oppositeProfit = 0.0;

            TradeType oppositeType =
                type == TradeType.Buy
                    ? TradeType.Sell
                    : TradeType.Buy;

            foreach (var position in GetMyPositions())
            {
                double profit =
                    position.GrossProfit + position.Swap;

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
                    level = Math.Min(
                        sameDirCount, MaxMartingaleLevels);
            }

            // 2. Strong recovery on direction shift
            bool isRecovery = false;

            if (RecoveryOnShift &&
                oppositeCount > 0 &&
                oppositeProfit < 0)
            {
                isRecovery = true;

                int recoveryLevel = Math.Min(
                    oppositeCount + 1,
                    MaxMartingaleLevels);

                level = Math.Max(level, recoveryLevel);
            }

            if (level > 0)
            {
                lot = BaseLot *
                    Math.Pow(LotMultiplier, level);
            }

            // Extra recovery multiplier
            if (isRecovery)
                lot *= RecoveryMultiplier;

            // Broker volume limits, converted to lots
            double unitsPerLot =
                Symbol.QuantityToVolumeInUnits(1.0);

            double minLot =
                Symbol.VolumeInUnitsMin / unitsPerLot;

            double maxLot =
                Symbol.VolumeInUnitsMax / unitsPerLot;

            double lotStep =
                Symbol.VolumeInUnitsStep / unitsPerLot;

            lot = Math.Max(minLot, Math.Min(maxLot, lot));

            lot = Math.Round(
                Math.Floor(lot / lotStep) * lotStep, 2);

            // Convert lots into cTrader volume units
            double volume =
                Symbol.QuantityToVolumeInUnits(lot);

            volume = Symbol.NormalizeVolumeInUnits(
                volume, RoundingMode.Down);

            // Margin check
            double margin =
                Symbol.GetEstimatedMargin(type, volume);

            if (margin > Account.FreeMargin)
            {
                Print("Not enough margin for lot {0}", lot);
                return 0;
            }

            return lot;
        }

        // === OPEN POSITION ===

        private void OpenPosition(TradeType type, double lot)
        {
            double volume =
                Symbol.QuantityToVolumeInUnits(lot);

            volume = Symbol.NormalizeVolumeInUnits(
                volume, RoundingMode.Down);

            double marketRangePips =
                Slippage * Symbol.TickSize / Symbol.PipSize;

            double basePrice =
                type == TradeType.Buy
                    ? Symbol.Ask
                    : Symbol.Bid;

            // No individual stop loss or take profit,
            // matching the original MQL5 OpenPosition call.
            TradeResult result = ExecuteMarketRangeOrder(
                type,
                SymbolName,
                volume,
                marketRangePips,
                basePrice,
                _label,
                null,
                null,
                "HS-Hero"
            );

            if (!result.IsSuccessful)
            {
                Print("Open failed. Error={0} Lot={1}",
                    result.Error, lot);
            }
            else
            {
                Print("Opened {0} lot={1}",
                    type, lot);
            }
        }

        // === COUNT POSITIONS ===

        private int CountMyPositions()
        {
            return GetMyPositions().Length;
        }

        // === GET THIS BOT'S POSITIONS ===

        private Position[] GetMyPositions()
        {
            return Positions.FindAll(_label, SymbolName);
        }
    }
}
