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
    public class FifteenMinuteOFBot : Robot
    {
        [Parameter("Symbols", DefaultValue = "EURUSD,GBPUSD,XAUUSD")]
        public string SymbolsToTrade { get; set; }

        [Parameter("Risk Per Trade (%)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPercent { get; set; }

        [Parameter("Min RR", DefaultValue = 1.5, MinValue = 0.5)]
        public double MinRR { get; set; }

        [Parameter("Max RR", DefaultValue = 3.0, MinValue = 1.0)]
        public double MaxRR { get; set; }

        private static readonly TimeFrame H1TimeFrame = TimeFrame.Hour;
        private static readonly TimeFrame M15TimeFrame = TimeFrame.Minute15;

        private string[] _symbolsToTradeArray;
        private Dictionary<string, Bars> _m15BarsDict;
        private Dictionary<string, Bars> _h1BarsDict;
        private Dictionary<string, DateTime> _lastM15BarProcessedTime;
        
        private int _tradesThisMonth = 0; // Needs persistent storage for real use. Placeholder.
        // private const int MaxAllowedMonthlyTrades = 25; // TODO: Make this a parameter or handle dynamically


        protected override void OnStart()
        {
            _symbolsToTradeArray = SymbolsToTrade.Split(',').Select(s => s.Trim().ToUpper()).ToArray();
            _m15BarsDict = new Dictionary<string, Bars>();
            _h1BarsDict = new Dictionary<string, Bars>();
            _lastM15BarProcessedTime = new Dictionary<string, DateTime>();

            Print($"Bot starting. Symbols: {string.Join(", ", _symbolsToTradeArray)}, Risk: {RiskPercent}%, MinRR: {MinRR}, MaxRR: {MaxRR}");
            Print("Reminder: Monthly trade limit (10-25) needs robust tracking (e.g., persistent storage or history analysis). Current implementation has a placeholder counter.");

            foreach (var symbolNameIter in _symbolsToTradeArray)
            {
                var symbol = Symbols.GetSymbol(symbolNameIter);
                if (symbol == null)
                {
                    Print($"Warning: Symbol {symbolNameIter} not found or not available in MarketWatch. Skipping.");
                    continue;
                }

                var m15Bars = MarketData.GetBars(M15TimeFrame, symbolNameIter);
                var h1Bars = MarketData.GetBars(H1TimeFrame, symbolNameIter);

                _m15BarsDict[symbolNameIter] = m15Bars;
                _h1BarsDict[symbolNameIter] = h1Bars;
                _lastM15BarProcessedTime[symbolNameIter] = DateTime.MinValue;

                m15Bars.BarOpened += (BarOpenedEventArgs eventArgs) => 
                {
                    OnM15BarOpened(symbolNameIter, _m15BarsDict[symbolNameIter]);
                };
                Print($"Subscribed to M15 bars for {symbolNameIter}. H1 Bars Ready: {h1Bars.Count > 0}, M15 Bars Ready: {m15Bars.Count > 0}");
            }
        }

        protected override void OnBar()
        {
            // Primarily using m15Bars.BarOpened. This OnBar is for the chart the bot is attached to.
            // Can be used for heartbeat or global checks if needed.
        }

        private void OnM15BarOpened(string symbolName, Bars m15Bars)
        {
            var h1Bars = _h1BarsDict[symbolName];
            var currentBarOpenTime = m15Bars.OpenTimes.LastValue;

            if (_lastM15BarProcessedTime.TryGetValue(symbolName, out var lastProcessedTime) && lastProcessedTime >= currentBarOpenTime)
            {
                return; // Already processed this bar
            }
            _lastM15BarProcessedTime[symbolName] = currentBarOpenTime;

            Print($"New M15 Bar for {symbolName} at {Server.Time:yyyy-MM-dd HH:mm} (Bar Open: {currentBarOpenTime:yyyy-MM-dd HH:mm}). Processing...");
            ProcessSymbol(symbolName, m15Bars, h1Bars);
        }

        private void ProcessSymbol(string symbolName, Bars m15Bars, Bars h1Bars)
        {
            const int requiredH1Bars = 50; 
            const int requiredM15Bars = 100;

            if (h1Bars.Count < requiredH1Bars || m15Bars.Count < requiredM15Bars)
            {
                Print($"[{symbolName}] Not enough data. H1: {h1Bars.Count}/{requiredH1Bars}, M15: {m15Bars.Count}/{requiredM15Bars}");
                return;
            }

            var h1OrderFlow = DetermineH1OrderFlow(h1Bars, symbolName);
            if (h1OrderFlow == TrendDirection.Uncertain)
            {
                Print($"[{symbolName}] Uncertain H1 Order Flow. No action.");
                return;
            }
            Print($"[{symbolName}] H1 Order Flow: {h1OrderFlow}");

            var setupInfo = CheckM15SetupConditions(m15Bars, h1Bars, h1OrderFlow, symbolName);
            if (!setupInfo.HasSetup)
            {
                // Print($"[{symbolName}-M15] No valid setup found."); // Detailed logs in CheckM15SetupConditions
                return;
            }
            Print($"[{symbolName}-M15] Valid setup found. Potential SL base: {setupInfo.PotentialStopLossPriceForSetup:F5}, Potential TP: {setupInfo.PotentialTakeProfitPrice:F5}. Waiting for entry trigger...");
            
            var entrySignal = CheckM15EntryTrigger(m15Bars, h1OrderFlow, setupInfo, symbolName);
            if (!entrySignal.HasSignal)
            {
                // Print($"[{symbolName}-M15] Setup found, but no entry trigger."); // Detailed logs in CheckM15EntryTrigger
                return;
            }
            Print($"[{symbolName}-M15] Entry trigger identified! Type: {entrySignal.TradeType}, Price: {entrySignal.EntryPrice:F5}, SL: {entrySignal.StopLossPrice:F5}, TP: {entrySignal.TakeProfitPrice:F5}");

            if (IsTradeOpenForSymbol(symbolName))
            {
                Print($"[{symbolName}] Trade already open. Skipping new entry.");
                return;
            }
            
            // Conceptual check for monthly trade limits
            // if (IsMaxTradesPerMonthReached()) {
            //     Print($"[{symbolName}] Max monthly trades reached. Skipping.");
            //     return;
            // }

            ExecuteTrade(symbolName, entrySignal.TradeType, entrySignal.EntryPrice, entrySignal.StopLossPrice, entrySignal.TakeProfitPrice);
        }

        private enum TrendDirection { Bullish, Bearish, Uncertain }

        private struct M15SetupInfo
        {
            public bool HasSetup;
            public bool LiquidityGrab15M;
            public bool TwoPointsOF15M;
            public bool ValidTargetAndRR;
            public double PotentialTakeProfitPrice;
            public double PotentialStopLossPriceForSetup; // SL based on the elements forming the 3 rules
        }

        private struct M15EntrySignal
        {
            public bool HasSignal;
            public double EntryPrice;
            public double StopLossPrice;
            public double TakeProfitPrice;
            public TradeType TradeType;
        }
        
        public struct FVGZone
        {
            public double Top; // Higher price of the FVG zone
            public double Bottom; // Lower price of the FVG zone
            public int FormationBarIndex; // Index of the 3rd candle that confirms the FVG
            public DateTime FormationTime; // OpenTime of the 3rd candle
            public bool IsBullish; // True for Bullish FVG (gap below price), False for Bearish FVG (gap above price)
            public bool IsMitigated; // Has price touched/entered the FVG
            public bool IsFilled; // Has price completely traded through the FVG

            public double MidPoint => Bottom + (Top - Bottom) / 2;

            public override string ToString()
            {
                return $"{(IsBullish ? "Bullish" : "Bearish")} FVG ({FormationTime:MM-dd HH:mm}): {Bottom:F5} - {Top:F5}";
            }
        }

        public struct SwingPoint
        {
            public int BarIndex;
            public double Price;
            public DateTime Time;
            public bool IsHigh; // True if Swing High, False if Swing Low

            public override string ToString()
            {
                return $"{(IsHigh ? "High" : "Low")} at {Time:MM-dd HH:mm}: {Price:F5} (Idx: {BarIndex})";
            }
        }


        private TrendDirection DetermineH1OrderFlow(Bars h1Bars, string symbolName)
        {
            Print($"[{symbolName}-H1] Determining Order Flow...");
            
            int currentIndex = h1Bars.Count - 1;
            if (currentIndex < 0) return TrendDirection.Uncertain;

            const int lookbackH1Period = 60; // Main lookback for swings and FVGs, e.g., 2.5 days of H1 bars
            const int swingStrength = 2; // Strength for swing point detection
            const int maxBarsAgoForFvgTest = 5; // Test must be within the last N bars from current
            const int maxBarsFvgFormationToTest = 15; // FVG must not be stale when tested (less than 15 bars old)
            const int maxBarsLiqGrabToFvgFormation = 20; // Liquidity grab must be reasonably close to FVG formation (less than 20 bars)
            const int minBarsBetweenSpAndGrab = 1; // Ensure grab is not the same bar as SP formation

            List<SwingPoint> allSwingPoints = GetSwingPoints(h1Bars, lookbackH1Period, swingStrength)
                                              .OrderByDescending(sp => sp.BarIndex).ToList(); // Newest first
            List<FVGZone> allFvgZones = IdentifyFVGZones(h1Bars, lookbackH1Period)
                                          .OrderByDescending(fvg => fvg.FormationBarIndex).ToList(); // Newest first

            if (allSwingPoints.Count == 0 || allFvgZones.Count == 0)
            {
                Print($"[{symbolName}-H1] Not enough swing points ({allSwingPoints.Count}) or FVG zones ({allFvgZones.Count}) in the last {lookbackH1Period} bars for OF detection.");
                return TrendDirection.Uncertain;
            }

            // Iterate backwards from the most recent bars to check for a test bar
            for (int testBarOffset = 0; testBarOffset < maxBarsAgoForFvgTest; testBarOffset++)
            {
                int testBarIndex = currentIndex - testBarOffset;
                // Ensure testBarIndex is valid and there are enough historical bars for checks
                if (testBarIndex < swingStrength + minBarsBetweenSpAndGrab + 2) continue; // Need enough history for SP, grab, FVG, test

                foreach (var fvg in allFvgZones)
                {
                    // FVG must form before the testBarIndex
                    if (fvg.FormationBarIndex >= testBarIndex) continue;
                    // FVG must not be too old relative to the test bar
                    if (testBarIndex - fvg.FormationBarIndex > maxBarsFvgFormationToTest) continue;
                    // FVG itself should not be too ancient in the lookback period (e.g. formed in the latest 2/3 of lookback)
                    if (currentIndex - fvg.FormationBarIndex > lookbackH1Period * 2 / 3 && testBarOffset > 0) continue; 

                    bool fvgTestedThisBar = false;
                    string fvgTypeString = fvg.IsBullish ? "Bullish" : "Bearish";

                    if (fvg.IsBullish)
                    {
                        if (h1Bars.LowPrices[testBarIndex] <= fvg.Top && h1Bars.HighPrices[testBarIndex] >= fvg.Bottom)
                            fvgTestedThisBar = true;
                    }
                    else // Bearish FVG
                    {
                        if (h1Bars.HighPrices[testBarIndex] >= fvg.Bottom && h1Bars.LowPrices[testBarIndex] <= fvg.Top)
                            fvgTestedThisBar = true;
                    }

                    if (fvgTestedThisBar)
                    {
                        Print($"[{symbolName}-H1] Candidate: {fvgTypeString} FVG {fvg} tested by bar {testBarIndex} ({h1Bars.OpenTimes[testBarIndex]:MM-dd HH:mm}). Looking for prior liquidity grab.");

                        foreach (var sp in allSwingPoints)
                        {
                            // Swing point must be clearly before FVG formation
                            if (sp.BarIndex >= fvg.FormationBarIndex - 1) continue; // SP must be before the 3-bar FVG pattern starts
                            
                            // Heuristic: Swing point itself isn't excessively old relative to the FVG it's associated with
                            if (fvg.FormationBarIndex - sp.BarIndex > maxBarsLiqGrabToFvgFormation + (lookbackH1Period / 4)) continue;

                            if (fvg.IsBullish && !sp.IsHigh) // Tested a Bullish FVG, need grab of a Swing Low
                            {
                                // Check for a bar between sp.BarIndex + minBarsBetweenSpAndGrab and fvg.FormationBarIndex -1 that grabbed liquidity
                                for (int grabAttemptBarIndex = sp.BarIndex + minBarsBetweenSpAndGrab; grabAttemptBarIndex < fvg.FormationBarIndex; grabAttemptBarIndex++)
                                {
                                    // The grab event should not be too far from the FVG formation candle itself
                                    if (fvg.FormationBarIndex - grabAttemptBarIndex > maxBarsLiqGrabToFvgFormation) continue; // Check from end of grab window
                                    if (grabAttemptBarIndex - sp.BarIndex > maxBarsLiqGrabToFvgFormation) break; // Check from start of grab window

                                    if (h1Bars.LowPrices[grabAttemptBarIndex] < sp.Price)
                                    {
                                        Print($"[{symbolName}-H1] Bullish H1 OF Confirmed: LiqGrab below SwingLow {sp} by bar {grabAttemptBarIndex} ({h1Bars.OpenTimes[grabAttemptBarIndex]:MM-dd HH:mm}), then {fvgTypeString} FVG {fvg} formed, then tested by bar {testBarIndex} ({h1Bars.OpenTimes[testBarIndex]:MM-dd HH:mm}).");
                                        return TrendDirection.Bullish;
                                    }
                                }
                            }
                            else if (!fvg.IsBullish && sp.IsHigh) // Tested a Bearish FVG, need grab of a Swing High
                            {
                                for (int grabAttemptBarIndex = sp.BarIndex + minBarsBetweenSpAndGrab; grabAttemptBarIndex < fvg.FormationBarIndex; grabAttemptBarIndex++)
                                {
                                    if (fvg.FormationBarIndex - grabAttemptBarIndex > maxBarsLiqGrabToFvgFormation) continue;
                                    if (grabAttemptBarIndex - sp.BarIndex > maxBarsLiqGrabToFvgFormation) break;

                                    if (h1Bars.HighPrices[grabAttemptBarIndex] > sp.Price)
                                    {
                                        Print($"[{symbolName}-H1] Bearish H1 OF Confirmed: LiqGrab above SwingHigh {sp} by bar {grabAttemptBarIndex} ({h1Bars.OpenTimes[grabAttemptBarIndex]:MM-dd HH:mm}), then {fvgTypeString} FVG {fvg} formed, then tested by bar {testBarIndex} ({h1Bars.OpenTimes[testBarIndex]:MM-dd HH:mm}).");
                                        return TrendDirection.Bearish;
                                    }
                                }
                            }
                        }
                    } // End if fvgTestedThisBar
                } // End foreach fvg
            } // End for testBarOffset

            Print($"[{symbolName}-H1] No definitive H1 Order Flow pattern (LiqGrab -> FVG form -> FVG test) found recently.");
            return TrendDirection.Uncertain;
        }

        private M15SetupInfo CheckM15SetupConditions(Bars m15Bars, Bars h1Bars, TrendDirection h1OrderFlow, string symbolName)
        {
            Print($"[{symbolName}-M15] Checking setup conditions for {h1OrderFlow} flow...");
            var setupInfo = new M15SetupInfo { HasSetup = false };
            int m15LookbackPeriod = 30; // Example lookback bars for M15 conditions

            // Rule 1: Снятие ликвидности на 15м
            bool m15LiqGrab = HasM15LiquidityGrab(m15Bars, h1OrderFlow, m15LookbackPeriod, out int liqGrabCandleIndexForRule1, out double _); // liqGrabLevel not used here
            if (!m15LiqGrab) { Print($"[{symbolName}-M15] Rule 1 Failed: No M15 liquidity grab."); return setupInfo; }
            setupInfo.LiquidityGrab15M = true;
            Print($"[{symbolName}-M15] Rule 1 Passed: M15 Liquidity grab detected.");

            // Rule 2: 2 пункта 15мОФ
            bool twoPointsOF = HasTwoPointsM15OF(m15Bars, h1OrderFlow, m15LookbackPeriod, liqGrabCandleIndexForRule1, out int slDefiningCandleIndexForRule2);
            if (!twoPointsOF) { Print($"[{symbolName}-M15] Rule 2 Failed: Not 2 points of M15 OF."); return setupInfo; }
            setupInfo.TwoPointsOF15M = true;
            Print($"[{symbolName}-M15] Rule 2 Passed: Two points of M15 OF detected.");

            // Define potential SL for RR check based on Rule 1 or Rule 2's defining candles
            // The SL definition: "за свечу которая дала Full Fill FVG или сняла ликвидность локальную"
            // This refers to elements of Rule 2 (most likely) or Rule 1.
            int slBaseCandleIndex = slDefiningCandleIndexForRule2 != -1 ? slDefiningCandleIndexForRule2 : liqGrabCandleIndexForRule1;
            if(slBaseCandleIndex == -1) { Print($"[{symbolName}-M15] Cannot determine SL base candle index for RR check."); return setupInfo; }
            
            double preliminarySL = CalculateActualSLPrice(m15Bars, slBaseCandleIndex, h1OrderFlow, false); // isFVGFill=false is a guess, depends on what Rule2 found
            if (preliminarySL == 0) { Print($"[{symbolName}-M15] Failed to calculate preliminary SL for RR check."); return setupInfo; }
            
            // For RR check, hypothetical entry is current price, or more sophisticated logic later
            var symbol = Symbols.GetSymbol(symbolName);
            double hypotheticalEntry = (h1OrderFlow == TrendDirection.Bullish) ? symbol.Ask : symbol.Bid;


            // Rule 3: Цель для выставления Take-Profit c допустимым RR
            bool validTarget = FindValidTargetAndRR(m15Bars, h1Bars, h1OrderFlow, hypotheticalEntry, preliminarySL, out double tpTargetPrice);
            if (!validTarget) { Print($"[{symbolName}-M15] Rule 3 Failed: No valid TP target with RR {MinRR}-{MaxRR}."); return setupInfo; }
            setupInfo.ValidTargetAndRR = true;
            setupInfo.PotentialTakeProfitPrice = tpTargetPrice;
            setupInfo.PotentialStopLossPriceForSetup = preliminarySL; // This SL is based on setup, actual entry SL might differ slightly
            Print($"[{symbolName}-M15] Rule 3 Passed: Valid TP target {tpTargetPrice:F5} found.");
            
            setupInfo.HasSetup = true;
            return setupInfo;
        }

        private M15EntrySignal CheckM15EntryTrigger(Bars m15Bars, TrendDirection h1OrderFlow, M15SetupInfo setupInfo, string symbolName)
        {
            Print($"[{symbolName}-M15] Checking entry trigger for {h1OrderFlow} flow...");
            var signal = new M15EntrySignal { HasSignal = false };
            int lookbackForTrigger = 5; // Check last few candles for a trigger

            // Option 1: Test of an imbalance (FVG)
            if (HasRecentFVGTest(m15Bars, h1OrderFlow, lookbackForTrigger, out double fvgEntryPrice, out int fvgTestCandleIndex))
            {
                double actualSL = CalculateActualSLPrice(m15Bars, fvgTestCandleIndex, h1OrderFlow, true /*isFVGFill*/);
                if (actualSL != 0)
                {
                    double rr = CalculateRR(fvgEntryPrice, actualSL, setupInfo.PotentialTakeProfitPrice, (h1OrderFlow == TrendDirection.Bullish) ? TradeType.Buy : TradeType.Sell);
                    if (rr >= MinRR && rr <= MaxRR)
                    {
                        signal.HasSignal = true;
                        signal.EntryPrice = fvgEntryPrice; 
                        signal.StopLossPrice = actualSL;
                        signal.TakeProfitPrice = setupInfo.PotentialTakeProfitPrice;
                        signal.TradeType = (h1OrderFlow == TrendDirection.Bullish) ? TradeType.Buy : TradeType.Sell;
                        Print($"[{symbolName}-M15] Trigger: FVG Test. Entry: {fvgEntryPrice:F5}, SL: {actualSL:F5}, TP: {setupInfo.PotentialTakeProfitPrice:F5}, RR: {rr:F2}");
                        return signal;
                    }
                    else Print($"[{symbolName}-M15] Trigger: FVG Test, but RR {rr:F2} (E:{fvgEntryPrice:F5}, SL:{actualSL:F5}, TP:{setupInfo.PotentialTakeProfitPrice:F5}) out of range.");
                } else Print($"[{symbolName}-M15] Trigger: FVG Test, but failed to calculate SL.");
            }

            // Option 2: Local liquidity grab
            if (!signal.HasSignal && HasRecentLocalLiquidityGrab(m15Bars, h1OrderFlow, lookbackForTrigger, out double liqGrabEntryPrice, out int liqGrabCandleIndex_trigger))
            {
                double actualSL = CalculateActualSLPrice(m15Bars, liqGrabCandleIndex_trigger, h1OrderFlow, false /*isFVGFill*/);
                 if (actualSL != 0)
                {
                    double rr = CalculateRR(liqGrabEntryPrice, actualSL, setupInfo.PotentialTakeProfitPrice, (h1OrderFlow == TrendDirection.Bullish) ? TradeType.Buy : TradeType.Sell);
                    if (rr >= MinRR && rr <= MaxRR)
                    {
                        signal.HasSignal = true;
                        signal.EntryPrice = liqGrabEntryPrice;
                        signal.StopLossPrice = actualSL;
                        signal.TakeProfitPrice = setupInfo.PotentialTakeProfitPrice;
                        signal.TradeType = (h1OrderFlow == TrendDirection.Bullish) ? TradeType.Buy : TradeType.Sell;
                        Print($"[{symbolName}-M15] Trigger: Local Liq Grab. Entry: {liqGrabEntryPrice:F5}, SL: {actualSL:F5}, TP: {setupInfo.PotentialTakeProfitPrice:F5}, RR: {rr:F2}");
                        return signal;
                    }
                    else Print($"[{symbolName}-M15] Trigger: Local Liq Grab, but RR {rr:F2} (E:{liqGrabEntryPrice:F5}, SL:{actualSL:F5}, TP:{setupInfo.PotentialTakeProfitPrice:F5}) out of range.");
                } else Print($"[{symbolName}-M15] Trigger: Local Liq Grab, but failed to calculate SL.");
            }
            
            if(!signal.HasSignal) Print($"[{symbolName}-M15] No valid entry trigger found on current evaluation.");
            return signal;
        }

        private double CalculateActualSLPrice(Bars bars, int triggerCandleIndex, TrendDirection flow, bool isFromFVGFillContext)
        {
            if (triggerCandleIndex < 0 || triggerCandleIndex >= bars.Count) { Print("Invalid triggerCandleIndex for SL calc."); return 0; }
            var symbol = Symbols.GetSymbol(bars.SymbolName);
            if (symbol == null) { Print($"Symbol {bars.SymbolName} not found for SL calc."); return 0; }

            double slAdjustment = symbol.PipSize * 2; // Buffer of 2 pips

            if (flow == TrendDirection.Bullish) // Buy trade, SL below low
            {
                // SL is "за свечу которая дала Full Fill FVG или сняла ликвидность локальную"
                return bars.LowPrices[triggerCandleIndex] - slAdjustment;
            }
            else if (flow == TrendDirection.Bearish) // Sell trade, SL above high
            {
                return bars.HighPrices[triggerCandleIndex] + slAdjustment;
            }
            return 0; // Should not happen if flow is Bullish/Bearish
        }
        
        private double CalculateRR(double entryPrice, double stopLossPrice, double takeProfitPrice, TradeType tradeType)
        {
            if (entryPrice == 0 || stopLossPrice == 0 || takeProfitPrice == 0) return 0;
            double riskDistance, rewardDistance;

            if (tradeType == TradeType.Buy)
            {
                riskDistance = entryPrice - stopLossPrice;
                rewardDistance = takeProfitPrice - entryPrice;
            }
            else 
            {
                riskDistance = stopLossPrice - entryPrice;
                rewardDistance = entryPrice - takeProfitPrice;
            }

            if (riskDistance <= 0 || rewardDistance <=0) return 0; // Invalid SL/TP placement or no profit potential
            return rewardDistance / riskDistance;
        }

        private bool HasM15LiquidityGrab(Bars m15Bars, TrendDirection h1OrderFlow, int lookback, out int grabCandleIndex, out double grabLevel)
        {
            grabCandleIndex = -1;
            grabLevel = 0;
            
            Print($"[{m15Bars.SymbolName}-M15] Checking for M15 Liquidity Grab in direction of {h1OrderFlow} within last {lookback} bars.");

            int currentIndex = m15Bars.Count - 1;
            if (currentIndex < 1) return false; // Need at least current and previous bar for any logic

            const int m15SwingStrength = 1; // Simpler swings for M15 are often sufficient
            // Lookback for SPs should be a bit more than general lookback to find SPs that were grabbed recently
            List<SwingPoint> m15SwingPoints = GetSwingPoints(m15Bars, lookback + (m15SwingStrength * 2) + 5, m15SwingStrength)
                                            .OrderByDescending(sp => sp.BarIndex).ToList(); // Newest first

            if (!m15SwingPoints.Any())
            {
                Print($"[{m15Bars.SymbolName}-M15] No M15 swing points found to check for liquidity grabs.");
                return false;
            }

            // We check from the current bar (offset 0) up to 'lookback' bars ago for the grab event itself.
            // The swing point that was grabbed could be older.
            for (int barOffset = 0; barOffset < lookback; barOffset++)
            {
                int currentBarIndex = currentIndex - barOffset;
                if (currentBarIndex < m15SwingStrength + 1) continue; // Ensure enough history for the swing to have formed and been grabbed

                foreach (var sp in m15SwingPoints)
                {
                    // Swing point must be older than the bar we are checking for a grab
                    if (sp.BarIndex >= currentBarIndex) continue;
                    // Swing point shouldn't be excessively old, relative to the lookback window for the grab
                    if (currentBarIndex - sp.BarIndex > lookback + 10) continue; 

                    bool grabbed = false;
                    if (h1OrderFlow == TrendDirection.Bullish && !sp.IsHigh) // H1 OF is Bullish, look for grab of a Swing Low
                    {
                        if (m15Bars.LowPrices[currentBarIndex] < sp.Price)
                        {
                            // Basic reaction: current bar closes above its open OR next bar is bullish
                            bool reaction = (m15Bars.ClosePrices[currentBarIndex] > m15Bars.OpenPrices[currentBarIndex]);
                            if (currentBarIndex + 1 <= currentIndex) // check next bar if available
                            {
                                reaction = reaction || (m15Bars.ClosePrices[currentBarIndex + 1] > m15Bars.OpenPrices[currentBarIndex + 1]);
                            }
                            else // if it's the most current bar, rely on its own close vs open
                            {
                                reaction = m15Bars.ClosePrices[currentBarIndex] > m15Bars.OpenPrices[currentBarIndex];
                            }

                            if (reaction)
                            {
                                grabCandleIndex = currentBarIndex;
                                grabLevel = sp.Price;
                                grabbed = true;
                                Print($"[{m15Bars.SymbolName}-M15] Bullish M15 LiqGrab: SwingLow {sp} at {sp.Price:F5} grabbed by bar {grabCandleIndex} ({m15Bars.OpenTimes[grabCandleIndex]:MM-dd HH:mm}) Low: {m15Bars.LowPrices[grabCandleIndex]:F5}. Reaction confirmed.");
                            }
                        }
                    }
                    else if (h1OrderFlow == TrendDirection.Bearish && sp.IsHigh) // H1 OF is Bearish, look for grab of a Swing High
                    {
                        if (m15Bars.HighPrices[currentBarIndex] > sp.Price)
                        {
                             bool reaction = (m15Bars.ClosePrices[currentBarIndex] < m15Bars.OpenPrices[currentBarIndex]);
                            if (currentBarIndex + 1 <= currentIndex) // check next bar if available
                            {
                                reaction = reaction || (m15Bars.ClosePrices[currentBarIndex + 1] < m15Bars.OpenPrices[currentBarIndex + 1]);
                            }
                            else
                            {
                                reaction = m15Bars.ClosePrices[currentBarIndex] < m15Bars.OpenPrices[currentBarIndex];
                            }

                            if (reaction)
                            {
                                grabCandleIndex = currentBarIndex;
                                grabLevel = sp.Price;
                                grabbed = true;
                                Print($"[{m15Bars.SymbolName}-M15] Bearish M15 LiqGrab: SwingHigh {sp} at {sp.Price:F5} grabbed by bar {grabCandleIndex} ({m15Bars.OpenTimes[grabCandleIndex]:MM-dd HH:mm}) High: {m15Bars.HighPrices[grabCandleIndex]:F5}. Reaction confirmed.");
                            }
                        }
                    }

                    if (grabbed) return true;
                }
            }
            Print($"[{m15Bars.SymbolName}-M15] No M15 liquidity grab with reaction found in direction {h1OrderFlow}.");
            return false; 
        }

        private bool HasTwoPointsM15OF(Bars m15Bars, TrendDirection h1OrderFlow, int lookback, int mainLiqGrabCandleIndexForRule1, out int slDefiningCandleIndex)
        {
            slDefiningCandleIndex = -1;
            Print($"[{m15Bars.SymbolName}-M15] Rule 2: Checking for Two Points of M15 OF after initial grab at index {mainLiqGrabCandleIndexForRule1}. Lookback {lookback} bars.");

            if (mainLiqGrabCandleIndexForRule1 < 0 || mainLiqGrabCandleIndexForRule1 >= m15Bars.Count -1) // Need at least one bar after grab for next point
            {
                Print($"[{m15Bars.SymbolName}-M15] Rule 2: Invalid mainLiqGrabCandleIndexForRule1: {mainLiqGrabCandleIndexForRule1}");
                return false;
            }

            // Define a search window starting AFTER the main liquidity grab from Rule 1.
            // The lookback here is relative to the current bar, but we only care about events after mainLiqGrabCandleIndexForRule1.
            int searchStartIndex = mainLiqGrabCandleIndexForRule1 + 1;
            int currentBarIndex = m15Bars.Count - 1;
            // Ensure lookback for elements doesn't go too far, relative to current processing bar.
            // Max index to search up to for the *end* of a 2-point sequence.
            int searchEndIndex = currentBarIndex; 

            // Scenario 1: Two local liquidity grabs
            // The first grab is mainLiqGrabCandleIndexForRule1.
            // We need a second grab after this one.
            if (FindSecondaryLiquidityGrab(m15Bars, h1OrderFlow, searchStartIndex, searchEndIndex, lookback, out int secondLiqGrabCandleIndex, out _))
            {
                Print($"[{m15Bars.SymbolName}-M15] Rule 2 Variant 1 Passed: Second M15 Liquidity grab detected at index {secondLiqGrabCandleIndex}.");
                slDefiningCandleIndex = secondLiqGrabCandleIndex;
                return true;
            }

            // Scenario 2: One local liquidity grab (Rule 1) + one M15 FVG test
            // First grab is mainLiqGrabCandleIndexForRule1.
            // Look for an FVG formed after mainLiqGrabCandleIndexForRule1 and then tested.
            if (FindFVGTest(m15Bars, h1OrderFlow, searchStartIndex, searchEndIndex, lookback, mainLiqGrabCandleIndexForRule1, out int firstFvgTestCandleIndex, out FVGZone identifiedFvgForScenario2))
            {
                Print($"[{m15Bars.SymbolName}-M15] Rule 2 Variant 2 Passed: M15 FVG {identifiedFvgForScenario2} test detected at index {firstFvgTestCandleIndex}.");
                slDefiningCandleIndex = firstFvgTestCandleIndex; // Candle that tested the FVG
                return true;
            }
            
            // Scenario 3: Two M15 FVG tests (Rule 1's grab is implicitly the "setup" for the first FVG)
            // This means we need to find a first FVG test, and then a second FVG test after the first one.
            if (FindFVGTest(m15Bars, h1OrderFlow, searchStartIndex, searchEndIndex, lookback, mainLiqGrabCandleIndexForRule1, out int firstFvgTestCandleIdxS3, out FVGZone firstFvgS3))
            {
                Print($"[{m15Bars.SymbolName}-M15] Rule 2 Variant 3: First FVG {firstFvgS3} test found at {firstFvgTestCandleIdxS3}. Looking for second FVG test.");
                // Now look for a *second* FVG test, starting after the first FVG test candle
                int searchStartForSecondFvg = firstFvgTestCandleIdxS3 + 1; 
                if (FindFVGTest(m15Bars, h1OrderFlow, searchStartForSecondFvg, searchEndIndex, lookback, firstFvgTestCandleIdxS3, out int secondFvgTestCandleIdxS3, out FVGZone secondFvgS3))
                {
                    Print($"[{m15Bars.SymbolName}-M15] Rule 2 Variant 3 Passed: Second M15 FVG {secondFvgS3} test detected at index {secondFvgTestCandleIdxS3}.");
                    slDefiningCandleIndex = secondFvgTestCandleIdxS3;
                    return true;
                }
                 else { Print($"[{m15Bars.SymbolName}-M15] Rule 2 Variant 3: First FVG test found, but no second FVG test after it."); }
            }

            Print($"[{m15Bars.SymbolName}-M15] Rule 2 Failed: None of the 2-point OF variations were met.");
            return false;
        }

        // Helper for HasTwoPointsM15OF: Finds a liquidity grab after a specific index
        private bool FindSecondaryLiquidityGrab(Bars m15Bars, TrendDirection h1OrderFlow, int searchStartIndex, int searchEndIndex, int lookbackForSwings, out int grabCandleIndex, out double grabLevel)
        {
            grabCandleIndex = -1;
            grabLevel = 0;

            // We need to find a swing point that formed *after* or *at* searchStartIndex effectively (or rather, a grab of such SP).
            // The grab itself must happen at or after searchStartIndex.
            List<SwingPoint> m15SwingPoints = GetSwingPoints(m15Bars, Math.Max(lookbackForSwings, m15Bars.Count - searchStartIndex + 5), 1) // More sensitive swings for M15 OF points
                                            .OrderByDescending(sp => sp.BarIndex).ToList(); 

            for (int barIdx = searchStartIndex; barIdx <= searchEndIndex; barIdx++)
            {
                if (barIdx < 1) continue; 

                foreach (var sp in m15SwingPoints)
                {
                    if (sp.BarIndex >= barIdx) continue; // Swing must be before the grab candle
                    // Ensure swing is not too old relative to our search window start, or relative to the grab candle
                    if (barIdx - sp.BarIndex > lookbackForSwings + 5) continue; 
                    if (sp.BarIndex < searchStartIndex - (lookbackForSwings/2) && barIdx > searchStartIndex + lookbackForSwings ) continue; // Avoid extremely old swings

                    bool grabbedThisBar = false;
                    if (h1OrderFlow == TrendDirection.Bullish && !sp.IsHigh) 
                    {
                        if (m15Bars.LowPrices[barIdx] < sp.Price)
                        {
                            bool reaction = (m15Bars.ClosePrices[barIdx] > m15Bars.OpenPrices[barIdx]) || (barIdx + 1 <= searchEndIndex && m15Bars.ClosePrices[barIdx+1] > m15Bars.OpenPrices[barIdx+1]);
                            if (reaction) { grabCandleIndex = barIdx; grabLevel = sp.Price; grabbedThisBar = true; }
                        }
                    }
                    else if (h1OrderFlow == TrendDirection.Bearish && sp.IsHigh) 
                    {
                        if (m15Bars.HighPrices[barIdx] > sp.Price)
                        {
                           bool reaction = (m15Bars.ClosePrices[barIdx] < m15Bars.OpenPrices[barIdx]) || (barIdx + 1 <= searchEndIndex && m15Bars.ClosePrices[barIdx+1] < m15Bars.OpenPrices[barIdx+1]);
                           if (reaction) { grabCandleIndex = barIdx; grabLevel = sp.Price; grabbedThisBar = true; }
                        }
                    }
                    if (grabbedThisBar) return true;
                }
            }
            return false;
        }

        // Helper for HasTwoPointsM15OF: Finds an FVG test after a specific index
        private bool FindFVGTest(Bars m15Bars, TrendDirection h1OrderFlow, int searchStartIndex, int searchEndIndex, int lookbackForFVGs, int eventMustBeAfterThisIndex, out int fvgTestCandleIndex, out FVGZone testedFvg)
        {
            fvgTestCandleIndex = -1;
            testedFvg = default(FVGZone);
            
            // FVGs should be identified in a relevant window.
            // The FVG formation index must be >= eventMustBeAfterThisIndex (or slightly after for clarity)
            // The test of the FVG must be within searchStartIndex and searchEndIndex.
            List<FVGZone> fvgZones = IdentifyFVGZones(m15Bars, Math.Max(lookbackForFVGs, m15Bars.Count - (eventMustBeAfterThisIndex - 3) + 5 )) // ensure window covers potential FVG formations
                                     .Where(fvg => fvg.IsBullish == (h1OrderFlow == TrendDirection.Bullish) && fvg.FormationBarIndex > eventMustBeAfterThisIndex) // FVG in direction of OF and after prior event
                                     .OrderByDescending(fvg => fvg.FormationBarIndex).ToList(); // Newest first

            if (!fvgZones.Any()) return false;

            for (int barIdx = searchStartIndex; barIdx <= searchEndIndex; barIdx++)
            {
                 if (barIdx < 1) continue;

                foreach (var fvg in fvgZones)
                {
                    if (fvg.FormationBarIndex >= barIdx) continue; // FVG must be formed before it can be tested by barIdx
                    // Ensure FVG is not excessively old relative to the test candle
                    if (barIdx - fvg.FormationBarIndex > lookbackForFVGs + 5) continue; 

                    bool fvgTestedThisBar = false;
                    if (fvg.IsBullish) // Bullish FVG, Bullish H1 OF
                    {
                        if (m15Bars.LowPrices[barIdx] <= fvg.Top && m15Bars.HighPrices[barIdx] >= fvg.Bottom) // Price entered/touched FVG
                        {
                             // Reaction: bar closes bullish, or next bar is bullish
                            bool reaction = (m15Bars.ClosePrices[barIdx] > m15Bars.OpenPrices[barIdx]) || (barIdx + 1 <= searchEndIndex && m15Bars.ClosePrices[barIdx+1] > m15Bars.OpenPrices[barIdx+1]);
                            if (reaction) { fvgTestedThisBar = true; }
                        }
                    }
                    else // Bearish FVG, Bearish H1 OF
                    {
                        if (m15Bars.HighPrices[barIdx] >= fvg.Bottom && m15Bars.LowPrices[barIdx] <= fvg.Top) // Price entered/touched FVG
                        {
                            bool reaction = (m15Bars.ClosePrices[barIdx] < m15Bars.OpenPrices[barIdx]) || (barIdx + 1 <= searchEndIndex && m15Bars.ClosePrices[barIdx+1] < m15Bars.OpenPrices[barIdx+1]);
                            if (reaction) { fvgTestedThisBar = true; }
                        }
                    }

                    if (fvgTestedThisBar)
                    {
                        fvgTestCandleIndex = barIdx;
                        testedFvg = fvg;
                        return true;
                    }
                }
            }
            return false;
        }

        private bool FindValidTargetAndRR(Bars m15Bars, Bars h1Bars, TrendDirection h1OrderFlow, double potentialEntry, double potentialSL, out double takeProfitTarget)
        {
            takeProfitTarget = 0;
            if (potentialEntry == 0 || potentialSL == 0)
            {
                Print($"[{m15Bars.SymbolName}-M15] Invalid potential entry ({potentialEntry:F5}) or SL ({potentialSL:F5}) for RR calculation.");
                return false;
            }

            Print($"[{m15Bars.SymbolName}-M15] Finding valid TP for {h1OrderFlow}. Entry: {potentialEntry:F5}, SL: {potentialSL:F5}. MinRR: {MinRR}, MaxRR: {MaxRR}");

            // Get fractals from both M15 and H1 timeframes
            // Lookback for fractals should be reasonable, e.g., last 100 M15 bars and 50 H1 bars
            var m15Fractals = GetFractals(m15Bars, 100, 2); 
            var h1Fractals = GetFractals(h1Bars, 50, 2); // Standard fractal is 2 bars on each side

            var potentialTargets = new List<double>();

            if (h1OrderFlow == TrendDirection.Bullish) // Looking for Buy, TP target is a high fractal
            {
                // M15 High Fractals above entry
                potentialTargets.AddRange(m15Fractals.Where(f => f.IsHigh && f.Price > potentialEntry).Select(f => f.Price));
                // H1 High Fractals above entry
                potentialTargets.AddRange(h1Fractals.Where(f => f.IsHigh && f.Price > potentialEntry).Select(f => f.Price));
                potentialTargets = potentialTargets.Distinct().OrderBy(p => p).ToList(); // Closest first
            }
            else // Looking for Sell, TP target is a low fractal
            {
                // M15 Low Fractals below entry
                potentialTargets.AddRange(m15Fractals.Where(f => !f.IsHigh && f.Price < potentialEntry).Select(f => f.Price));
                // H1 Low Fractals below entry
                potentialTargets.AddRange(h1Fractals.Where(f => !f.IsHigh && f.Price < potentialEntry).Select(f => f.Price));
                potentialTargets = potentialTargets.Distinct().OrderByDescending(p => p).ToList(); // Closest first
            }

            if (!potentialTargets.Any())
            {
                Print($"[{m15Bars.SymbolName}-M15] No suitable fractal targets found in the direction of {h1OrderFlow} above/below entry {potentialEntry:F5}.");
                return false;
            }

            foreach (var target in potentialTargets)
            {
                double rr = CalculateRR(potentialEntry, potentialSL, target, (h1OrderFlow == TrendDirection.Bullish) ? TradeType.Buy : TradeType.Sell);
                Print($"[{m15Bars.SymbolName}-M15] Checking target {target:F5}. RR: {rr:F2}");
                if (rr >= MinRR && rr <= MaxRR)
                {
                    takeProfitTarget = target;
                    Print($"[{m15Bars.SymbolName}-M15] Valid TP found: {takeProfitTarget:F5} with RR {rr:F2}.");
                    return true;
                }
            }

            Print($"[{m15Bars.SymbolName}-M15] No fractal targets found with RR between {MinRR} and {MaxRR}. Checked {potentialTargets.Count} potential targets.");
            return false;
        }

        private bool HasRecentFVGTest(Bars m15Bars, TrendDirection h1OrderFlow, int lookback, out double entryPrice, out int fvgTestCandleIndex)
        {
            entryPrice = 0; fvgTestCandleIndex = -1;
            Print($"[{m15Bars.SymbolName}-M15] Placeholder: HasRecentFVGTest - Needs Implementation");
            return false;
        }

        private bool HasRecentLocalLiquidityGrab(Bars m15Bars, TrendDirection h1OrderFlow, int lookback, out double entryPrice, out int grabCandleIndex)
        {
            entryPrice = 0; grabCandleIndex = -1;
            Print($"[{m15Bars.SymbolName}-M15] Placeholder: HasRecentLocalLiquidityGrab for entry - Needs Implementation");
            return false;
        }

        private void ExecuteTrade(string symbolName, TradeType tradeType, double entryPrice, double stopLossPrice, double takeProfitPrice)
        {
            var symbol = Symbols.GetSymbol(symbolName);
            if (symbol == null) { Print($"[{symbolName}] Cannot execute trade. Symbol not found."); return; }

            if (stopLossPrice == 0 || takeProfitPrice == 0 || entryPrice == 0) {
                Print($"[{symbolName}] Invalid SL ({stopLossPrice:F5}), TP ({takeProfitPrice:F5}) or Entry ({entryPrice:F5}). Trade aborted."); return;
            }
             // Additional checks for SL/TP validity relative to entry
            if (tradeType == TradeType.Buy && (entryPrice <= stopLossPrice || entryPrice >= takeProfitPrice)) {
                Print($"[{symbolName}] Invalid SL/TP for BUY. E:{entryPrice:F5} SL:{stopLossPrice:F5} TP:{takeProfitPrice:F5}"); return;
            }
            if (tradeType == TradeType.Sell && (entryPrice >= stopLossPrice || entryPrice <= takeProfitPrice)) {
                Print($"[{symbolName}] Invalid SL/TP for SELL. E:{entryPrice:F5} SL:{stopLossPrice:F5} TP:{takeProfitPrice:F5}"); return;
            }

            double volumeInUnits = CalculateVolume(symbol, entryPrice, stopLossPrice, tradeType);

            if (volumeInUnits < symbol.VolumeInUnitsMin) {
                Print($"[{symbolName}] Calculated volume {volumeInUnits} is less than MinVolume {symbol.VolumeInUnitsMin}. Risk: {RiskPercent}%. Trade aborted."); return;
            }
            if (volumeInUnits == 0) {
                 Print($"[{symbolName}] Calculated volume is 0. Trade aborted."); return;
            }


            string label = $"15mOF_{symbolName}_{Server.Time.Ticks}"; // Unique enough label
            var result = ExecuteMarketOrder(tradeType, symbol, volumeInUnits, label, stopLossPrice, takeProfitPrice);

            if (result.IsSuccessful) {
                Print($"[{symbolName}] TRADE EXECUTED: {tradeType} {symbolName} {volumeInUnits} @ {result.Position.EntryPrice:F5} (intended: {entryPrice:F5}), SL: {stopLossPrice:F5}, TP: {takeProfitPrice:F5}. Label: {label}");
                _tradesThisMonth++; // Placeholder increment
            } else {
                Print($"[{symbolName}] ERROR executing trade: {result.Error}. Vol: {volumeInUnits}, SL: {stopLossPrice:F5}, TP: {takeProfitPrice:F5}");
            }
        }

        private double CalculateVolume(Symbol tradeSymbol, double entryPrice, double stopLossPrice, TradeType tradeType)
        {
            double accountBalance = Account.Balance;
            double riskAmount = accountBalance * (RiskPercent / 100.0);
            double stopLossDistance = Math.Abs(entryPrice - stopLossPrice);

            if (stopLossDistance == 0) {
                Print($"[{tradeSymbol.Name}] Stop loss distance is zero. E:{entryPrice:F5}, SL:{stopLossPrice:F5}. Cannot calculate volume."); return 0;
            }
             // Check if SL distance is too small (e.g. less than a pip)
            if (stopLossDistance < tradeSymbol.PipSize) {
                Print($"[{tradeSymbol.Name}] Stop loss distance {stopLossDistance:F5} is less than PipSize {tradeSymbol.PipSize:F5}. Potentially too tight. Check logic.");
                // Depending on symbol, this might be okay or too small. For now, proceed but log.
            }


            // Monetary value of one pip (for one lot) in account currency.
            // For cTrader, Symbol.MonetaryValue(volume, type) is better but requires volume first.
            // PipValue is generally for 1 lot. Value of 1 pip for one unit = Symbol.PipValue / Symbol.LotSize
            // However, Symbol.TickValue is more direct for per-unit calculations.
            // TickValue is the value of a TickSize move for one unit of the base currency, in Account Currency.
            
            double pointsValuePerUnit = tradeSymbol.TickValue / tradeSymbol.TickSize; // Value of 1 point (TickSize) move for 1 unit
            double stopLossInPoints = stopLossDistance / tradeSymbol.TickSize; // SL distance in points

            if (pointsValuePerUnit <= 0 || stopLossInPoints <= 0) {
                Print($"[{tradeSymbol.Name}] Invalid pointsValuePerUnit ({pointsValuePerUnit}) or stopLossInPoints ({stopLossInPoints}). E:{entryPrice:F5} SL:{stopLossPrice:F5}");
                return 0;
            }

            double costPerUnitAtSL = stopLossInPoints * pointsValuePerUnit;
            if (costPerUnitAtSL <= 0) {
                 Print($"[{tradeSymbol.Name}] Invalid costPerUnitAtSL ({costPerUnitAtSL}). Cannot calculate volume."); return 0;
            }

            double volumeInUnits = riskAmount / costPerUnitAtSL;
            double normalizedVolume = tradeSymbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            
            // Final check for very small SL leading to huge volume, capped by MaxVolume anyway.
            if (normalizedVolume > tradeSymbol.VolumeInUnitsMax) {
                normalizedVolume = tradeSymbol.VolumeInUnitsMax;
                 Print($"[{tradeSymbol.Name}] Calculated volume {volumeInUnits} exceeds MaxVolume {tradeSymbol.VolumeInUnitsMax}. Capped to MaxVolume.");
            }

            if (normalizedVolume < tradeSymbol.VolumeInUnitsMin && normalizedVolume > 0) {
                 Print($"[{tradeSymbol.Name}] Normalized volume {normalizedVolume} is positive but < MinVolume {tradeSymbol.VolumeInUnitsMin}. Risk may be too small for min trade size with this SL.");
                 return 0; // Cannot meet precise risk and min volume constraint
            }
             if (normalizedVolume == 0 && tradeSymbol.VolumeInUnitsMin > 0) {
                 Print($"[{tradeSymbol.Name}] Normalized volume is 0, MinVolume {tradeSymbol.VolumeInUnitsMin}. Cannot trade. SL distance: {stopLossDistance:F5}");
                 return 0;
            }


            return normalizedVolume;
        }

        private bool IsTradeOpenForSymbol(string symbolName)
        {
            return Positions.Any(p => p.SymbolName == symbolName && p.Label != null && p.Label.StartsWith("15mOF_"));
        }

        protected override void OnStop()
        {
            Print("Bot stopping. Conceptual trades this session: " + _tradesThisMonth);
        }

        // TODO: Implement all placeholder methods for strategy logic using user's definitions from images/further comms.
        // Key methods needing detailed implementation:
        // - DetermineH1OrderFlow
        // - HasM15LiquidityGrab
        // - HasTwoPointsM15OF (and its sub-logic for liq grabs / FVG tests on M15)
        // - FindValidTargetAndRR (including Fractal detection)
        // - HasRecentFVGTest (for entry trigger)
        // - HasRecentLocalLiquidityGrab (for entry trigger)
        // Helper structures: FVGZone, SwingPoint

        private List<SwingPoint> GetSwingPoints(Bars bars, int lookbackPeriod, int strength = 2)
        {
            var swingPoints = new List<SwingPoint>();
            if (bars.Count < lookbackPeriod || bars.Count < (2 * strength + 1))
            {
                //Print($"[{bars.SymbolName}-{bars.TimeFrame}] Not enough data for GetSwingPoints. Have {bars.Count}, Need > {Math.Max(lookbackPeriod, 2 * strength + 1)}");
                return swingPoints;
            }

            // Iterate from (count - 1 - strength) down to strength, ensuring we have 'strength' bars on either side.
            // LookbackPeriod limits how far back we search from the *current* end of the series.
            int startIndex = Math.Max(strength, bars.Count - lookbackPeriod);
            int endIndex = bars.Count - 1 - strength;

            for (int i = endIndex; i >= startIndex; i--)
            {
                bool isSwingHigh = true;
                bool isSwingLow = true;

                for (int j = 1; j <= strength; j++)
                {
                    if (bars.HighPrices[i] <= bars.HighPrices[i - j] || bars.HighPrices[i] <= bars.HighPrices[i + j])
                        isSwingHigh = false;
                    if (bars.LowPrices[i] >= bars.LowPrices[i - j] || bars.LowPrices[i] >= bars.LowPrices[i + j])
                        isSwingLow = false;
                }

                if (isSwingHigh)
                {
                    swingPoints.Add(new SwingPoint { BarIndex = i, Price = bars.HighPrices[i], Time = bars.OpenTimes[i], IsHigh = true });
                }
                if (isSwingLow) // Can be both if strength=0 or a doji is a swing in a flat market with strength > 0
                {
                     // If it's already a swing high, and strength is low, avoid double-counting a single bar as both.
                     // For simplicity, we allow it but typically a bar is one or the other if strength > 0.
                    swingPoints.Add(new SwingPoint { BarIndex = i, Price = bars.LowPrices[i], Time = bars.OpenTimes[i], IsHigh = false });
                }
            }
            //Print($"[{bars.SymbolName}-{bars.TimeFrame}] Found {swingPoints.Count} swing points in last {lookbackPeriod} bars (strength {strength}).");
            return swingPoints.OrderBy(sp => sp.BarIndex).ToList(); // Return sorted by time ascending
        }

        private List<FVGZone> IdentifyFVGZones(Bars bars, int lookbackPeriod)
        {
            var fvgZones = new List<FVGZone>();
            if (bars.Count < lookbackPeriod || bars.Count < 3)
            {
                //Print($"[{bars.SymbolName}-{bars.TimeFrame}] Not enough data for IdentifyFVGZones. Have {bars.Count}, Need > {Math.Max(lookbackPeriod,3)}");
                return fvgZones;
            }

            // Iterate from (count - 1) down to 2, as FVG involves 3 candles (i, i-1, i-2)
            // LookbackPeriod limits how far back we search from the *current* end of the series.
            int startIndex = Math.Max(2, bars.Count - lookbackPeriod); 
            int endIndex = bars.Count - 1;

            for (int i = endIndex; i >= startIndex; i--) // i is the 3rd candle (current candle being checked for FVG confirmation)
            {
                // Candle indices: Bar 1: i-2, Bar 2: i-1 (the imbalanced one), Bar 3: i
                double highPrev = bars.HighPrices[i - 2];
                double lowPrev = bars.LowPrices[i - 2];
                double highNext = bars.HighPrices[i];
                double lowNext = bars.LowPrices[i];

                // Bullish FVG: Low of Candle 3 is above High of Candle 1
                // Gap is between High of Candle 1 (highPrev) and Low of Candle 3 (lowNext)
                // Candle 2 (bars.ClosePrices[i-1]) should be bullish
                if (lowNext > highPrev && bars.ClosePrices[i-1] > bars.OpenPrices[i-1])
                {
                    fvgZones.Add(new FVGZone
                    {
                        Top = lowNext, // Top of bullish FVG
                        Bottom = highPrev, // Bottom of bullish FVG
                        FormationBarIndex = i,
                        FormationTime = bars.OpenTimes[i],
                        IsBullish = true
                    });
                }
                // Bearish FVG: High of Candle 3 is below Low of Candle 1
                // Gap is between Low of Candle 1 (lowPrev) and High of Candle 3 (highNext)
                // Candle 2 (bars.ClosePrices[i-1]) should be bearish
                else if (highNext < lowPrev && bars.ClosePrices[i-1] < bars.OpenPrices[i-1])
                {
                    fvgZones.Add(new FVGZone
                    {
                        Top = lowPrev,    // Top of bearish FVG
                        Bottom = highNext, // Bottom of bearish FVG
                        FormationBarIndex = i,
                        FormationTime = bars.OpenTimes[i],
                        IsBullish = false
                    });
                }
            }
            //Print($"[{bars.SymbolName}-{bars.TimeFrame}] Found {fvgZones.Count} FVG zones in last {lookbackPeriod} bars.");
            return fvgZones.OrderBy(fvg => fvg.FormationBarIndex).ToList(); // Return sorted by time ascending
        }

        // Helper structure for Fractals (similar to SwingPoint but for Bill Williams Fractals)
        public struct FractalPoint
        {
            public int BarIndex;
            public double Price;
            public DateTime Time;
            public bool IsHigh; // True if High Fractal, False if Low Fractal

            public override string ToString()
            {
                return $"{(IsHigh ? "Fractal High" : "Fractal Low")} at {Time:MM-dd HH:mm}: {Price:F5} (Idx: {BarIndex})";
            }
        }
        
        private List<FractalPoint> GetFractals(Bars bars, int lookbackPeriod, int strength = 2)
        {
            var fractals = new List<FractalPoint>();
            // A fractal needs 'strength' bars on each side, plus the middle bar. Total 2*strength + 1 bars.
            if (bars.Count < lookbackPeriod || bars.Count < (2 * strength + 1))
            {
                //Print($"[{bars.SymbolName}-{bars.TimeFrame}] Not enough data for GetFractals. Have {bars.Count}, Need > {Math.Max(lookbackPeriod, 2 * strength + 1)}");
                return fractals;
            }

            // Iterate from (count - 1 - strength) down to strength.
            // This ensures that `i` is the middle bar of a potential fractal, and we can look `strength` bars to the left and right.
            int startIndex = Math.Max(strength, bars.Count - lookbackPeriod); // Start of the lookback window or earliest possible fractal start
            int endIndex = bars.Count - 1 - strength; // Last possible middle bar of a fractal

            for (int i = endIndex; i >= startIndex; i--)
            {
                bool isHighFractal = true;
                for (int j = 1; j <= strength; j++)
                {
                    if (bars.HighPrices[i] <= bars.HighPrices[i - j] || bars.HighPrices[i] <= bars.HighPrices[i + j])
                    {
                        isHighFractal = false;
                        break;
                    }
                }

                if (isHighFractal)
                {
                    fractals.Add(new FractalPoint { BarIndex = i, Price = bars.HighPrices[i], Time = bars.OpenTimes[i], IsHigh = true });
                    // Skip checking for low fractal on the same bar if high fractal found, typical for standard fractals.
                    continue; 
                }

                bool isLowFractal = true;
                for (int j = 1; j <= strength; j++)
                {
                    if (bars.LowPrices[i] >= bars.LowPrices[i - j] || bars.LowPrices[i] >= bars.LowPrices[i + j])
                    {
                        isLowFractal = false;
                        break;
                    }
                }

                if (isLowFractal)
                {
                    fractals.Add(new FractalPoint { BarIndex = i, Price = bars.LowPrices[i], Time = bars.OpenTimes[i], IsHigh = false });
                }
            }
            //Print($"[{bars.SymbolName}-{bars.TimeFrame}] Found {fractals.Count} fractals in last {lookbackPeriod} bars (strength {strength}).");
            return fractals.OrderBy(f => f.BarIndex).ToList(); // Return sorted by time ascending
        }
    }
} 