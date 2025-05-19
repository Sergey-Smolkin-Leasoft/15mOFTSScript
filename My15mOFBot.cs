using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class My15mOFBot : Robot
    {
        [Parameter("Symbols to Trade (comma separated)", DefaultValue = "EURUSD,GBPUSD,XAUUSD")]
        public string SymbolsToTrade { get; set; }

        [Parameter("Context Days", DefaultValue = 5, MinValue = 1)]
        public int ContextDays { get; set; }

        [Parameter("Context Price Change %", DefaultValue = 2.0, MinValue = 0.1)]
        public double ContextPriceChangePercentage { get; set; }

        [Parameter("Min RR", DefaultValue = 1.5, MinValue = 0.1)]
        public double MinRR { get; set; }

        [Parameter("Max RR", DefaultValue = 3.0, MinValue = 0.1)]
        public double MaxRR { get; set; }

        [Parameter("Risk per Trade %", DefaultValue = 1.0, MinValue = 0.01, MaxValue = 5.0)]
        public double RiskPerTradePercentage { get; set; }

        [Parameter("Max Trades Per Month", DefaultValue = 15, MinValue = 1)]
        public int MaxTradesPerMonth { get; set; }
        
        [Parameter("Label for Trades", DefaultValue = "My15mOFBot")]
        public string TradeLabel { get; set; }

        private List<string> _symbolsList;
        private Bars _series15M;
        private Bars _series1H;
        private int _tradesThisMonth;
        private DateTime _lastTradeMonth;

        protected override void OnStart()
        {
            _symbolsList = SymbolsToTrade.Split(',').Select(s => s.Trim().ToUpper()).ToList();
            if (!_symbolsList.Contains(Symbol.Name.ToUpper()))
            {
                Print($"Symbol {Symbol.Name} is not in the list of tradable symbols. Bot will not operate on this chart.");
                Stop();
                return;
            }

            _series15M = MarketData.GetBars(TimeFrame.Minute15);
            _series1H = MarketData.GetBars(TimeFrame.H1);
            _tradesThisMonth = 0;
            _lastTradeMonth = Server.Time.Date;

            Print("My15mOFBot started.");
            Print($"Symbols to trade: {string.Join(", ", _symbolsList)}");
            Print($"Context Days: {ContextDays}, Context Price Change %: {ContextPriceChangePercentage}");
            Print($"Min RR: {MinRR}, Max RR: {MaxRR}");
            Print($"Risk per Trade %: {RiskPerTradePercentage}");
            Print($"Max Trades Per Month: {MaxTradesPerMonth}");
        }

        protected override void OnBar()
        {
            if (Server.Time.Month != _lastTradeMonth.Month)
            {
                _tradesThisMonth = 0;
                _lastTradeMonth = Server.Time.Date;
            }

            if (_tradesThisMonth >= MaxTradesPerMonth)
            {
                //Print("Max trades for this month reached.");
                return;
            }
            
            // Only proceed if the current symbol is in our list
            if (!_symbolsList.Contains(Symbol.Name.ToUpper())) return;

            try
            {
                CheckForTradingOpportunities();
            }
            catch (Exception e)
            {
                Print($"Error in OnBar: {e.Message}{Environment.NewLine}{e.StackTrace}");
            }
        }

        private void CheckForTradingOpportunities()
        {
            // Ensure enough data
            if (_series15M.Count < ContextDays * (24 * 4) || _series1H.Count < 50) // Approx bars for context, 50 for 1H fractals
            {
                //Print("Not enough data yet.");
                return;
            }

            MarketContext context = DetermineMarketContext();
            //Print($"Current Market Context for {Symbol.Name}: {context}");

            if (context == MarketContext.Neutral)
            {
                //Print("Market context is neutral. No trade.");
                return;
            }

            if (context == MarketContext.Bullish)
            {
                TryOpenLongPosition();
            }
            else if (context == MarketContext.Bearish)
            {
                TryOpenShortPosition();
            }
        }

        private enum MarketContext
        {
            Bullish,
            Bearish,
            Neutral
        }

        private MarketContext DetermineMarketContext()
        {
            // Look back ContextDays
            int barsToAnalyze = ContextDays * 24 * 4; // 15M bars in ContextDays
            if (_series15M.Count <= barsToAnalyze) return MarketContext.Neutral;

            double startingPrice = _series15M.OpenPrices[barsToAnalyze]; // Price ContextDays ago
            double currentPrice = _series15M.ClosePrices.Last(1);
            double priceChange = ((currentPrice - startingPrice) / startingPrice) * 100;

            //Print($"Context Check: StartPrice={startingPrice}, CurrentPrice={currentPrice}, Change={priceChange}% over {ContextDays} days.");

            if (priceChange >= ContextPriceChangePercentage)
                return MarketContext.Bullish;
            if (priceChange <= -ContextPriceChangePercentage)
                return MarketContext.Bearish;

            return MarketContext.Neutral;
        }
        
        // --- Rule 1: Liquidity Sweep (Main) ---
        private bool IsMainLiquiditySweep(MarketContext context, int lookback = 20) // lookback for recent high/low
        {
            return HasLocalLiquiditySweep(_series15M, _series15M.Count - 1, 40, context == MarketContext.Bullish, 3, true);
        }

        // --- Rule 2: 15mOF (Order Flow) ---
        private bool CheckRule2_15mOF(MarketContext context)
        {
            int rule2LookbackBars = 96; // Approx 1 day on M15 (24 * 4)
            bool isBullishContext = context == MarketContext.Bullish;

            var sweepEventIndices = new List<int>();
            var fvgTestEventIndices = new List<int>();

            // Look for distinct sweep events in the last 'rule2LookbackBars'
            // Parameter for local sweep detection within Rule 2 (can be different from main sweep)
            int localSweepLookbackForSwing = 20; 
            int localSweepReactionLookback = 3;

            for (int i = 0; i < rule2LookbackBars; i++)
            {
                int evalIndex = _series15M.Count - 1 - i;
                // Ensure enough history for the evaluation at evalIndex
                if (evalIndex < localSweepLookbackForSwing + localSweepReactionLookback + 5) // Min bars for HasLocalLiquiditySweep
                    break;

                if (HasLocalLiquiditySweep(_series15M, evalIndex, localSweepLookbackForSwing, isBullishContext, localSweepReactionLookback, false))
                {
                    if (!sweepEventIndices.Any() || evalIndex < sweepEventIndices.Last() - localSweepReactionLookback) // Ensure distinct events
                    {
                        sweepEventIndices.Add(evalIndex);
                    }
                }
            }

            // Look for distinct FVG test events in the last 'rule2LookbackBars'
            int fvgLookback = 10; // How far back to look for an FVG from evalIndex
            int fvgTestReactionLookback = 3; // How many bars from evalIndex to check for test

            for (int i = 0; i < rule2LookbackBars; i++)
            {
                int evalIndex = _series15M.Count - 1 - i;
                 // Ensure enough history for FVG + test detection up to evalIndex
                if (evalIndex < fvgLookback + fvgTestReactionLookback + 3) // Min bars for FindLastFVG + IsFVGTested
                    break;

                // FVG must form *before* the bars that test it. Test window is evalIndex back to evalIndex - fvgTestReactionLookback + 1
                // So FVG should be searched up to evalIndex - fvgTestReactionLookback
                var fvg = FindLastFVG(_series15M, evalIndex - fvgTestReactionLookback, fvgLookback, isBullishContext ? FVGType.Bullish : FVGType.Bearish);
                if (fvg != null && IsFVGTested(_series15M, evalIndex, fvg, isBullishContext, fvgTestReactionLookback))
                {
                    if (!fvgTestEventIndices.Any() || evalIndex < fvgTestEventIndices.Last() - fvgTestReactionLookback) // Ensure distinct events
                    {
                        fvgTestEventIndices.Add(evalIndex);
                    }
                }
            }
            
            //Print($"Rule 2 Check for {Symbol.Name} ({context}): Sweeps found: {sweepEventIndices.Count}, FVG Tests found: {fvgTestEventIndices.Count}");

            bool rule2a = sweepEventIndices.Count >= 2;
            bool rule2b = sweepEventIndices.Count >= 1 && fvgTestEventIndices.Count >= 1;
            bool rule2c = fvgTestEventIndices.Count >= 2;

            if (rule2a) Print($"Rule 2a PASSED for {Symbol.Name} ({context})");
            if (rule2b) Print($"Rule 2b PASSED for {Symbol.Name} ({context})");
            if (rule2c) Print($"Rule 2c PASSED for {Symbol.Name} ({context})");

            return rule2a || rule2b || rule2c;
        }

        // --- Rule 3: Take-Profit Target with Acceptable RR ---
        // Checked during position opening when calculating SL/TP

        private void TryOpenLongPosition()
        {
            if (!IsMainLiquiditySweep(MarketContext.Bullish))
            {
                //Print("Long: Main liquidity sweep condition (Rule 1) not met.");
                return;
            }
            if (!CheckRule2_15mOF(MarketContext.Bullish))
            {
                //Print("Long: Rule 2 (15mOF) condition not met.");
                return;
            }

            // Entry Model: New FVG Test OR New Local Liquidity Sweep
            double entryPrice = Symbol.Ask;
            double stopLossPrice = 0;
            int signalBarIndex = _series15M.Count - 1; // Current bar is the potential signal bar
            string entryType = "";

            // 1. Check for entry via new FVG test
            var newFvgForEntry = FindLastFVG(_series15M, signalBarIndex - 3, 5, FVGType.Bullish); // Look for FVG forming before last 3 bars
            if (newFvgForEntry != null && IsFVGTested(_series15M, signalBarIndex, newFvgForEntry, true, 3))
            {
                Print($"Long Entry: New FVG Test for {Symbol.Name}");
                entryType = "FVG Test";
                stopLossPrice = GetStopLossForLong(newFvgForEntry, signalBarIndex); 
            }
            // 2. Else, check for entry via new local liquidity sweep
            else if (HasLocalLiquiditySweep(_series15M, signalBarIndex, 20, true, 3, false))
            {
                Print($"Long Entry: New Local Liquidity Sweep for {Symbol.Name}");
                entryType = "Local Sweep";
                // For sweep entry, SL is behind the signal bar (reaction bar of the sweep)
                stopLossPrice = GetStopLossForLong(null, signalBarIndex); 
            }
            else
            {
                //Print("Long: Entry model condition (New FVG Test or New Local Sweep) not met.");
                return;
            }
            
            if (stopLossPrice == 0 || stopLossPrice >= entryPrice) 
            {
                Print($"Long: Invalid SL price {stopLossPrice} relative to entry {entryPrice}. Entry type: {entryType}");
                return;
            }

            double takeProfitPrice = GetTakeProfitForLong(entryPrice, stopLossPrice);
            if (takeProfitPrice <= entryPrice)
            {
                Print($"Long: Invalid TP price {takeProfitPrice} relative to entry {entryPrice} (Rule 3 Fail?). SL was {stopLossPrice}.");
                return;
            }

            double rr = (takeProfitPrice - entryPrice) / (entryPrice - stopLossPrice);
            if (rr < MinRR || rr > MaxRR)
            {
                Print($"Long: RR {rr:F2} is outside the acceptable range [{MinRR}-{MaxRR}]. TP: {takeProfitPrice}, SL: {stopLossPrice}, Entry: {entryPrice}.");
                return;
            }

            double positionSize = CalculatePositionSize(entryPrice, stopLossPrice);
            if (positionSize <= 0)
            {
                Print("Long: Calculated position size is zero or negative.");
                return;
            }
            
            var result = ExecuteMarketOrder(TradeType.Buy, Symbol.Name, positionSize, TradeLabel + "_" + entryType, stopLossPrice, takeProfitPrice);
            if (result.IsSuccessful)
            {
                Print($"LONG ({entryType}) opened for {Symbol.Name} at {entryPrice}, SL: {stopLossPrice}, TP: {takeProfitPrice}, Size: {positionSize}, RR: {rr:F2}");
                _tradesThisMonth++;
            }
            else
            {
                Print($"Error opening LONG ({entryType}) for {Symbol.Name}: {result.Error}");
            }
        }

        private void TryOpenShortPosition()
        {
            if (!IsMainLiquiditySweep(MarketContext.Bearish)) 
            {
                //Print("Short: Main liquidity sweep condition (Rule 1) not met.");
                return;
            }
            if (!CheckRule2_15mOF(MarketContext.Bearish))
            {
                //Print("Short: Rule 2 (15mOF) condition not met.");
                return;
            }

            double entryPrice = Symbol.Bid;
            double stopLossPrice = 0;
            int signalBarIndex = _series15M.Count - 1;
            string entryType = "";

            var newFvgForEntry = FindLastFVG(_series15M, signalBarIndex - 3, 5, FVGType.Bearish);
            if (newFvgForEntry != null && IsFVGTested(_series15M, signalBarIndex, newFvgForEntry, false, 3))
            {
                Print($"Short Entry: New FVG Test for {Symbol.Name}");
                entryType = "FVG Test";
                stopLossPrice = GetStopLossForShort(newFvgForEntry, signalBarIndex);
            }
            else if (HasLocalLiquiditySweep(_series15M, signalBarIndex, 20, false, 3, false))
            {
                Print($"Short Entry: New Local Liquidity Sweep for {Symbol.Name}");
                entryType = "Local Sweep";
                stopLossPrice = GetStopLossForShort(null, signalBarIndex);
            }
            else
            {
                //Print("Short: Entry model condition (New FVG Test or New Local Sweep) not met.");
                return;
            }

            if (stopLossPrice == 0 || stopLossPrice <= entryPrice) 
            {
                Print($"Short: Invalid SL price {stopLossPrice} relative to entry {entryPrice}. Entry type: {entryType}");
                return;
            }

            double takeProfitPrice = GetTakeProfitForShort(entryPrice, stopLossPrice);
            if (takeProfitPrice >= entryPrice)
            {
                Print($"Short: Invalid TP price {takeProfitPrice} relative to entry {entryPrice} (Rule 3 Fail?). SL was {stopLossPrice}.");
                return;
            }

            double rr = (entryPrice - takeProfitPrice) / (stopLossPrice - entryPrice);
            if (rr < MinRR || rr > MaxRR)
            {
                Print($"Short: RR {rr:F2} is outside the acceptable range [{MinRR}-{MaxRR}]. TP: {takeProfitPrice}, SL: {stopLossPrice}, Entry: {entryPrice}.");
                return;
            }

            double positionSize = CalculatePositionSize(entryPrice, stopLossPrice);
            if (positionSize <= 0)
            {
                Print("Short: Calculated position size is zero or negative.");
                return;
            }

            var result = ExecuteMarketOrder(TradeType.Sell, Symbol.Name, positionSize, TradeLabel + "_" + entryType, stopLossPrice, takeProfitPrice);
            if (result.IsSuccessful)
            {
                Print($"SHORT ({entryType}) opened for {Symbol.Name} at {entryPrice}, SL: {stopLossPrice}, TP: {takeProfitPrice}, Size: {positionSize}, RR: {rr:F2}");
                _tradesThisMonth++;
            }
            else
            {
                Print($"Error opening SHORT ({entryType}) for {Symbol.Name}: {result.Error}");
            }
        }
        
        private double GetStopLossForLong(FVG fvg, int signalBarIndex)
        {
            // If FVG is provided (entry via FVG test), SL can be below the FVG structure or testing candle.
            // A common placement is below the low of the first candle (c1_idx) of the 3-bar FVG pattern,
            // or below the low of the signalBarIndex if it tested deep into FVG.
            // If FVG is null (entry via sweep), SL is based purely on signalBarIndex (reaction bar of sweep).
            double price;
            if (fvg != null && fvg.Type == FVGType.Bullish)
            {
                // FVG.BarIndex is bar *after* pattern. Pattern is BarIndex-1, BarIndex-2, BarIndex-3.
                // Low of C1 (oldest bar in FVG pattern) is _series15M.LowPrices[fvg.BarIndex - 3]
                // Low of C3 (newest bar in FVG pattern, its low forms top of Bullish FVG) is _series15M.LowPrices[fvg.BarIndex - 1]
                // Simplest: low of the FVG itself (fvg.Bottom), or the low of the testing candle (signalBarIndex)
                price = Math.Min(_series15M.LowPrices[signalBarIndex], fvg.Bottom); 
            }
            else
            {
                price = _series15M.LowPrices[signalBarIndex];
            }
            return price - Symbol.PipSize * 2; 
        }

        private double GetStopLossForShort(FVG fvg, int signalBarIndex)
        {
            double price;
            if (fvg != null && fvg.Type == FVGType.Bearish)
            {
                // High of C1 (oldest bar in FVG pattern) is _series15M.HighPrices[fvg.BarIndex - 3]
                // High of C3 (newest bar in FVG pattern, its high forms bottom of Bearish FVG) is _series15M.HighPrices[fvg.BarIndex - 1]
                // Simplest: high of the FVG itself (fvg.Top), or the high of the testing candle (signalBarIndex)
                price = Math.Max(_series15M.HighPrices[signalBarIndex], fvg.Top);
            }
            else
            {
                price = _series15M.HighPrices[signalBarIndex];
            }
            return price + Symbol.PipSize * 2;
        }

        private double CalculatePositionSize(double entryPrice, double stopLossPrice)
        {
            double riskAmount = Account.Balance * (RiskPerTradePercentage / 100.0);
            double stopLossPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;
            if (stopLossPips == 0) return 0; // Avoid division by zero

            double pipValue = Symbol.PipValue; // This might need adjustment for XAUUSD etc.
                                                // For non-forex pairs, PipValue might not be what we want.
                                                // We need the value of a 1-point move for the minimum tradeable quantity.
            
            if (Symbol.Name.ToUpper().Contains("XAU")) // Gold specific handling
            {
                // For XAUUSD, Symbol.TickSize is typically 0.01. Symbol.PipSize might be 0.01.
                // A 1 USD price move in XAUUSD for 1 lot (100 oz) is $100.
                // For 0.01 lot (1 oz), a 1 USD price move is $1.
                // Stop distance in price: Math.Abs(entryPrice - stopLossPrice)
                // Value per point (tick): Symbol.TickValue
                // Number of ticks in SL: Math.Abs(entryPrice - stopLossPrice) / Symbol.TickSize
                // Total risk for 1 lot: (Math.Abs(entryPrice - stopLossPrice) / Symbol.TickSize) * Symbol.TickValue
                // This needs to be per unit of Symbol.VolumeInUnitsMin
                
                // Simpler: stopLossDistanceInPrice * Symbol.VolumeInUnitsStep / Symbol.TickValue doesn't seem right
                // Let's use Symbol.VolumeInUnitsToRiskAmount(volume, currency) or calculate directly.
                // Monetary value of SL distance per unit of volume:
                // (Math.Abs(entryPrice - stopLossPrice) / Symbol.TickSize) * Symbol.TickValue per Symbol.LotSize (usually 1 for XAU)
                // Or more directly: Math.Abs(entryPrice - stopLossPrice) * Symbol.ContractSize (if SL is in price units and ContractSize is oz/shares etc.)
                // cTrader Symbol.MonetaryValue is useful here.
                // For 1 unit of volume (e.g. 0.01 lots), what is the value of the SL distance?
                // SL_distance_price * value_per_price_point_per_min_volume_step
                // This is complex due to varying contract specs.
                // The most reliable is usually:
                // riskAmount / (stopLossPips * Symbol.PipValue) for FX
                // For XAUUSD, if SL is $10, and 1 lot risk is $10 * 100 (oz), then risk is $1000.
                // We need riskAmount / ( (stopLossDistanceInPrice) * MonetaryValuePerUnit )
                // MonetaryValuePerUnit for XAUUSD: 1 (if trading 1 oz contracts and price is per oz)
                // For now, a common approach for XAUUSD if price is per oz and lot is 100 oz:
                // double pointsToRisk = Math.Abs(entryPrice - stopLossPrice);
                // double valuePerPointPerLot = Symbol.LotSize; (e.g. 100 for XAUUSD standard lot)
                // double lots = riskAmount / (pointsToRisk * valuePerPointPerLot);
                // return Symbol.NormalizeVolumeInUnits(lots * Symbol.LotSize, RoundingMode.Down); //This converts lots to volume units

                // Let's use a slightly more generic way if PipValue handles it.
                // If PipValue is per pip per lot, and PipSize defines a pip.
                // This part is CRITICAL and needs testing per symbol type.
                // For XAUUSD, if PipSize = 0.01 (a cent), PipValue is value of that cent move per lot.
                // stopLossPips = Math.Abs(entryPrice - stopLossPrice) / 0.01
                // This seems to be the standard cTrader way if PipValue is correctly reported.
            }


            double volumeInUnits = riskAmount / (stopLossPips * pipValue); // This gives volume in Lots if PipValue is per Lot
                                                                        // We need to convert it to symbol's volume units
            
            // The above formula gives lots directly if Symbol.PipValue is value of 1 pip change for 1 lot.
            // Then we need to convert these lots to the volume steps cTrader expects.
            double lots = volumeInUnits; 
            double tradeVolume = Symbol.NormalizeVolumeInUnits(lots * Symbol.LotSize, RoundingMode.Down);


            if (tradeVolume < Symbol.VolumeInUnitsMin)
            {
                Print($"Calculated volume {tradeVolume} is less than min volume {Symbol.VolumeInUnitsMin}. No trade.");
                return 0;
            }
            if (tradeVolume > Symbol.VolumeInUnitsMax)
            {
                Print($"Calculated volume {tradeVolume} is greater than max volume {Symbol.VolumeInUnitsMax}. Capping to max.");
                tradeVolume = Symbol.VolumeInUnitsMax;
            }

            return tradeVolume;
        }


        private double GetTakeProfitForLong(double entryPrice, double stopLossPrice)
        {
            double riskDistance = entryPrice - stopLossPrice;
            if (riskDistance <= 0) return entryPrice; // Should not happen

            // Try 1H Fractals first, then 15M Fractals
            double potentialTp = double.MinValue;

            // 1H Fractals (look for recent highs)
            var highs1H = _series1H.HighPrices.ToList(); // Get a copy to avoid modification issues if any
            var fractalHighs1H = GetFractalHighs(_series1H, 30).Where(fh => fh > entryPrice).OrderBy(fh => fh).ToList();
            
            foreach (var fractalHigh in fractalHighs1H)
            {
                if ((fractalHigh - entryPrice) / riskDistance >= MinRR)
                {
                    potentialTp = fractalHigh;
                    break;
                }
            }
            
            // 15M Fractals (look for recent highs)
            if (potentialTp == double.MinValue || (potentialTp - entryPrice) / riskDistance > MaxRR) // If 1H target is too far or not found
            {
                var highs15M = _series15M.HighPrices.ToList();
                var fractalHighs15M = GetFractalHighs(_series15M, 60).Where(fh => fh > entryPrice).OrderBy(fh => fh).ToList();
                foreach (var fractalHigh in fractalHighs15M)
                {
                     if ((fractalHigh - entryPrice) / riskDistance >= MinRR)
                    {
                        // Prefer closer TP if 1H was too far or not found
                        if (potentialTp == double.MinValue || fractalHigh < potentialTp)
                        {
                           potentialTp = fractalHigh;
                           break; // Take the first valid 15M fractal
                        }
                    }
                }
            }

            if (potentialTp == double.MinValue) return entryPrice; // No suitable fractal found

            // Cap TP by MaxRR
            double tpAtMaxRR = entryPrice + (riskDistance * MaxRR);
            return Math.Min(potentialTp, tpAtMaxRR);
        }

        private double GetTakeProfitForShort(double entryPrice, double stopLossPrice)
        {
            double riskDistance = stopLossPrice - entryPrice;
            if (riskDistance <= 0) return entryPrice;

            double potentialTp = double.MaxValue;

            // 1H Fractals (look for recent lows)
            var lows1H = _series1H.LowPrices.ToList();
            var fractalLows1H = GetFractalLows(_series1H, 30).Where(fl => fl < entryPrice).OrderByDescending(fl => fl).ToList();

            foreach (var fractalLow in fractalLows1H)
            {
                if ((entryPrice - fractalLow) / riskDistance >= MinRR)
                {
                    potentialTp = fractalLow;
                    break;
                }
            }

            // 15M Fractals (look for recent lows)
             if (potentialTp == double.MaxValue || (entryPrice - potentialTp) / riskDistance > MaxRR)
            {
                var lows15M = _series15M.LowPrices.ToList();
                var fractalLows15M = GetFractalLows(_series15M, 60).Where(fl => fl < entryPrice).OrderByDescending(fl => fl).ToList();
                foreach (var fractalLow in fractalLows15M)
                {
                    if ((entryPrice - fractalLow) / riskDistance >= MinRR)
                    {
                        if (potentialTp == double.MaxValue || fractalLow > potentialTp) // Prefer closer TP
                        {
                            potentialTp = fractalLow;
                            break;
                        }
                    }
                }
            }
            
            if (potentialTp == double.MaxValue) return entryPrice; // No suitable fractal found

            // Cap TP by MaxRR
            double tpAtMaxRR = entryPrice - (riskDistance * MaxRR);
            return Math.Max(potentialTp, tpAtMaxRR);
        }

        // --- Liquidity Sweep Helpers ---
        private bool HasLocalLiquiditySweep(Bars series, int evalIndex, int lookbackPeriodsForSwing, bool isBullishSweep, int reactionLookback = 3, bool isMainSweepContext = false)
        {
            if (evalIndex < reactionLookback + 2 + 2) return false; // Min bars for reaction + swing point itself + 2 bars around swing
            
            string sweepTypeLog = isMainSweepContext ? "Main" : "Local";

            for (int i = reactionLookback + 2; i < lookbackPeriodsForSwing + reactionLookback; i++)
            {
                int swingCandleIndex = evalIndex - i;
                if (swingCandleIndex < 2 || swingCandleIndex >= series.Count - 2 || swingCandleIndex >= evalIndex - reactionLookback +1) continue;

                if (isBullishSweep)
                {
                    bool isSwingLow = series.LowPrices[swingCandleIndex] < series.LowPrices[swingCandleIndex - 1] &&
                                      series.LowPrices[swingCandleIndex] < series.LowPrices[swingCandleIndex - 2] &&
                                      series.LowPrices[swingCandleIndex] < series.LowPrices[swingCandleIndex + 1] &&
                                      series.LowPrices[swingCandleIndex] < series.LowPrices[swingCandleIndex + 2];
                    
                    if (isSwingLow)
                    {
                        double swingLowPrice = series.LowPrices[swingCandleIndex];
                        bool swept = false;
                        int sweptAtIndex = -1;
                        double lowestPointDuringSweep = double.MaxValue;

                        for (int k = 0; k < reactionLookback; k++) 
                        {
                            int currentBarIndexToCheckSweep = evalIndex - k;
                            if (currentBarIndexToCheckSweep <= swingCandleIndex) continue; // Sweep must happen after swing point

                            if (series.LowPrices[currentBarIndexToCheckSweep] < swingLowPrice)
                            {
                                swept = true;
                                if (series.LowPrices[currentBarIndexToCheckSweep] < lowestPointDuringSweep)
                                {
                                   lowestPointDuringSweep = series.LowPrices[currentBarIndexToCheckSweep];
                                   sweptAtIndex = currentBarIndexToCheckSweep;
                                }
                            }
                        }

                        if (swept)
                        {
                            if (series.ClosePrices[evalIndex] > swingLowPrice) 
                            {
                                 //Print($"Bullish {sweepTypeLog} Sweep: Eval@{series.OpenTimes[evalIndex]}, Swing@{series.OpenTimes[swingCandleIndex]}({swingLowPrice}), SweptBy@{series.OpenTimes[sweptAtIndex]}({lowestPointDuringSweep}), ReactClose@{series.ClosePrices[evalIndex]}");
                                 return true;
                            }
                        }
                        if (isMainSweepContext) return false; 
                    }
                }
                else 
                {
                    bool isSwingHigh = series.HighPrices[swingCandleIndex] > series.HighPrices[swingCandleIndex - 1] &&
                                       series.HighPrices[swingCandleIndex] > series.HighPrices[swingCandleIndex - 2] &&
                                       series.HighPrices[swingCandleIndex] > series.HighPrices[swingCandleIndex + 1] &&
                                       series.HighPrices[swingCandleIndex] > series.HighPrices[swingCandleIndex + 2];
                    if (isSwingHigh)
                    {
                        double swingHighPrice = series.HighPrices[swingCandleIndex];
                        bool swept = false;
                        int sweptAtIndex = -1;
                        double highestPointDuringSweep = double.MinValue;

                        for (int k = 0; k < reactionLookback; k++)
                        {
                            int currentBarIndexToCheckSweep = evalIndex - k;
                            if (currentBarIndexToCheckSweep <= swingCandleIndex) continue;

                            if (series.HighPrices[currentBarIndexToCheckSweep] > swingHighPrice)
                            {
                                swept = true;
                                if (series.HighPrices[currentBarIndexToCheckSweep] > highestPointDuringSweep)
                                {
                                    highestPointDuringSweep = series.HighPrices[currentBarIndexToCheckSweep];
                                    sweptAtIndex = currentBarIndexToCheckSweep;
                                }
                            }
                        }

                        if (swept)
                        {
                            if (series.ClosePrices[evalIndex] < swingHighPrice)
                            {
                                //Print($"Bearish {sweepTypeLog} Sweep: Eval@{series.OpenTimes[evalIndex]}, Swing@{series.OpenTimes[swingCandleIndex]}({swingHighPrice}), SweptBy@{series.OpenTimes[sweptAtIndex]}({highestPointDuringSweep}), ReactClose@{series.ClosePrices[evalIndex]}");
                                return true;
                            }
                        }
                        if (isMainSweepContext) return false; 
                    }
                }
            }
            return false;
        }


        // --- FVG (Imbalance) Helpers ---
        private enum FVGType { Bullish, Bearish }
        private class FVG
        {
            public double Top { get; set; }
            public double Bottom { get; set; }
            public DateTime StartTime { get; set; } // Time of the bar *after* the FVG pattern
            public int BarIndex { get; set; } // Index of the bar *after* the FVG pattern (candle3_idx + 1)
            public FVGType Type {get; set;}
        }

        private FVG FindLastFVG(Bars series, int evalIndex, int lookback, FVGType type)
        {
            for (int i = 0; i < lookback; i++)
            {
                int c3_idx = evalIndex - 3 - i; // Candle 3 (most recent of the pattern being checked)
                int c2_idx = c3_idx - 1;       // Candle 2 (middle)
                int c1_idx = c3_idx - 2;       // Candle 1 (oldest of the pattern)

                if (c1_idx < 0) break; 

                var c1High = series.HighPrices[c1_idx];
                var c1Low = series.LowPrices[c1_idx];
                var c3High = series.HighPrices[c3_idx];
                var c3Low = series.LowPrices[c3_idx];
                
                // BarIndex for FVG object refers to the bar *after* c3.
                int fvgBarIndex = c3_idx + 1;
                if (fvgBarIndex >= series.Count) continue; // FVG must be fully formed

                if (type == FVGType.Bullish)
                {
                    if (c1High < c3Low) 
                    {
                        return new FVG
                        {
                            Top = c3Low,
                            Bottom = c1High,
                            StartTime = series.OpenTimes[fvgBarIndex],
                            BarIndex = fvgBarIndex,
                            Type = FVGType.Bullish
                        };
                    }
                }
                else 
                {
                    if (c1Low > c3High) 
                    {
                        return new FVG
                        {
                            Top = c1Low,
                            Bottom = c3High,
                            StartTime = series.OpenTimes[fvgBarIndex],
                            BarIndex = fvgBarIndex,
                            Type = FVGType.Bearish
                        };
                    }
                }
            }
            return null;
        }

        private bool IsFVGTested(Bars series, int evalIndex, FVG fvg, bool forBullishSignal, int testLookback = 3)
        {
            if (fvg == null) return false;
            
            for (int i = 0; i < testLookback; i++)
            {
                int barIndexToTest = evalIndex - i;
                if (barIndexToTest < 0 || barIndexToTest < fvg.BarIndex) continue; // Test must happen at or after FVG is defined
                
                if (forBullishSignal && fvg.Type == FVGType.Bullish)
                {
                    if (series.LowPrices[barIndexToTest] <= fvg.Top && series.HighPrices[barIndexToTest] >= fvg.Bottom) 
                    {
                        //Print($"Bullish FVG {fvg.Bottom}-{fvg.Top} (formed after {fvg.StartTime}) tested by bar {series.OpenTimes[barIndexToTest]} Low: {series.LowPrices[barIndexToTest]} at evalIndex {series.OpenTimes[evalIndex]}");
                        return true;
                    }
                }
                else if (!forBullishSignal && fvg.Type == FVGType.Bearish)
                {
                     if (series.HighPrices[barIndexToTest] >= fvg.Bottom && series.LowPrices[barIndexToTest] <= fvg.Top) 
                    {
                        //Print($"Bearish FVG {fvg.Bottom}-{fvg.Top} (formed after {fvg.StartTime}) tested by bar {series.OpenTimes[barIndexToTest]} High: {series.HighPrices[barIndexToTest]} at evalIndex {series.OpenTimes[evalIndex]}");
                        return true;
                    }
                }
            }
            return false;
        }
        
        // --- Fractal Helpers ---
        private List<double> GetFractalHighs(Bars series, int lookback)
        {
            var highs = new List<double>();
            // Standard fractal: High[i] > High[i-1] && High[i] > High[i-2] && High[i] > High[i+1] && High[i] > High[i+2]
            for (int i = 2; i < series.Count - lookback -2 ; i++) // Iterate up to 'lookback' bars ago
            {
                 int targetBar = series.Count - 1 - i;
                 if (targetBar < 2 || targetBar >= series.Count - 2) continue;

                if (series.HighPrices[targetBar] > series.HighPrices[targetBar - 1] &&
                    series.HighPrices[targetBar] > series.HighPrices[targetBar - 2] &&
                    series.HighPrices[targetBar] > series.HighPrices[targetBar + 1] &&
                    series.HighPrices[targetBar] > series.HighPrices[targetBar + 2])
                {
                    highs.Add(series.HighPrices[targetBar]);
                }
            }
            return highs.Distinct().OrderByDescending(h => h).Take(5).ToList(); // Return up to 5 most recent distinct fractals
        }

        private List<double> GetFractalLows(Bars series, int lookback)
        {
            var lows = new List<double>();
            // Standard fractal: Low[i] < Low[i-1] && Low[i] < Low[i-2] && Low[i] < Low[i+1] && Low[i] < Low[i+2]
            for (int i = 2; i < series.Count - lookback - 2; i++)
            {
                int targetBar = series.Count - 1 - i;
                if (targetBar < 2 || targetBar >= series.Count - 2) continue;

                if (series.LowPrices[targetBar] < series.LowPrices[targetBar - 1] &&
                    series.LowPrices[targetBar] < series.LowPrices[targetBar - 2] &&
                    series.LowPrices[targetBar] < series.LowPrices[targetBar + 1] &&
                    series.LowPrices[targetBar] < series.LowPrices[targetBar + 2])
                {
                    lows.Add(series.LowPrices[targetBar]);
                }
            }
            return lows.Distinct().OrderBy(l => l).Take(5).ToList(); // Return up to 5 most recent distinct fractals
        }


        protected override void OnStop()
        {
            Print("My15mOFBot stopped.");
        }
    }
} 