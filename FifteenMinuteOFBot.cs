using System;
using System.Linq;
using System.Collections.Generic; // Required for List<T>
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // Structure to hold FVG Information
    public class FVG
    {
        public double Top { get; set; }          // Top price of the FVG range
        public double Bottom { get; set; }       // Bottom price of the FVG range
        public bool IsBullish { get; set; }      // True if bullish FVG, false if bearish
        
        public Bar Bar1 { get; set; } // First bar of the FVG pattern
        public Bar Bar2 { get; set; } // Second bar (middle bar)
        public Bar Bar3 { get; set; } // Third bar of the FVG pattern

        public DateTime OpenTimeOfFirstBar { get; set; } // Open time of Bar1 for identification and uniqueness
        public int FirstBarIndex { get; set; } // Index of Bar1 in the Bars collection (e.g., from series.Count - 1 - i)
        
        public bool IsTested { get; set; } = false;
        public int TestBarIndex { get; set; } = -1; // Index of the bar that tested this FVG (e.g., Bars.Count - 1 - testBarIdx)
        public bool IsViolated { get; set; } = false; // Optional: To mark if FVG is no longer valid (e.g., price closed completely through it)
    }

    // Structure to hold Liquidity Sweep Information
    public class LiquiditySweepInfo
    {
        public DateTime Time { get; set; }           // Time of the bar that confirmed the sweep
        public double SweptLevel { get; set; }     // The High/Low level that was swept
        public bool IsBullishSweepAbove { get; set; } // True if a high was swept (potentially bearish signal), false if a low was swept (potentially bullish signal)
        public int ConfirmationBarIndex { get; set; } // Index of the bar that confirmed the sweep (closed back)
        public double WickHigh {get; set; } // Highest point of the wick that swept
        public double WickLow {get; set; } // Lowest point of the wick that swept
    }

    // Structure to hold Fractal Information
    public class FractalInfo
    {
        public DateTime Time { get; set; }       // Time of the middle bar of the fractal
        public double Price { get; set; }        // The High (for High Fractal) or Low (for Low Fractal)
        public bool IsHighFractal { get; set; }  // True if it's a high fractal (peak), false if low fractal (valley)
        public int SourceBarIndex { get; set; } // Index of the middle bar from the end of the series (e.g., Last(SourceBarIndex))
        public TimeFrame TF { get; set; }         // TimeFrame on which fractal was identified
    }

    // New class for Historical Trade Replay
    public class HistoricalTradeInfo
    {
        public DateTime EntryTimeUTC { get; set; }
        public bool IsLong { get; set; }
        public string Symbol { get; set; }
        public bool IsProcessed { get; set; } = false;

        public HistoricalTradeInfo(string dateTimeStr, bool isLong, string symbol)
        {
            if (DateTime.TryParse(dateTimeStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsedTime))
            {
                EntryTimeUTC = parsedTime;
            }
            else
            {
                // Handle parsing error or set a default, though this should be caught by user providing correct format
                throw new ArgumentException($"Invalid DateTime format for historical trade: {dateTimeStr}");
            }
            IsLong = isLong;
            Symbol = symbol;
        }
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class FifteenMinuteOFBot : Robot
    {
        [Parameter("---- H1 Context Parameters ----", Group = "Context")]
        public string SeparatorContext { get; set; }

        [Parameter("H1 Bars for Context", DefaultValue = 8, MinValue = 1, Group = "Context")]
        public int H1BarsForContext { get; set; }

        [Parameter("H1 Min. % Change", DefaultValue = 0.2, MinValue = 0.01, Step = 0.01, Group = "Context")]
        public double H1MinPercentageChange { get; set; }

        [Parameter("---- Liquidity Sweep (LS) Parameters ----", Group = "Strategy - LS")]
        public string SeparatorLS { get; set; }

        [Parameter("LS Lookback Bars", DefaultValue = 10, MinValue = 3, MaxValue = 50, Group = "Strategy - LS")]
        public int LSLookbackBars { get; set; }

        [Parameter("LS Detection Window Bars", DefaultValue = 3, MinValue = 1, MaxValue = 5, Group = "Strategy - LS")]
        public int LSDetectionWindowBars { get; set; } // How many recent bars to check for a sweep event

        [Parameter("---- Rule 1 LS Parameters ----", Group = "Strategy - Rule 1 LS")]
        public string SeparatorRule1LS { get; set; }

        [Parameter("Rule 1 LS Lookback Bars", DefaultValue = 20, MinValue = 3, MaxValue = 1000, Group = "Strategy - Rule 1 LS")]
        public int Rule1_LSLookbackBars { get; set; }

        [Parameter("Rule 1 LS Detection Window", DefaultValue = 5, MinValue = 1, MaxValue = 10, Group = "Strategy - Rule 1 LS")]
        public int Rule1_LSDetectionWindowBars { get; set; }

        [Parameter("---- Fractal Parameters ----", Group = "Strategy - Fractals")]
        public string SeparatorFr { get; set; }

        [Parameter("Fractal Lookback Bars", DefaultValue = 30, MinValue = 3, MaxValue = 100, Group = "Strategy - Fractals")]
        public int FractalLookbackBars { get; set; } // How many bars to scan for fractals on M15 and H1

        [Parameter("---- FVG Parameters ----", Group = "Strategy - FVG")]
        public string SeparatorFVG { get; set; }

        [Parameter("FVG Lookback Bars", DefaultValue = 20, MinValue = 3, Group = "Strategy - FVG")]
        public int FVGLookbackBars { get; set; }

        [Parameter("FVG Test Window Bars", DefaultValue = 3, MinValue = 1, MaxValue = 5, Group = "Strategy - FVG")]
        public int FVGTestWindowBars { get; set; } // How many recent bars to check for an FVG test

        [Parameter("FVG Impulse Lookback Bars", DefaultValue = 10, MinValue = 3, MaxValue = 30, Group = "Strategy - FVG")]
        public int FVGImpulseLookbackBars { get; set; }

        [Parameter("---- 15mOF Strategy Parameters ----", Group = "Strategy")]
        public string Separator2 { get; set; } // Dummy for spacing

        // Risk per Trade is now a fixed constant
        // [Parameter("Risk per Trade (%)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Step = 0.1, Group = "Strategy")]
        // public double RiskPercentPerTrade { get; set; }
        private const double RiskPercentPerTrade = 1.0;

        [Parameter("Min Risk/Reward Ratio", DefaultValue = 1.5, MinValue = 0.5, Step = 0.1, Group = "Strategy")]
        public double MinRR { get; set; }

        [Parameter("Max Risk/Reward Ratio", DefaultValue = 3.0, MinValue = 1.0, Step = 0.1, Group = "Strategy")]
        public double MaxRR { get; set; }
        
        [Parameter("Trade Label", DefaultValue = "15mOF", Group = "Strategy")]
        public string TradeLabel { get; set; }

        [Parameter("SL Offset (Ticks)", DefaultValue = 15, MinValue = 1, Group = "Strategy")]
        public int StopLossOffsetTicks { get; set; }

        [Parameter("Max Armed Duration (Bars)", DefaultValue = 10, MinValue = 1, MaxValue = 50, Group = "Strategy")]
        public int MaxArmedDurationBars { get; set; }

        [Parameter("Max Bars For Entry Signal Age", DefaultValue = 1, MinValue = 1, MaxValue = 5, Group = "Strategy")]
        public int MaxBarsForEntrySignalAge { get; set; }

        [Parameter("Min SL (API Pips)", DefaultValue = 1.0, MinValue = 0.1, Step = 0.1, Group = "Strategy")]
        public double MinSL_API_Pips { get; set; }

        // New Parameter for FVG lookback specifically for entry signals
        [Parameter("Entry FVG Lookback Bars", DefaultValue = 30, MinValue = 5, Group = "Strategy")]
        public int EntryFVGLookbackBars { get; set; }

        [Parameter("SL/TP Buffer Ticks (Execution)", DefaultValue = 5, MinValue = 0, Group = "Strategy")]
        public int SLTPBufferTicks { get; set; }        

        [Parameter("---- Historical Replay ----", Group = "Historical Replay")]
        public string SeparatorHist { get; set; }

        [Parameter("Enable Historical Replay Mode", DefaultValue = false, Group = "Historical Replay")]
        public bool EnableHistoricalReplayMode { get; set; }

        // Internal state variables
        private string _currentD1Context = "Initializing...";
        private DateTime _lastContextUpdateTime = DateTime.MinValue;

        private Bars _d1Bars;
        private Bars _h1Bars; 

        // Arming State Variables
        private bool _isArmedForLong = false;
        private bool _isArmedForShort = false;
        private ArmingInfo _armingDetails = null;
        private DateTime _setupArmedTime = DateTime.MinValue;

        // List for historical trades
        private List<HistoricalTradeInfo> _historicalTrades = new List<HistoricalTradeInfo>();

        protected override void OnStart()
        {
            Print("FifteenMinuteOFBot started.");
            Print($"Bot running on: {SymbolName} {TimeFrame}");
            Print($"---- Symbol Info ----");
            Print($"Digits: {Symbol.Digits}");
            Print($"PipSize (from API): {Symbol.PipSize}"); // Log API's PipSize
            Print($"PipValue: {Symbol.PipValue}");
            Print($"TickSize: {Symbol.TickSize}");
            Print($"TickValue: {Symbol.TickValue}");
            Print($"LotSize (Units): {Symbol.LotSize}");
            Print($"---------------------");
            Print($"H1 Context: Bars={H1BarsForContext}, Min%Change={H1MinPercentageChange}");
            Print($"Strategy: Risk={RiskPercentPerTrade}% (Fixed), SL Offset Ticks={StopLossOffsetTicks}, Label='{TradeLabel}'");
            Print($"LS Params: Lookback={LSLookbackBars}, DetectionWindow={LSDetectionWindowBars}");
            Print($"Rule 1 LS Params: Lookback={Rule1_LSLookbackBars}, DetectionWindow={Rule1_LSDetectionWindowBars}");
            Print($"FVG Params: Lookback={FVGLookbackBars}, TestWindow={FVGTestWindowBars}, ImpulseLookback={FVGImpulseLookbackBars}");
            Print($"Fractal Params: Lookback={FractalLookbackBars}");
            Print($"Strategy Params: MaxArmedDurationBars={MaxArmedDurationBars}, MaxBarsForEntrySignalAge={MaxBarsForEntrySignalAge}, MinSL_API_Pips={MinSL_API_Pips}");

            if (TimeFrame != TimeFrame.Minute15)
            {
                Print("WARNING: This bot is designed for the M15 timeframe. Please apply it to an M15 chart.");
            }
            
            if (EnableHistoricalReplayMode)
            {
                InitializeHistoricalTrades();
                Print($"Historical Replay Mode ENABLED. Loaded {_historicalTrades.Count(t => t.Symbol == SymbolName)} trades for {SymbolName}.");
            }

            _d1Bars = MarketData.GetBars(TimeFrame.Daily);
            _h1Bars = MarketData.GetBars(TimeFrame.Hour); 
            DetermineContext(); // Initial H1 context
            _lastContextUpdateTime = _h1Bars.OpenTimes.LastValue; // Store time of initial context update
        }

        protected override void OnBar()
        {
            // Determine H1 context first
            bool newH1BarFormed = _h1Bars.OpenTimes.LastValue > _lastContextUpdateTime;
            if (newH1BarFormed || _currentD1Context == "Initializing...")
            {
                 Print("New H1 bar formed or initial run, re-evaluating H1 context...");
                 DetermineContext();
                 _lastContextUpdateTime = _h1Bars.OpenTimes.LastValue;
                 
                 // Check if the context actually changed from a non-initializing state before resetting.
                 // This avoids resetting armed state if context was "Initializing..." and became something else,
                 // or if it was re-evaluated but didn't change.
                 // A more robust way to check for actual change is to store the context before DetermineContext()
                 // and compare afterwards. For now, this simple check should reduce unnecessary resets.
                 if (!EnableHistoricalReplayMode && _currentD1Context != "Initializing...") // And ideally, check if context *actually* changed
                 {
                    ResetArmedState("H1 Context Updated/Re-evaluated");
                 }
            }

            if (EnableHistoricalReplayMode)
            {
                ProcessHistoricalTradeTrigger();
                return; // Skip normal logic if in replay mode
            }

            // -------- Normal Trading Logic Starts Here --------
            if (!IsTradingSessionActive())
            {
                // Print("Trading session is not active. Skipping strategy logic.");
                return;
            }

            // If armed, check for entry trigger or timeout
            if (_isArmedForLong || _isArmedForShort)
            {
                if (Bars.OpenTimes.LastValue > _setupArmedTime.AddMinutes(MaxArmedDurationBars * 15)) // Assuming M15 timeframe
                {
                    ResetArmedState($"Armed state timed out after {MaxArmedDurationBars} bars.");
                }
                else
                {
                    // Placeholder for new entry logic
                    bool tradeExecuted = LookForEntryTriggerAndExecute(); 
                    if (tradeExecuted)
                    {
                        ResetArmedState("Trade executed.");
                        return; // Stop further processing on this bar
                    }
                    // If no trade executed, bot remains armed and waits for next bar or timeout
                    // Print($"Bot remains armed. {_armingDetails.ArmingPattern} for {_armingDetails.ArmingSignalTime}. Waiting for entry trigger.");
                    return; 
                }
            }

            // If not armed, and H1 context is clear, look for an arming condition
            if (!_isArmedForLong && !_isArmedForShort)
            {
                if (_currentD1Context == "Uptrend" || _currentD1Context == "Downtrend")
                {
                    List<FVG> m15fvgs = FindFVGs(Bars, FVGLookbackBars);
                    List<LiquiditySweepInfo> m15Rule1Sweeps = FindLiquiditySweeps(Bars, Rule1_LSLookbackBars, Rule1_LSDetectionWindowBars);
                    List<LiquiditySweepInfo> m15SecondarySweeps = FindLiquiditySweeps(Bars, LSLookbackBars, LSDetectionWindowBars);
                    // Fractals are needed for TryCalculateTradeParameters, which is called by LookForEntryTriggerAndExecute
                    // List<FractalInfo> m15Fractals = FindFractals(Bars, TimeFrame.Minute15, FractalLookbackBars);
                    // List<FractalInfo> h1Fractals = FindFractals(_h1Bars, TimeFrame.Hour, FractalLookbackBars); 
                    
                    foreach(var fvg in m15fvgs) { CheckAndMarkFVGTest(fvg, Bars, FVGTestWindowBars); }

                    ArmingInfo identifiedArmingCondition = IdentifyArmingCondition(m15Rule1Sweeps, m15SecondarySweeps, m15fvgs, _currentD1Context);

                    if (identifiedArmingCondition.IsMet)
                    {
                        _armingDetails = identifiedArmingCondition;
                        _setupArmedTime = Bars.OpenTimes.LastValue; // Time of the bar where arming condition was met
                        if (_armingDetails.IsBullish)
                        {
                            _isArmedForLong = true;
                            Print($"BOT ARMED FOR LONG. Pattern: {_armingDetails.ArmingPattern}, Signal Time: {_armingDetails.ArmingSignalTime}, H1: {_currentD1Context}");
                        }
                        else
                        {
                            _isArmedForShort = true;
                            Print($"BOT ARMED FOR SHORT. Pattern: {_armingDetails.ArmingPattern}, Signal Time: {_armingDetails.ArmingSignalTime}, H1: {_currentD1Context}");
                        }
                    }
                }
                else
                {
                    // Print($"H1 Context: {_currentD1Context}. Not looking for arming conditions.");
                }
            }
        }

        private void ResetArmedState(string reason)
        {
            if (_isArmedForLong || _isArmedForShort)
            {
                Print($"Armed state reset: {reason}");
            }
            _isArmedForLong = false;
            _isArmedForShort = false;
            _armingDetails = null;
            _setupArmedTime = DateTime.MinValue;
        }

        private bool LookForEntryTriggerAndExecute()
        {
            if (!_isArmedForLong && !_isArmedForShort) return false;

            var potentialTrades = new List<PotentialTrade>();
            TradeType currentTradeDirection = _isArmedForLong ? TradeType.Buy : TradeType.Sell;
            string armedDirectionStr = _isArmedForLong ? "LONG" : "SHORT";

            // Print($"DEBUG ({Bars.OpenTimes.LastValue}): Armed {armedDirectionStr}. Looking for entry triggers using EntryFVGLookbackBars: {EntryFVGLookbackBars} and MaxBarsForEntrySignalAge: {MaxBarsForEntrySignalAge}.");

            // 1. Evaluate FVG-based entries
            List<FVG> recentFVGs = FindFVGs(Bars, EntryFVGLookbackBars);
            // LogFVGs(recentFVGs, "Entry Candidate FVGs"); // For debugging

            foreach (var fvg in recentFVGs)
            {
                CheckAndMarkFVGTest(fvg, Bars, MaxBarsForEntrySignalAge); 

                if (fvg.IsTested && fvg.TestBarIndex >= 1 && fvg.TestBarIndex <= MaxBarsForEntrySignalAge)
                {
                    bool matchesArmingDirection = (_isArmedForLong && fvg.IsBullish) || (_isArmedForShort && !fvg.IsBullish);
                    if (!matchesArmingDirection) continue;

                    double potentialEntryPrice = _isArmedForLong ? fvg.Top : fvg.Bottom;
                    if (fvg.Top == fvg.Bottom) continue; 

                    DateTime signalTime = Bars.OpenTimes.Last(fvg.TestBarIndex);

                    var tempExecutionInfo = new TradeExecutionInfo(currentTradeDirection == TradeType.Buy)
                    {
                        EntryPrice = potentialEntryPrice,
                        EntryFVG = fvg,
                        EntryTriggerTime = signalTime
                    };

                    List<FractalInfo> m15Fractals = FindFractals(Bars, TimeFrame.Minute15, FractalLookbackBars);
                    List<FractalInfo> h1Fractals = FindFractals(_h1Bars, TimeFrame.Hour, FractalLookbackBars);

                    if (TryCalculateTradeParameters(tempExecutionInfo, m15Fractals, h1Fractals))
                    {
                        if (tempExecutionInfo.IsValid)
                        {
                            potentialTrades.Add(new PotentialTrade
                            {
                                EntryPrice = tempExecutionInfo.EntryPrice,
                                SLPrice = tempExecutionInfo.StopLossPrice,
                                TPPrice = tempExecutionInfo.TakeProfitTargetPrice,
                                RR = tempExecutionInfo.RiskRewardRatio,
                                CalculatedVolumeInUnits = tempExecutionInfo.CalculatedVolumeInUnits,
                                Reason = $"FVG Test ({ (fvg.IsBullish ? "Bullish" : "Bearish") } {fvg.Bottom.ToString("F"+Symbol.Digits)}-{fvg.Top.ToString("F"+Symbol.Digits)} @ {fvg.OpenTimeOfFirstBar:HH:mm:ss})",
                                FvgDetails = fvg,
                                LsDetails = null,
                                BarIndexOfSignal = fvg.TestBarIndex,
                                TradeDirection = currentTradeDirection,
                                SignalTime = signalTime
                            });
                            // Print($"Added POTENTIAL FVG trade ({armedDirectionStr}): Entry {tempExecutionInfo.EntryPrice:F5}, SL {tempExecutionInfo.StopLossPrice:F5}, TP {tempExecutionInfo.TakeProfitTargetPrice:F5}, RR {tempExecutionInfo.RiskRewardRatio:F2}, Vol {tempExecutionInfo.CalculatedVolumeInUnits}");
                        }
                    }
                }
            }

            // 2. Evaluate LS-based entries
            List<LiquiditySweepInfo> currentLSs = FindLiquiditySweeps(Bars, LSLookbackBars, LSDetectionWindowBars);
            foreach (var ls in currentLSs)
            {
                if (ls.ConfirmationBarIndex >= 1 && ls.ConfirmationBarIndex <= MaxBarsForEntrySignalAge)
                {
                    bool matchesArmingDirection = (_isArmedForLong && !ls.IsBullishSweepAbove) || (_isArmedForShort && ls.IsBullishSweepAbove);
                    if (!matchesArmingDirection) continue;

                    double potentialEntryPrice = ls.SweptLevel;
                    DateTime signalTime = ls.Time;

                    var tempExecutionInfo = new TradeExecutionInfo(currentTradeDirection == TradeType.Buy)
                    {
                        EntryPrice = potentialEntryPrice,
                        EntryLS = ls,
                        EntryTriggerTime = signalTime
                    };
                    
                    List<FractalInfo> m15Fractals = FindFractals(Bars, TimeFrame.Minute15, FractalLookbackBars);
                    List<FractalInfo> h1Fractals = FindFractals(_h1Bars, TimeFrame.Hour, FractalLookbackBars);

                    if (TryCalculateTradeParameters(tempExecutionInfo, m15Fractals, h1Fractals))
                    {
                         if (tempExecutionInfo.IsValid)
                         {
                            potentialTrades.Add(new PotentialTrade
                            {
                                EntryPrice = tempExecutionInfo.EntryPrice,
                                SLPrice = tempExecutionInfo.StopLossPrice,
                                TPPrice = tempExecutionInfo.TakeProfitTargetPrice,
                                RR = tempExecutionInfo.RiskRewardRatio,
                                CalculatedVolumeInUnits = tempExecutionInfo.CalculatedVolumeInUnits,
                                Reason = $"LS Entry ({(!ls.IsBullishSweepAbove ? "Bullish" : "Bearish")} sweep of {ls.SweptLevel} @ {ls.Time:HH:mm:ss})",
                                FvgDetails = null,
                                LsDetails = ls,
                                BarIndexOfSignal = ls.ConfirmationBarIndex,
                                TradeDirection = currentTradeDirection,
                                SignalTime = signalTime
                            });
                            // Print($"Added POTENTIAL LS trade ({armedDirectionStr}): Entry {tempExecutionInfo.EntryPrice:F5}, SL {tempExecutionInfo.StopLossPrice:F5}, TP {tempExecutionInfo.TakeProfitTargetPrice:F5}, RR {tempExecutionInfo.RiskRewardRatio:F2}, Vol {tempExecutionInfo.CalculatedVolumeInUnits}");
                         }
                    }
                }
            }
            
            PotentialTrade bestTrade = null;
            if (potentialTrades.Any())
            {
                if (potentialTrades.Count > 1) Print($"Found {potentialTrades.Count} potential trades for {armedDirectionStr} on bar {Bars.OpenTimes.LastValue:yyyy-MM-dd HH:mm:ss}. Evaluating best option...");
                
                if (currentTradeDirection == TradeType.Buy)
                {
                    bestTrade = potentialTrades.OrderBy(t => t.EntryPrice).ThenByDescending(t => t.RR).FirstOrDefault();
                }
                else 
                {
                    bestTrade = potentialTrades.OrderByDescending(t => t.EntryPrice).ThenByDescending(t => t.RR).FirstOrDefault();
                }
            }

            if (bestTrade != null)
            {
                Print($"Selected BEST trade ({armedDirectionStr}): {bestTrade.Reason}, Entry: {bestTrade.EntryPrice:F5}, SL: {bestTrade.SLPrice:F5}, TP: {bestTrade.TPPrice:F5}, RR: {bestTrade.RR:F2}, Vol: {bestTrade.CalculatedVolumeInUnits}");

                var finalExecutionInfo = new TradeExecutionInfo(bestTrade.TradeDirection == TradeType.Buy)
                {
                    EntryPrice = bestTrade.EntryPrice,
                    StopLossPrice = bestTrade.SLPrice,
                    TakeProfitTargetPrice = bestTrade.TPPrice,
                    RiskRewardRatio = bestTrade.RR,
                    CalculatedVolumeInUnits = bestTrade.CalculatedVolumeInUnits, 
                    EntryFVG = bestTrade.FvgDetails,
                    EntryLS = bestTrade.LsDetails,
                    EntryTriggerTime = bestTrade.SignalTime,
                    IsValid = true 
                };
                
                ProcessTradeExecution(finalExecutionInfo);
                return true; 
            }
            // else if (_isArmedForLong || _isArmedForShort) // Still armed but no trade taken
            // {
            //      Print($"DEBUG ({Bars.OpenTimes.LastValue}): Armed {armedDirectionStr}. No suitable entry trigger found this bar. Potential trades count: {potentialTrades.Count}");
            // }

            return false; 
        }

        // Renamed from DetermineD1Context to DetermineContext and logic changed to H1
        private void DetermineContext()
        {
            if (_h1Bars.ClosePrices.Count <= H1BarsForContext)
            {
                Print($"Not enough H1 historical data. Need more than {H1BarsForContext} bars, have {_h1Bars.ClosePrices.Count}. H1 Context: Insufficient Data");
                _currentD1Context = "Insufficient Data"; // Still using _currentD1Context variable name
                return;
            }

            double mostRecentH1Close = _h1Bars.ClosePrices.LastValue; 
            DateTime mostRecentH1OpenTime = _h1Bars.OpenTimes.LastValue;

            int pastH1BarIndex = H1BarsForContext; 
            
            if (pastH1BarIndex < 1 || _h1Bars.ClosePrices.Count <= pastH1BarIndex) 
            {
                 Print($"Invalid H1BarsForContext ({H1BarsForContext}) or not enough H1 data for past bar. Count: {_h1Bars.ClosePrices.Count}. H1 Context: Insufficient Data for Past Bar");
                _currentD1Context = "Insufficient Data for Past Bar";
                return;
            }
            double pastH1Close = _h1Bars.ClosePrices.Last(pastH1BarIndex);
            DateTime pastH1OpenTime = _h1Bars.OpenTimes.Last(pastH1BarIndex);

            if (pastH1Close == 0)
            {
                Print("Error: Past H1 closing price is zero. H1 Context: Error Zero Price");
                _currentD1Context = "Error: Zero Price";
                return;
            }

            double priceChange = mostRecentH1Close - pastH1Close;
            double percentageChange = (priceChange / pastH1Close) * 100;

            string determinedContext;
            if (percentageChange >= H1MinPercentageChange)
            {
                determinedContext = "Uptrend";
            }
            else if (percentageChange <= -H1MinPercentageChange)
            {
                determinedContext = "Downtrend";
            }
            else
            {
                determinedContext = "Ranging/Undefined";
            }
            
            string previousContext = _currentD1Context; // Store previous context to log only on change
            _currentD1Context = determinedContext; 

            if (previousContext != _currentD1Context || previousContext == "Initializing...") // Log if changed or initial
            {
                Print($"H1 Market Context Updated: {_currentD1Context}. Change: {percentageChange:F2}% over last {H1BarsForContext} H1 bar(s).");
                Print($"  H1 Calc: Recent Close {mostRecentH1OpenTime:yyyy-MM-dd HH:mm} ({mostRecentH1Close.ToString("F" + Symbol.Digits)}) vs Past Close {pastH1OpenTime:yyyy-MM-dd HH:mm} ({pastH1Close.ToString("F" + Symbol.Digits)})");
            }
        }
        
        // Placeholder for StopLoss calculation based on pips
        private double GetStopLossPrice(TradeType tradeType, double entryPrice, double referencePriceForSL)
        {
            // Stop loss должен ставится за свечу которая дала Full Fill FVG или сняла ликвидность локальную
            // This will be more complex. For now, a placeholder:
            // double slDistance = StopLossPipsParam * Symbol.PipSize; // Assuming a StopLossPipsParam exists
            // return tradeType == TradeType.Buy ? referencePriceForSL - slDistance : referencePriceForSL + slDistance;
            return 0; // To be implemented
        }

        // Placeholder for TakeProfit calculation
        private double GetTakeProfitPrice(TradeType tradeType, double entryPrice, double stopLossPrice)
        {
            // Наши цели - 1H фракталы, 15м фракталы, Min RR - 1.5 Max RR - 3
            return 0; // To be implemented
        }
        
        // Placeholder for Position Size Calculation
        private double CalculatePositionVolume(double stopLossPrice, double entryPrice)
        {
            // Define our strategic pip for risk calculation (e.g., 10 ticks)
            // This MUST match the definition in TryFinalizeSetupWithRule3 if used there for validation
            double strategicPipSize = 10 * Symbol.TickSize; 
            if (strategicPipSize == 0) strategicPipSize = Symbol.TickSize; // Fallback

            double stopDistanceInPrice = Math.Abs(entryPrice - stopLossPrice);
            if (stopDistanceInPrice == 0 || strategicPipSize == 0) 
            {
                Print("Error in CalculatePositionVolume: stop distance or strategicPipSize is zero.");
                return 0;
            }

            double stopLossInStrategicPips = stopDistanceInPrice / strategicPipSize;
            
            double riskAmount = Account.Equity * (RiskPercentPerTrade / 100.0);
            
            // PipValue here should ideally be for our strategic pip.
            // Symbol.PipValue is for API's PipSize. If API PipSize = 1 and our strategicPipSize = 0.1 (10 ticks),
            // then value of our strategic pip is Symbol.PipValue / (Symbol.PipSize / strategicPipSize)
            // = Symbol.PipValue * strategicPipSize / Symbol.PipSize
            double valuePerStrategicPipPerUnit = Symbol.TickValue * 10; // Since our strategic pip is 10 ticks
            if (Symbol.PipSize != 0 && strategicPipSize != Symbol.PipSize) // Adjust if API's PipValue is based on a different PipSize
            {
                // This is a more robust calculation for value of our strategic pip if API's PipValue is reliable
                // valuePerStrategicPipPerUnit = Symbol.PipValue * (strategicPipSize / Symbol.PipSize);
                // However, since PipValue itself seems tied to the faulty PipSize=1, let's stick to TickValue based calc.
            }
            if (valuePerStrategicPipPerUnit == 0) 
            {
                Print("Error in CalculatePositionVolume: valuePerStrategicPipPerUnit is zero.");
                return 0;
            }
            
            double volumeInUnits = riskAmount / (stopLossInStrategicPips * valuePerStrategicPipPerUnit);
            if (double.IsInfinity(volumeInUnits) || double.IsNaN(volumeInUnits) || volumeInUnits <=0) 
            {
                 Print($"Error in CalculatePositionVolume: volumeInUnits is invalid ({volumeInUnits}). RiskAmount: {riskAmount}, SLStrategicPips: {stopLossInStrategicPips}, ValPerStratPipUnit: {valuePerStrategicPipPerUnit}");
                return 0;
            }

            // Normalize this volume in units to lots, then to actual tradeable units
            double targetLots = volumeInUnits / Symbol.LotSize;
            double normalizedLots = Symbol.NormalizeVolumeInUnits(targetLots, RoundingMode.Down); // Using Symbol.LotSize for units per lot

            if (normalizedLots == 0) 
            {
                Print($"Calculated normalized lots is 0. Target lots: {targetLots:F8}. Check Risk % or SL calculation.");
                return 0;
            }
            return normalizedLots * Symbol.LotSize; // Return volume in units for ExecuteMarketOrder
        }

        // Function to find Fair Value Gaps (FVG)
        private List<FVG> FindFVGs(Bars series, int lookbackBars)
        {
            var fvgs = new List<FVG>();
            
            for (int i = lookbackBars + 2; i >= 3; i--) 
            {
                if (i > series.Count -1) continue; // Ensure i, i-1, i-2 are valid indices for Last()
                if ((i-2) < 0) continue; // Should be covered by i >= 3, but defensive check

                // Get actual Bar objects. series.Last(k) gives price/time, series[index] gives Bar object.
                // Index for series.Last(k) is k (0-based from current bar).
                // Actual 0-based index from start for series.Last(k) is series.Count - 1 - k.
                Bar bar1_obj = series[series.Count - 1 - i];
                Bar bar2_obj = series[series.Count - 1 - (i-1)];
                Bar bar3_obj = series[series.Count - 1 - (i-2)];
                
                int actualBar1Index = series.Count - 1 - i; 

                // Bullish FVG: Low of Bar1 is above High of Bar3
                if (bar1_obj.Low > bar3_obj.High)
                {
                    fvgs.Add(new FVG 
                    {
                        Bar1 = bar1_obj,
                        Bar2 = bar2_obj,
                        Bar3 = bar3_obj,
                        OpenTimeOfFirstBar = bar1_obj.OpenTime,
                        FirstBarIndex = actualBar1Index, 
                        Top = bar1_obj.Low,         
                        Bottom = bar3_obj.High,     
                        IsBullish = true,
                        IsTested = false,
                        TestBarIndex = -1
                    });
                }
                // Bearish FVG: High of Bar1 is below Low of Bar3
                else if (bar1_obj.High < bar3_obj.Low)
                {
                    fvgs.Add(new FVG
                    {
                        Bar1 = bar1_obj,
                        Bar2 = bar2_obj,
                        Bar3 = bar3_obj,
                        OpenTimeOfFirstBar = bar1_obj.OpenTime,
                        FirstBarIndex = actualBar1Index, 
                        Top = bar3_obj.Low, // Corrected: For Bearish FVG, Top is Bar3.Low
                        Bottom = bar1_obj.High, // Corrected: For Bearish FVG, Bottom is Bar1.High
                        IsBullish = false,
                        IsTested = false,
                        TestBarIndex = -1
                    });
                }
            }
            return fvgs.OrderBy(fvg => fvg.OpenTimeOfFirstBar).ToList();
        }

        private void LogFVGs(List<FVG> fvgs, string seriesName)
        {
            if(fvgs.Any())
            {
                Print($"--- {seriesName} FVGs Found ({fvgs.Count}) ---");
                foreach(var fvg in fvgs.Take(5)) // Log first 5
                {
                    Print($"  {(fvg.IsBullish ? "Bullish" : "Bearish")} FVG: {fvg.Bar1.Low} - {fvg.Bar3.High} at {fvg.OpenTimeOfFirstBar}");
                }
            }
        }

        // Function to find Liquidity Sweeps (LS)
        private List<LiquiditySweepInfo> FindLiquiditySweeps(Bars series, int lookbackBars, int detectionWindowBars) 
        {
            var sweeps = new List<LiquiditySweepInfo>();
            if (series.Count < lookbackBars + detectionWindowBars + 1) 
            {
                return sweeps; 
            }

            for (int i = 1; i <= detectionWindowBars; i++)
            {
                var detectionBar = series.Last(i); 
                DateTime detectionBarTime = detectionBar.OpenTime;
                double detectionBarHigh = detectionBar.High;
                double detectionBarLow = detectionBar.Low;
                double detectionBarClose = detectionBar.Close;

                int lookbackStartIdx = i + 1; 
                int lookbackEndIdx = i + lookbackBars;

                if (series.Count <= lookbackEndIdx) continue;

                double recentHigh = double.MinValue;
                double recentLow = double.MaxValue;
                int highBarIndex = -1;
                int lowBarIndex = -1;

                for (int k = lookbackStartIdx; k <= lookbackEndIdx; k++)
                {
                    if (series.HighPrices.Last(k) > recentHigh)
                    {
                        recentHigh = series.HighPrices.Last(k);
                        highBarIndex = k; 
                    }
                    if (series.LowPrices.Last(k) < recentLow)
                    {
                        recentLow = series.LowPrices.Last(k);
                        lowBarIndex = k;
                    }
                }
                
                if (highBarIndex != -1 && detectionBarHigh > recentHigh && detectionBarClose < recentHigh)
                {
                    sweeps.Add(new LiquiditySweepInfo
                    {
                        Time = detectionBarTime,
                        SweptLevel = recentHigh,
                        IsBullishSweepAbove = true, 
                        ConfirmationBarIndex = i,
                        WickHigh = detectionBarHigh,
                        WickLow = detectionBarLow
                    });
                    Print($"LS Sweep detected! Type: High Sweep (Bearish), BarTime: {detectionBarTime:yyyy-MM-dd HH:mm}, SweptLevel: {recentHigh}, WickHigh: {detectionBarHigh}, WickLow: {detectionBarLow}, Lookback: {lookbackBars}, DetectionWindow: {detectionWindowBars}, BarIndex: {i}");
                }

                if (lowBarIndex != -1 && detectionBarLow < recentLow && detectionBarClose > recentLow)
                {
                    sweeps.Add(new LiquiditySweepInfo
                    {
                        Time = detectionBarTime,
                        SweptLevel = recentLow,
                        IsBullishSweepAbove = false, 
                        ConfirmationBarIndex = i,
                        WickHigh = detectionBarHigh,
                        WickLow = detectionBarLow
                    });
                    Print($"LS Sweep detected! Type: Low Sweep (Bullish), BarTime: {detectionBarTime:yyyy-MM-dd HH:mm}, SweptLevel: {recentLow}, WickHigh: {detectionBarHigh}, WickLow: {detectionBarLow}, Lookback: {lookbackBars}, DetectionWindow: {detectionWindowBars}, BarIndex: {i}");
                }
            }
            return sweeps.OrderBy(s => s.ConfirmationBarIndex).ToList(); 
        }

        // Function to find 3-bar Fractals
        private List<FractalInfo> FindFractals(Bars series, TimeFrame tf, int lookbackBars)
        {
            var fractals = new List<FractalInfo>();
            if (series.Count < Math.Max(lookbackBars, 3)) { return fractals; } // Use .Count for Bars
            for (int bar_idx = 2; bar_idx <= Math.Min(lookbackBars, series.Count - 1 ); bar_idx++)
            {
                double prevHigh = series.HighPrices.Last(bar_idx + 1); double prevLow = series.LowPrices.Last(bar_idx + 1);
                double middleHigh = series.HighPrices.Last(bar_idx); double middleLow = series.LowPrices.Last(bar_idx);
                DateTime middleTime = series.OpenTimes.Last(bar_idx);
                double nextHigh = series.HighPrices.Last(bar_idx - 1); double nextLow = series.LowPrices.Last(bar_idx - 1);
                if (middleHigh > prevHigh && middleHigh > nextHigh)
                { fractals.Add(new FractalInfo { Time = middleTime, Price = middleHigh, IsHighFractal = true, SourceBarIndex = bar_idx, TF = tf }); }
                else if (middleLow < prevLow && middleLow < nextLow)
                { fractals.Add(new FractalInfo { Time = middleTime, Price = middleLow, IsHighFractal = false, SourceBarIndex = bar_idx, TF = tf }); }
            }
            return fractals.OrderByDescending(f => f.SourceBarIndex).ToList(); 
        }
        
        // Helper to log fractals (for debugging)
        private void LogFractals(List<FractalInfo> fractals, string tfLabel)
        {
            if(fractals.Any())
            {
                Print($"--- {tfLabel} Fractals Found ({fractals.Count}) ---");
                foreach(var fr in fractals.Take(5)) // Log first 5
                {
                    Print($"  {(fr.IsHighFractal ? "High" : "Low")} Fractal at {fr.Time} ({fr.Price.ToString("F"+Symbol.Digits)}) on {fr.TF}, Index: {fr.SourceBarIndex}");
                }
            }
        }

        // New function to check FVG test
        private void CheckAndMarkFVGTest(FVG fvg, Bars series, int testWindowBars)
        {
            if (fvg.IsTested) return; // Already marked as tested

            // FVG is defined by Bar1, Bar2, Bar3. Bar3 is the newest bar of the FVG pattern.
            // A test occurs if a bar *newer* than Bar3 interacts with the FVG levels.
            for (int i = 1; i <= Math.Min(testWindowBars, series.Count -1 ); i++)
            {
                Bar testBar = series[series.Count - 1 - i]; // series.Last(i) equivalent object

                // Ensure the testBar is newer than the FVG's Bar3
                if (testBar.OpenTime <= fvg.Bar3.OpenTime) continue;

                double barHigh = testBar.High;
                double barLow = testBar.Low;

                if (fvg.IsBullish)
                {
                    // Bullish FVG: Top = fvg.Bar1.Low, Bottom = fvg.Bar3.High
                    // Test is if a bar's Low dips into or below the FVG's Top (fvg.Bar1.Low)
                    // and the bar's High is at or above the FVG's Bottom (fvg.Bar3.High)
                    if (barLow <= fvg.Top && barHigh >= fvg.Bottom) 
                    {
                        fvg.IsTested = true;
                        fvg.TestBarIndex = i; // Store the index from end (1 = last closed bar) of the testing bar
                        // Print($"Bullish FVG at {fvg.OpenTimeOfFirstBar:HH:mm:ss} ({fvg.Bottom}-{fvg.Top}) TESTED by bar {testBar.OpenTime:HH:mm:ss} (idx {i})");
                        return; 
                    }
                }
                else // Bearish FVG
                {
                    // Bearish FVG: Top = fvg.Bar3.Low, Bottom = fvg.Bar1.High
                    // Test is if a bar's High rises into or above the FVG's Bottom (fvg.Bar1.High)
                    // and the bar's Low is at or below the FVG's Top (fvg.Bar3.Low)
                    if (barHigh >= fvg.Bottom && barLow <= fvg.Top) 
                    {
                        fvg.IsTested = true;
                        fvg.TestBarIndex = i; 
                        // Print($"Bearish FVG at {fvg.OpenTimeOfFirstBar:HH:mm:ss} ({fvg.Bottom}-{fvg.Top}) TESTED by bar {testBar.OpenTime:HH:mm:ss} (idx {i})");
                        return; 
                    }
                }
            }
        }

        protected override void OnStop()
        {
            Print("FifteenMinuteOFBot stopped.");
        }

        private bool IsTradingSessionActive()
        {
            // ServerTime is already UTC
            int currentHourUTC = Server.Time.Hour;

            // Frankfurt: 06:00-07:00 UTC (exclusive of end hour)
            if (currentHourUTC >= 6 && currentHourUTC < 7)
            {
                return true;
            }
            // London: 07:00-12:00 UTC (exclusive of end hour)
            if (currentHourUTC >= 7 && currentHourUTC < 12)
            {
                return true;
            }
            // New York: 12:00-20:00 UTC (exclusive of end hour)
            if (currentHourUTC >= 12 && currentHourUTC < 20)
            {
                return true;
            }
            return false;
        }

        // Renamed from SetupInfo
        public class ArmingInfo
        {
            public bool IsMet { get; set; } = false;
            public string ArmingPattern { get; set; } 
            public DateTime ArmingSignalTime { get; set; } 
            public bool IsBullish { get; set; }    
            
            public LiquiditySweepInfo PrimaryLS { get; set; } 
            public LiquiditySweepInfo SecondaryLS { get; set; } 
            public FVG PrimaryFVGTest { get; set; } 
            public FVG SecondaryFVGTest { get; set; } 

            public ArmingInfo(bool isBullish)
            {
                IsBullish = isBullish;
            }

            // Default constructor for cases where IsBullish might be set later or not applicable initially
            public ArmingInfo() {}
        }

        // Renamed from CheckForSetups. Now returns ArmingInfo and doesn't do SL/TP.
        private ArmingInfo IdentifyArmingCondition(List<LiquiditySweepInfo> m15Rule1Sweeps, List<LiquiditySweepInfo> m15SecondarySweeps, List<FVG> m15FVGs, string d1Context)
        {
            // Default to not met, bullish (will be overridden)
            var potentialArmingCondition = new ArmingInfo(true) { IsMet = false }; 

            if (d1Context == "Ranging/Undefined" || d1Context == "Insufficient Data" || d1Context == "Initializing..." || d1Context.StartsWith("Error"))
            {
                return potentialArmingCondition; // No arming if D1 context is not clear
            }

            bool lookingForBullish = d1Context == "Uptrend";
            potentialArmingCondition.IsBullish = lookingForBullish;

            // Rule 2.1: LS + LS
            foreach (var ls2 in m15SecondarySweeps) 
            {
                if ((lookingForBullish && !ls2.IsBullishSweepAbove) || (!lookingForBullish && ls2.IsBullishSweepAbove))
                {
                    foreach (var ls1 in m15Rule1Sweeps) 
                    {
                        if (ls1.ConfirmationBarIndex == ls2.ConfirmationBarIndex && ls1.Time == ls2.Time) continue;
                        if (!((lookingForBullish && !ls1.IsBullishSweepAbove) || (!lookingForBullish && ls1.IsBullishSweepAbove))) continue;
                        
                        if (ls1.ConfirmationBarIndex >= ls2.ConfirmationBarIndex && ls1.Time < ls2.Time) 
                        {
                            potentialArmingCondition.ArmingPattern = "LS+LS";
                            potentialArmingCondition.ArmingSignalTime = ls2.Time; 
                            potentialArmingCondition.PrimaryLS = ls1; 
                            potentialArmingCondition.SecondaryLS = ls2;
                            potentialArmingCondition.IsMet = true;
                            return potentialArmingCondition; // Found an arming condition
                        }
                    }
                }
            }

            // Rule 2.2: LS + FVG Test
            foreach (var fvg_tested in m15FVGs)
            {
                if (!fvg_tested.IsTested || fvg_tested.TestBarIndex < 1) continue; 

                if ((lookingForBullish && fvg_tested.IsBullish) || (!lookingForBullish && !fvg_tested.IsBullish))
                {
                    foreach (var ls1 in m15Rule1Sweeps) 
                    {
                        if (!((lookingForBullish && !ls1.IsBullishSweepAbove) || (!lookingForBullish && ls1.IsBullishSweepAbove))) continue;

                        DateTime fvgTestBarTime = Bars.OpenTimes.Last(fvg_tested.TestBarIndex);
                        if (ls1.ConfirmationBarIndex >= fvg_tested.TestBarIndex && ls1.Time < fvgTestBarTime) 
                        {
                            potentialArmingCondition.ArmingPattern = "LS+FVGTest";
                            potentialArmingCondition.ArmingSignalTime = fvgTestBarTime; 
                            potentialArmingCondition.PrimaryLS = ls1; 
                            potentialArmingCondition.PrimaryFVGTest = fvg_tested;
                            potentialArmingCondition.IsMet = true;
                            return potentialArmingCondition; // Found an arming condition
                        }
                    }
                }
            }
            
            // Rule 2.3: FVG Test + FVG Test (and a preceding LS for Rule 1)
            foreach (var fvg2_tested in m15FVGs)
            {
                if (!fvg2_tested.IsTested || fvg2_tested.TestBarIndex < 1) continue;

                if ((lookingForBullish && fvg2_tested.IsBullish) || (!lookingForBullish && !fvg2_tested.IsBullish))
                {
                    DateTime fvg2TestBarTime = Bars.OpenTimes.Last(fvg2_tested.TestBarIndex);
                    foreach (var fvg1_tested in m15FVGs)
                    {
                        if (fvg1_tested == fvg2_tested || !fvg1_tested.IsTested || fvg1_tested.TestBarIndex < 1) continue;
                        if (!((lookingForBullish && fvg1_tested.IsBullish) || (!lookingForBullish && !fvg1_tested.IsBullish))) continue;

                        DateTime fvg1TestBarTime = Bars.OpenTimes.Last(fvg1_tested.TestBarIndex);
                        if (fvg1_tested.TestBarIndex >= fvg2_tested.TestBarIndex && fvg1TestBarTime < fvg2TestBarTime) 
                        {
                            LiquiditySweepInfo precedingRule1LS = null;
                            foreach (var ls_check in m15Rule1Sweeps) 
                            {
                                if (!((lookingForBullish && !ls_check.IsBullishSweepAbove) || (!lookingForBullish && ls_check.IsBullishSweepAbove))) continue;
                                if (ls_check.ConfirmationBarIndex >= fvg1_tested.TestBarIndex && ls_check.Time <= fvg1TestBarTime)
                                {
                                    precedingRule1LS = ls_check;
                                    break; 
                                }
                            }

                            if (precedingRule1LS != null)
                            {
                                potentialArmingCondition.ArmingPattern = "FVGTest+FVGTest";
                                potentialArmingCondition.ArmingSignalTime = fvg2TestBarTime;
                                potentialArmingCondition.PrimaryLS = precedingRule1LS; 
                                potentialArmingCondition.PrimaryFVGTest = fvg1_tested;
                                potentialArmingCondition.SecondaryFVGTest = fvg2_tested;
                                potentialArmingCondition.IsMet = true;
                                return potentialArmingCondition; // Found an arming condition
                            }
                        }
                    }
                }
            }
            
            return potentialArmingCondition; // No arming condition met
        }

        // This class will hold details for actual trade execution
        public class TradeExecutionInfo
        {
            public bool IsValid { get; set; } = false;
            // public string Rule2Pattern { get; set; } // May not be needed if ArmingInfo.ArmingPattern is used
            public DateTime EntryTriggerTime { get; set; } 
            public bool IsBullish { get; set; }    
            
            // Entry, SL, TP details
            public double EntryPrice { get; set; }
            public double StopLossPrice { get; set; }
            public double TakeProfitTargetPrice { get; set; }
            public FractalInfo TakeProfitFractal { get; set; } 
            public double RiskRewardRatio { get; set; }
            public double CalculatedVolumeInUnits { get; set; }

            // Store the entry trigger elements
            public FVG EntryFVG { get; set; } // if entry was FVG based
            public LiquiditySweepInfo EntryLS { get; set; } // if entry was LS based

            public TradeExecutionInfo(bool isBullish)
            {
                IsBullish = isBullish;
            }
        }

        // Helper class for evaluating potential trades before execution
        private class PotentialTrade
        {
            public double EntryPrice { get; set; }
            public double SLPrice { get; set; }
            public double TPPrice { get; set; }
            public double RR { get; set; }
            public double SLBasePrice { get; set; } // For logging/info, actual SL is SLPrice
            public double CalculatedVolumeInUnits { get; set; }
            public string Reason { get; set; } // e.g., "FVG Test", "LS Entry"
            public FVG FvgDetails { get; set; } // Null if not FVG based
            public LiquiditySweepInfo LsDetails { get; set; } // Null if not LS based
            public int BarIndexOfSignal { get; set; } // Bar index where this signal (FVG test, LS) occurred (from end of series)
            public TradeType TradeDirection { get; set; }
            public DateTime SignalTime { get; set; } // Time of the signal (e.g. FVG test time, LS confirmation time)
        }

        // Renamed from TryFinalizeSetupWithRule3 - role changed
        // Now this function is primarily responsible for calculating SL (new logic), TP (fractal), RR and Volume
        // based on a CONFIRMED entry trigger (new FVG test or new LS).
        // The 'setup' argument should now be of type TradeExecutionInfo, 
        // and it will also need access to _armingDetails for context if necessary for SL.
        private bool TryCalculateTradeParameters(TradeExecutionInfo tradeInfo, List<FractalInfo> m15Fractals, List<FractalInfo> h1Fractals)
        {
            double entryPrice = tradeInfo.EntryPrice;
            double stopLossPrice = 0;
            double slBasePrice = 0;
            string slReason = "";

            if (tradeInfo.EntryFVG != null) 
            {
                FVG entryFVG = tradeInfo.EntryFVG;
                // entryFVG.FirstBarIndex is the 0-based index from the start of the series for Bar1 of the FVG.
                // We need the 1-based index from the end for series.Last() for Bar1 of the FVG.
                // series.Last(k) access element at actual index series.Count - 1 - k.
                // So, k = series.Count - 1 - actual_index.
                int fvgBar1Index_fromEnd = Bars.Count - 1 - entryFVG.FirstBarIndex;

                int impulseLookbackPeriod = FVGImpulseLookbackBars;
                // Start lookback for impulse one bar *before* Bar1 of FVG.
                // So, the first bar in the lookback range is series.Last(fvgBar1Index_fromEnd + 1).
                int lookbackRangeStartBar_fromEnd = fvgBar1Index_fromEnd + 1; 

                // Check if impulse lookback range is valid
                if (lookbackRangeStartBar_fromEnd + impulseLookbackPeriod -1 >= Bars.Count)
                {
                    Print($"Warning: Not enough bars for full FVGImpulseLookback before FVG. Adjusting lookback. FVG @ {entryFVG.OpenTimeOfFirstBar:HH:mm:ss}");
                    impulseLookbackPeriod = Bars.Count - lookbackRangeStartBar_fromEnd;
                    if (impulseLookbackPeriod < 1) impulseLookbackPeriod = 1; 
                }

                if (impulseLookbackPeriod < 1 || lookbackRangeStartBar_fromEnd >= Bars.Count || lookbackRangeStartBar_fromEnd < 0) 
                {
                     Print($"Warning: Not enough bars for FVGImpulseLookback (RangeStartIdxFromEnd {lookbackRangeStartBar_fromEnd}, period {impulseLookbackPeriod}). Using Bar #1 of FVG for SL base. FVG @ {entryFVG.OpenTimeOfFirstBar:HH:mm:ss}");
                     // Use Low/High of Bar1 of the FVG itself
                     slBasePrice = tradeInfo.IsBullish ? entryFVG.Bar1.Low : entryFVG.Bar1.High;
                     slReason = $"Extremum of Bar #1 of FVG (insufficient impulse lookback) (FVG @{entryFVG.OpenTimeOfFirstBar:HH:mm:ss})";
                }
                else
                {
                    if (tradeInfo.IsBullish) 
                    {
                        double minLow = double.MaxValue;
                        for (int k = 0; k < impulseLookbackPeriod; k++)
                        {
                            // series.Last(lookbackRangeStartBar_fromEnd + k)
                            minLow = Math.Min(minLow, Bars.LowPrices.Last(lookbackRangeStartBar_fromEnd + k));
                        }
                        slBasePrice = minLow;
                        slReason = $"Lowest low in {impulseLookbackPeriod} bars before Bullish FVG (FVG @{entryFVG.OpenTimeOfFirstBar:HH:mm:ss})";
                    }
                    else 
                    {
                        double maxHigh = double.MinValue;
                        for (int k = 0; k < impulseLookbackPeriod; k++)
                        {
                            maxHigh = Math.Max(maxHigh, Bars.HighPrices.Last(lookbackRangeStartBar_fromEnd + k));
                        }
                        slBasePrice = maxHigh;
                        slReason = $"Highest high in {impulseLookbackPeriod} bars before Bearish FVG (FVG @{entryFVG.OpenTimeOfFirstBar:HH:mm:ss})";
                    }
                }
            }
            else if (tradeInfo.EntryLS != null) // LS-based entry trigger
            {
                LiquiditySweepInfo entryLS = tradeInfo.EntryLS;
                slBasePrice = tradeInfo.IsBullish ? entryLS.WickLow : entryLS.WickHigh;
                slReason = $"{(tradeInfo.IsBullish ? "EntryLS.WickLow" : "EntryLS.WickHigh")} @{entryLS.Time}";
            }
            else
            {
                Print($"Error: TradeExecutionInfo has no EntryFVG or EntryLS for SL calculation.");
                return false; 
            }

            if (entryPrice == 0) 
            {
                Print("Error: Entry price is zero in TryCalculateTradeParameters.");
                return false;
            }
            
            if (slBasePrice <= 0 || (slBasePrice < entryPrice * 0.5 && entryPrice > 0) || (slBasePrice > entryPrice * 1.5 && entryPrice > 0) )
            {
                Print($"Trade invalidated: SL base price ({slBasePrice.ToString("F" + Symbol.Digits)} from {slReason}) is zero, negative or absurd relative to entry price ({entryPrice.ToString("F" + Symbol.Digits)}).");
                return false;
            }

            stopLossPrice = tradeInfo.IsBullish 
                ? (slBasePrice - StopLossOffsetTicks * Symbol.TickSize) 
                : (slBasePrice + StopLossOffsetTicks * Symbol.TickSize);
            
            if ((tradeInfo.IsBullish && stopLossPrice >= entryPrice) || (!tradeInfo.IsBullish && stopLossPrice <= entryPrice))
            {
                Print($"Trade invalidated: Stop Loss Price ({stopLossPrice.ToString("F" + Symbol.Digits)}) is on the wrong side or at entry price ({entryPrice.ToString("F" + Symbol.Digits)}). SL Base: {slBasePrice.ToString("F" + Symbol.Digits)} ({slReason}), Offset: {StopLossOffsetTicks} ticks.");
                return false;
            }

            double strategicPipSize = 10 * Symbol.TickSize; 
            if (strategicPipSize == 0) strategicPipSize = Symbol.TickSize; 

            if (strategicPipSize == 0) {
                Print("Error: strategicPipSize is zero, cannot validate SL or calculate volume.");
                return false;
            }
            
            double riskDistanceInPrice = Math.Abs(entryPrice - stopLossPrice);
            if (riskDistanceInPrice < Symbol.TickSize) 
            {
                Print($"Trade invalidated: Risk distance ({riskDistanceInPrice}) is less than TickSize ({Symbol.TickSize}).");
                return false;
            }

            // Min SL check
            double minStopLossPipsParamValue = MinSL_API_Pips; // Use new parameter
            double stopLossInStrategicPips = riskDistanceInPrice / strategicPipSize;
            double minStopLossInStrategicPipsThreshold = (minStopLossPipsParamValue * (Symbol.PipSize > 0 ? Symbol.PipSize : strategicPipSize) / strategicPipSize);
            if (stopLossInStrategicPips < minStopLossInStrategicPipsThreshold) 
            {
                 Print($"Trade invalidated: SL (in strategic pips) {stopLossInStrategicPips:F2} is less than min threshold {minStopLossInStrategicPipsThreshold:F2}. Entry: {entryPrice}, SL: {stopLossPrice}");
                return false;
            }

            tradeInfo.StopLossPrice = stopLossPrice;

            // --- Take Profit Logic using Fractals and R/R Range (largely reused) ---
            List<FractalInfo> potentialTPs = new List<FractalInfo>();
            if (tradeInfo.IsBullish)
            {
                potentialTPs.AddRange(h1Fractals.Where(f => f.IsHighFractal && f.Price > entryPrice));
                potentialTPs.AddRange(m15Fractals.Where(f => f.IsHighFractal && f.Price > entryPrice));
                potentialTPs = potentialTPs.OrderBy(f => f.Price).ToList(); 
            }
            else 
            {
                potentialTPs.AddRange(h1Fractals.Where(f => !f.IsHighFractal && f.Price < entryPrice));
                potentialTPs.AddRange(m15Fractals.Where(f => !f.IsHighFractal && f.Price < entryPrice));
                potentialTPs = potentialTPs.OrderByDescending(f => f.Price).ToList(); 
            }

            if (!potentialTPs.Any())
            {
                Print($"Trade invalidated: No suitable fractals found for TP. Bullish: {tradeInfo.IsBullish}");
                return false;
            }

            FractalInfo chosenFractal = null;
            double chosenTpPrice = 0;
            double chosenRR = 0;

            foreach (var fractalTp in potentialTPs)
            {
                double currentTpPrice = fractalTp.Price;
                double rewardDistance = Math.Abs(currentTpPrice - entryPrice);
                if (rewardDistance < Symbol.TickSize) continue; 

                double currentCalculatedRR = 0;
                if (riskDistanceInPrice > 0) 
                {
                     currentCalculatedRR = rewardDistance / riskDistanceInPrice;
                }

                if (currentCalculatedRR >= MinRR && currentCalculatedRR <= MaxRR)
                {
                    chosenFractal = fractalTp;
                    chosenTpPrice = currentTpPrice;
                    chosenRR = currentCalculatedRR;
                    break; 
                }
            }

            if (chosenFractal == null)
            {
                Print($"Trade invalidated: No fractal TP found that meets R/R criteria [{MinRR}-{MaxRR}].");
                return false;
            }
            
            tradeInfo.TakeProfitTargetPrice = chosenTpPrice;
            tradeInfo.RiskRewardRatio = chosenRR;
            tradeInfo.TakeProfitFractal = chosenFractal;

            double calculatedVolume = CalculatePositionVolume(stopLossPrice, entryPrice);
            if (calculatedVolume > 0)
            {
                tradeInfo.CalculatedVolumeInUnits = calculatedVolume;
                tradeInfo.IsValid = true; // Mark as valid for execution
                Print($"Trade Parameters Calculated: Entry: {tradeInfo.EntryPrice.ToString("F" + Symbol.Digits)}, SL: {tradeInfo.StopLossPrice.ToString("F" + Symbol.Digits)} (Base: {slBasePrice.ToString("F"+Symbol.Digits)} from {slReason}), TP: {tradeInfo.TakeProfitTargetPrice.ToString("F" + Symbol.Digits)} (Fractal {tradeInfo.TakeProfitFractal.TF} {tradeInfo.TakeProfitFractal.Time} @ {tradeInfo.TakeProfitFractal.Price.ToString("F"+Symbol.Digits)}), RR: {tradeInfo.RiskRewardRatio:F2}, VolLots: {Symbol.VolumeInUnitsToQuantity(calculatedVolume)}");
                return true;
            }
            else
            {
                Print($"Trade invalidated: Calculated volume is 0. Chosen TP: {tradeInfo.TakeProfitTargetPrice.ToString("F" + Symbol.Digits)}, RR: {tradeInfo.RiskRewardRatio:F2}");
                return false;
            }
        }

        // Renamed from ProcessSetups
        private void ProcessTradeExecution(TradeExecutionInfo tradeToExecute)
        {
            if (tradeToExecute == null || !tradeToExecute.IsValid) return;

            var existingPosition = Positions.FirstOrDefault(p => p.Label == TradeLabel && p.SymbolName == SymbolName);
            if (existingPosition != null)
            {
                Print("A position with this label already exists. No new trade will be opened.");
                return;
            }

            TradeType tradeType = tradeToExecute.IsBullish ? TradeType.Buy : TradeType.Sell;
            double volumeInUnits = tradeToExecute.CalculatedVolumeInUnits;
            string symbolName = SymbolName;
            
            // Используем SL/TP, рассчитанные в TryCalculateTradeParameters
            double intendedStopLossPrice = tradeToExecute.StopLossPrice;
            double? intendedTakeProfitPrice = tradeToExecute.TakeProfitTargetPrice; // TP может быть null, если не найден подходящий фрактал

            Print($"Attempting to open {tradeType} trade for {SymbolName} based on entry trigger at {tradeToExecute.EntryTriggerTime}.");
            Print($"  ARMING Pattern was: {_armingDetails?.ArmingPattern} at {_armingDetails?.ArmingSignalTime}");
            Print($"  Intended Entry (approx for SL/TP calc): {tradeToExecute.EntryPrice.ToString("F" + Symbol.Digits)}"); 
            Print($"  Calculated SL Price: {intendedStopLossPrice.ToString("F" + Symbol.Digits)}, Calculated TP Price: {intendedTakeProfitPrice?.ToString("F" + Symbol.Digits) ?? "N/A"}");
            Print($"  Volume (Units): {volumeInUnits}");
            Print($"  Volume (Lots): {Symbol.VolumeInUnitsToQuantity(volumeInUnits)}");
            Print($"  Original Risk/Reward: {tradeToExecute.RiskRewardRatio:F2}");
            Print($"  Stop Loss Trigger Method will be: Trade");

            // Открываем ордер СРАЗУ с SL/TP
            // Argument 7: comment (null)
            // Argument 8: hasTrailingStop (false)
            // Argument 9: stopLossTriggerMethod (StopTriggerMethod.Trade)
            var result = ExecuteMarketOrder(tradeType, symbolName, volumeInUnits, TradeLabel, intendedStopLossPrice, intendedTakeProfitPrice, null, false, StopTriggerMethod.Trade);

            if (result.IsSuccessful)
            {
                Position position = result.Position;
                Print($"Market Order Sent and SUCCEEDED: {tradeType} {Symbol.VolumeInUnitsToQuantity(volumeInUnits)} lots of {SymbolName}. Position ID: {position.Id}");
                Print($"  Actual Entry Price: {position.EntryPrice.ToString("F"+Symbol.Digits)}");
                Print($"  SL Sent: {intendedStopLossPrice.ToString("F"+Symbol.Digits)}, Actual SL on Position: {position.StopLoss?.ToString("F" + Symbol.Digits) ?? "N/A"}");
                Print($"  TP Sent: {intendedTakeProfitPrice?.ToString("F" + Symbol.Digits) ?? "N/A"}, Actual TP on Position: {position.TakeProfit?.ToString("F" + Symbol.Digits) ?? "N/A"}");
                Print($"  StopLoss Trigger on Position: {position.StopLossTriggerMethod}"); // Log actual trigger method

                // Проверка, если SL/TP не установились, несмотря на успешный ордер
                if (position.StopLoss == null && intendedStopLossPrice != 0) // intendedStopLossPrice != 0 to avoid warning if SL was meant to be none
                {
                    Print("WARNING: Stop Loss was sent but is NOT on the position. Possible rejection by server due to distance/rules.");
                }
                if (position.TakeProfit == null && intendedTakeProfitPrice.HasValue)
                {
                    Print("WARNING: Take Profit was sent but is NOT on the position. Possible rejection by server due to distance/rules.");
                }
            }
            else
            {
                Print($"Error opening trade: {result.Error}. SL tried: {intendedStopLossPrice.ToString("F"+Symbol.Digits)}, TP tried: {intendedTakeProfitPrice?.ToString("F"+Symbol.Digits) ?? "N/A"}");
            }
        }

        // Method to initialize the list of historical trades
        private void InitializeHistoricalTrades()
        {
            _historicalTrades.Clear();
            // IMPORTANT: Replace placeholder times (e.g., "HH:mm:ss") with the EXACT M15 bar open time in UTC.
            // Format: "yyyy-MM-dd HH:mm:ss"
            
            // Data extracted from screenshot (Year 2024 assumed for all)
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-05 07:00:00", true, "XAUUSD")); // XAU/USD, Nov 5, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-08 07:00:00", true, "GBPUSD")); // GBP/USD, Nov 8, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-08 07:00:00", true, "EURUSD")); // EUR/USD, Nov 8, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-21 07:00:00", false, "XAUUSD"));// XAU/USD, Nov 21, LONDON, Short - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-21 07:15:00", true, "XAUUSD")); // XAU/USD, Nov 21, LONDON, Long (assuming different time) - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-21 07:00:00", false, "EURUSD"));// EUR/USD, Nov 21, LONDON, Short - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-22 07:00:00", true, "XAUUSD")); // XAU/USD, Nov 22, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-25 07:00:00", true, "GBPUSD")); // GBP/USD, Nov 25, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-27 07:00:00", true, "XAUUSD")); // XAU/USD, Nov 27, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-27 07:00:00", true, "EURUSD")); // EUR/USD, Nov 27, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-28 07:00:00", true, "GBPUSD")); // GBP/USD, Nov 28, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-11-28 07:00:00", true, "XAUUSD")); // XAU/USD, Nov 28, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-12-04 07:00:00", true, "EURUSD")); // EUR/USD, Dec 4, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-12-05 12:00:00", true, "GBPUSD")); // GBP/USD, Dec 5, NY, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-12-10 12:00:00", false, "GBPUSD"));// GBP/USD, Dec 10, OVERLAP, Short - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-12-10 07:00:00", false, "XAUUSD"));// XAU/USD, Dec 10, LONDON, Short - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-12-11 12:00:00", true, "XAUUSD")); // XAU/USD, Dec 11, OVERLAP, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-12-11 07:00:00", false, "GBPUSD"));// GBP/USD, Dec 11, LONDON, Short - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-12-12 07:00:00", true, "EURUSD")); // EUR/USD, Dec 12, LONDON, Long - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-12-12 12:00:00", false, "EURUSD"));// EUR/USD, Dec 12, OVERLAP, Short - REPLACE TIME
            _historicalTrades.Add(new HistoricalTradeInfo("2024-12-12 12:00:00", false, "XAUUSD"));// XAU/USD, Dec 12, OVERLAP, Short - REPLACE TIME
            // Add more trades as needed
            // Example: _historicalTrades.Add(new HistoricalTradeInfo("YYYY-MM-DD HH:MM:SS", true, "EURUSD")); // For a Buy on EURUSD
        }

        private void ProcessHistoricalTradeTrigger()
        {
            if (!EnableHistoricalReplayMode) return;

            DateTime currentBarTime = Bars.OpenTimes.LastValue;

            foreach (var trade in _historicalTrades)
            {
                if (trade.Symbol == SymbolName && !trade.IsProcessed && trade.EntryTimeUTC == currentBarTime)
                {
                    Print($"HISTORICAL REPLAY: Matched historical trade for {SymbolName} at {currentBarTime}. IsLong: {trade.IsLong}");
                    trade.IsProcessed = true; // Mark as processed to avoid re-triggering

                    // Restore H1 context check for historical replay
                    // bool d1ContextAligned = true; // FORCED TRUE FOR DEBUGGING - This line is now removed/commented
                    // Print($"HISTORICAL REPLAY: D1 context check BYPASSED for historical trade. Assuming aligned. Current actual D1: {_currentD1Context}");

                    // Original H1 Context Check - Now active again
                    bool d1ContextAligned = (trade.IsLong && _currentD1Context == "Uptrend") ||
                                            (!trade.IsLong && _currentD1Context == "Downtrend");

                    if (!d1ContextAligned)
                    {
                        Print($"HISTORICAL REPLAY: H1 context ({_currentD1Context}) not aligned for historical trade. Skipping.");
                        return; // Stop processing this trade further
                    }
                    Print($"HISTORICAL REPLAY: H1 context ({_currentD1Context}) IS aligned.");
                    

                    // Simulate arming for this specific trade
                    _isArmedForLong = trade.IsLong;
                    _isArmedForShort = !trade.IsLong;
                    _armingDetails = new ArmingInfo(trade.IsLong)
                    {
                        IsMet = true,
                        ArmingPattern = "HistoricalReplay",
                        ArmingSignalTime = currentBarTime, // Or the actual setup time if available
                        // No specific LS or FVG needed here as we force the entry check
                    };
                    _setupArmedTime = currentBarTime;
                    Print($"HISTORICAL REPLAY: Bot ARMED {(trade.IsLong ? "LONG" : "SHORT")} for historical entry at {currentBarTime}.");

                    // Attempt to find an entry trigger *on this specific bar* using bot's rules
                    bool tradeExecuted = LookForEntryTriggerAndExecute();

                    if (tradeExecuted)
                    {
                        Print($"HISTORICAL REPLAY: Trade executed based on historical trigger at {currentBarTime}.");
                        ResetArmedState("Historical trade executed.");
                    }
                    else
                    {
                        Print($"HISTORICAL REPLAY: Entry trigger found as per historical log, but bot's current rules DID NOT find a valid FVG/LS entry on bar {currentBarTime}. No trade placed by bot logic.");
                        ResetArmedState("Historical trade attempt completed (no valid bot entry found).");
                    }
                    return; // Process one historical trade per bar for clarity
                }
            }
        }
    }
}
