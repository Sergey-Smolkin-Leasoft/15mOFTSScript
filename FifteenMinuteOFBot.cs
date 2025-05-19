using System;
using System.Linq;
using System.Collections.Generic; // Required for List<T>
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // Structure to hold FVG Information
    public class FVGInfo
    {
        public DateTime Time { get; set; }     // Time of the middle bar of FVG (Bar 2)
        public double Top { get; set; }          // Top price of the FVG range
        public double Bottom { get; set; }       // Bottom price of the FVG range
        public bool IsBullish { get; set; }      // True if bullish FVG, false if bearish
        public int SourceBarIndex { get; set; } // Index of the middle bar (Bar 2) from the end of the series (e.g., Last(SourceBarIndex))
        public bool IsFilled { get; set; } = false; // Later we can check if it has been filled
        public bool IsTested { get; set; } = false; // New property
        public int TestBarIndex { get; set; } = -1; // Index of the bar that tested this FVG (1 = last closed bar)
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

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class FifteenMinuteOFBot : Robot
    {
        [Parameter("---- D1 Context Parameters ----", Group = "Context")]
        public string Separator1 { get; set; } // Dummy for spacing

        [Parameter("D1 Days for Context", DefaultValue = 3, MinValue = 1, Group = "Context")]
        public int D1DaysForContext { get; set; }

        [Parameter("D1 Min. % Change", DefaultValue = 1.0, MinValue = 0.01, Step = 0.01, Group = "Context")]
        public double D1PercentageChangeForContext { get; set; }

        [Parameter("---- Liquidity Sweep (LS) Parameters ----", Group = "Strategy - LS")]
        public string SeparatorLS { get; set; }

        [Parameter("LS Lookback Bars", DefaultValue = 10, MinValue = 3, MaxValue = 50, Group = "Strategy - LS")]
        public int LSLookbackBars { get; set; }

        [Parameter("LS Detection Window Bars", DefaultValue = 3, MinValue = 1, MaxValue = 5, Group = "Strategy - LS")]
        public int LSDetectionWindowBars { get; set; } // How many recent bars to check for a sweep event

        [Parameter("---- Rule 1 LS Parameters ----", Group = "Strategy - Rule 1 LS")]
        public string SeparatorRule1LS { get; set; }

        [Parameter("Rule 1 LS Lookback Bars", DefaultValue = 20, MinValue = 3, MaxValue = 100, Group = "Strategy - Rule 1 LS")]
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

        [Parameter("Risk per Trade (%)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Step = 0.1, Group = "Strategy")]
        public double RiskPercentPerTrade { get; set; }

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

        // Session times are now hardcoded as per user request
        // Frankfurt: 06:00-07:00 UTC
        // London:    07:00-12:00 UTC
        // New York:  12:00-20:00 UTC

        // Internal state variables
        private string _currentD1Context = "Initializing...";
        private DateTime _lastD1ContextUpdateDate = DateTime.MinValue;

        private Bars _d1Bars;
        private Bars _h1Bars; 

        // Arming State Variables
        private bool _isArmedForLong = false;
        private bool _isArmedForShort = false;
        private ArmingInfo _armingDetails = null;
        private DateTime _setupArmedTime = DateTime.MinValue;

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
            Print($"D1 Context: Days={D1DaysForContext}, Min%Change={D1PercentageChangeForContext}");
            Print($"Strategy: Risk={RiskPercentPerTrade}%, SL Offset Ticks={StopLossOffsetTicks}, Label='{TradeLabel}' (Using Fixed RR=2.0)");
            Print($"LS Params: Lookback={LSLookbackBars}, DetectionWindow={LSDetectionWindowBars}");
            Print($"Rule 1 LS Params: Lookback={Rule1_LSLookbackBars}, DetectionWindow={Rule1_LSDetectionWindowBars}");
            Print($"FVG Params: Lookback={FVGLookbackBars}, TestWindow={FVGTestWindowBars}, ImpulseLookback={FVGImpulseLookbackBars}");
            Print($"Fractal Params: Lookback={FractalLookbackBars}");
            Print($"Strategy Params: MaxArmedDurationBars={MaxArmedDurationBars}");

            if (TimeFrame != TimeFrame.Minute15)
            {
                Print("WARNING: This bot is designed for the M15 timeframe. Please apply it to an M15 chart.");
            }
            
            _d1Bars = MarketData.GetBars(TimeFrame.Daily);
            _h1Bars = MarketData.GetBars(TimeFrame.Hour); 
            DetermineD1Context(); // Initial D1 context
            _lastD1ContextUpdateDate = _d1Bars.OpenTimes.LastValue.Date; // Store date of initial context update
        }

        protected override void OnBar()
        {
            if (!IsTradingSessionActive())
            {
                // Print("Trading session is not active. Skipping strategy logic.");
                return;
            }

            bool newD1BarFormed = _d1Bars.OpenTimes.LastValue.Date > _lastD1ContextUpdateDate;
            if (newD1BarFormed || _currentD1Context == "Initializing...")
            {
                 Print("New D1 bar formed or initial run, re-evaluating D1 context...");
                 DetermineD1Context();
                 _lastD1ContextUpdateDate = _d1Bars.OpenTimes.LastValue.Date;
                 // If D1 context changes, reset any armed state
                 ResetArmedState("D1 Context Changed");
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

            // If not armed, and D1 context is clear, look for an arming condition
            if (!_isArmedForLong && !_isArmedForShort)
            {
                if (_currentD1Context == "Uptrend" || _currentD1Context == "Downtrend")
                {
                    List<FVGInfo> m15fvgs = FindFVGs(Bars, FVGLookbackBars);
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
                            Print($"BOT ARMED FOR LONG. Pattern: {_armingDetails.ArmingPattern}, Signal Time: {_armingDetails.ArmingSignalTime}, D1: {_currentD1Context}");
                        }
                        else
                        {
                            _isArmedForShort = true;
                            Print($"BOT ARMED FOR SHORT. Pattern: {_armingDetails.ArmingPattern}, Signal Time: {_armingDetails.ArmingSignalTime}, D1: {_currentD1Context}");
                        }
                    }
                }
                else
                {
                    // Print($"D1 Context: {_currentD1Context}. Not looking for arming conditions.");
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

        // Placeholder - to be fully implemented
        private bool LookForEntryTriggerAndExecute()
        {
            // Print("DEBUG: LookForEntryTriggerAndExecute() called.");

            // 1. Find *new* M15 FVGs and *new* M15 LSs.
            // We need the freshest data. Consider only elements formed/confirmed on the very last closed bar (index 1).
            List<FVGInfo> currentFVGs = FindFVGs(Bars, FVGLookbackBars); // Use general FVG lookback
            foreach(var fvg in currentFVGs) { CheckAndMarkFVGTest(fvg, Bars, FVGTestWindowBars); }

            // For entry triggers, we are interested in sweeps confirmed on the *last closed bar* (index 1)
            List<LiquiditySweepInfo> currentLSs = FindLiquiditySweeps(Bars, LSLookbackBars, LSDetectionWindowBars); // Use general LS params

            TradeExecutionInfo tradeInfo = null;
            bool entryTriggerFound = false;
            double entryPrice = Bars.ClosePrices.Last(1); // Default entry is close of the trigger bar

            if (_isArmedForLong)
            {
                // Priority 1: FVG Test as entry trigger
                FVGInfo bullishEntryFVG = currentFVGs.FirstOrDefault(fvg => fvg.IsBullish && 
                                                                    fvg.IsTested && 
                                                                    fvg.TestBarIndex == 1); // Tested on last closed bar
                if (bullishEntryFVG != null)
                {
                    Print($"ARMED LONG: Bullish FVG Test entry trigger found at {bullishEntryFVG.Time}, tested by bar {Bars.OpenTimes.Last(1)}");
                    tradeInfo = new TradeExecutionInfo(true) 
                    {
                        EntryTriggerTime = Bars.OpenTimes.Last(1),
                        EntryPrice = entryPrice, 
                        EntryFVG = bullishEntryFVG
                    };
                    entryTriggerFound = true;
                }

                // Priority 2: Local Bullish LS as entry trigger (if no FVG test)
                if (!entryTriggerFound)
                {
                    LiquiditySweepInfo bullishEntryLS = currentLSs.FirstOrDefault(ls => !ls.IsBullishSweepAbove && // Low swept, bullish signal
                                                                                ls.ConfirmationBarIndex == 1); // Confirmed on last closed bar
                    if (bullishEntryLS != null)
                    {
                        Print($"ARMED LONG: Bullish Local LS entry trigger found at {bullishEntryLS.Time}");
                        tradeInfo = new TradeExecutionInfo(true)
                        {
                            EntryTriggerTime = Bars.OpenTimes.Last(1),
                            EntryPrice = entryPrice,
                            EntryLS = bullishEntryLS
                        };
                        entryTriggerFound = true;
                    }
                }
            }
            else if (_isArmedForShort)
            {
                // Priority 1: FVG Test as entry trigger
                FVGInfo bearishEntryFVG = currentFVGs.FirstOrDefault(fvg => !fvg.IsBullish && 
                                                                    fvg.IsTested && 
                                                                    fvg.TestBarIndex == 1); // Tested on last closed bar
                if (bearishEntryFVG != null)
                {
                    Print($"ARMED SHORT: Bearish FVG Test entry trigger found at {bearishEntryFVG.Time}, tested by bar {Bars.OpenTimes.Last(1)}");
                    tradeInfo = new TradeExecutionInfo(false) 
                    {
                        EntryTriggerTime = Bars.OpenTimes.Last(1),
                        EntryPrice = entryPrice, 
                        EntryFVG = bearishEntryFVG
                    };
                    entryTriggerFound = true;
                }

                // Priority 2: Local Bearish LS as entry trigger (if no FVG test)
                if (!entryTriggerFound)
                {
                    LiquiditySweepInfo bearishEntryLS = currentLSs.FirstOrDefault(ls => ls.IsBullishSweepAbove && // High swept, bearish signal
                                                                                 ls.ConfirmationBarIndex == 1); // Confirmed on last closed bar
                    if (bearishEntryLS != null)
                    {
                        Print($"ARMED SHORT: Bearish Local LS entry trigger found at {bearishEntryLS.Time}");
                        tradeInfo = new TradeExecutionInfo(false)
                        {
                            EntryTriggerTime = Bars.OpenTimes.Last(1),
                            EntryPrice = entryPrice,
                            EntryLS = bearishEntryLS
                        };
                        entryTriggerFound = true;
                    }
                }
            }

            if (entryTriggerFound && tradeInfo != null)
            {
                // Fetch fractals for TP calculation
                List<FractalInfo> m15Fractals = FindFractals(Bars, TimeFrame.Minute15, FractalLookbackBars);
                List<FractalInfo> h1Fractals = FindFractals(_h1Bars, TimeFrame.Hour, FractalLookbackBars);

                if (TryCalculateTradeParameters(tradeInfo, m15Fractals, h1Fractals))
                {
                    if (tradeInfo.IsValid) // Double check IsValid from TryCalculateTradeParameters
                    {
                        ProcessTradeExecution(tradeInfo);
                        return true; // Signal that trade was attempted, reset armed state
                    }
                    else
                    {
                        Print("Entry trigger found, but trade parameters were invalid. Remaining armed.");
                    }
                }
                else
                {
                    Print("Entry trigger found, but failed to calculate trade parameters. Remaining armed.");
                }
            }
            
            return false; // No valid entry trigger found or executed
        }

        private void DetermineD1Context()
        {
            if (_d1Bars.ClosePrices.Count <= D1DaysForContext) // Adjusted condition
            {
                Print($"Not enough D1 historical data. Need more than {D1DaysForContext} bars, have {_d1Bars.ClosePrices.Count}. D1 Context: Insufficient Data");
                _currentD1Context = "Insufficient Data";
                return;
            }

            // Use the close of the very last available D1 bar as the most recent reference point
            double mostRecentD1Close = _d1Bars.ClosePrices.LastValue; 
            DateTime mostRecentD1OpenTime = _d1Bars.OpenTimes.LastValue;

            int pastD1BarIndex = D1DaysForContext; // Index for Last(X) relative to the end of the series
            
            // Ensure the pastD1BarIndex is valid (e.g., if D1DaysForContext is 0, Last(0) is not what we want for a *past* bar)
            if (pastD1BarIndex < 1 || _d1Bars.ClosePrices.Count <= pastD1BarIndex) 
            {
                 Print($"Invalid D1DaysForContext ({D1DaysForContext}) or not enough D1 data for past bar. Count: {_d1Bars.ClosePrices.Count}. D1 Context: Insufficient Data for Past Bar");
                _currentD1Context = "Insufficient Data for Past Bar";
                return;
            }
            double pastD1Close = _d1Bars.ClosePrices.Last(pastD1BarIndex);
            DateTime pastD1OpenTime = _d1Bars.OpenTimes.Last(pastD1BarIndex);

            if (pastD1Close == 0)
            {
                Print("Error: Past D1 closing price is zero. D1 Context: Error Zero Price");
                _currentD1Context = "Error: Zero Price";
                return;
            }

            double priceChange = mostRecentD1Close - pastD1Close;
            double percentageChange = (priceChange / pastD1Close) * 100;

            string determinedContext;
            if (percentageChange >= D1PercentageChangeForContext)
            {
                determinedContext = "Uptrend";
            }
            else if (percentageChange <= -D1PercentageChangeForContext)
            {
                determinedContext = "Downtrend";
            }
            else
            {
                determinedContext = "Ranging/Undefined";
            }

            if (_currentD1Context != determinedContext || _currentD1Context == "Initializing...")
            {
                _currentD1Context = determinedContext;
                Print($"D1 Market Context Updated: {_currentD1Context}. Change: {percentageChange:F2}% over last {D1DaysForContext} D1 bar(s).");
                Print($"  D1 Calc: Recent Close {mostRecentD1OpenTime:yyyy-MM-dd} ({mostRecentD1Close.ToString("F" + Symbol.Digits)}) vs Past Close {pastD1OpenTime:yyyy-MM-dd} ({pastD1Close.ToString("F" + Symbol.Digits)})");
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
        private List<FVGInfo> FindFVGs(Bars series, int lookbackBars) // Changed to accept Bars
        {
            var fvgs = new List<FVGInfo>();
            // Ensure we have enough bars: lookbackBars for iterations, and +2 for the 3-bar pattern itself.
            // We iterate from (lookbackBars - 1 + 2) down to 2 (index from end)
            // Bar indices from end: Bar[0] is current forming, Bar[1] is last closed, Bar[2] is one before last closed.
            // For a 3-bar pattern ending at Bar[i], we need Bar[i], Bar[i+1], Bar[i+2]
            // So, Bar 1 of pattern is Bar[i+2], Bar 2 is Bar[i+1], Bar 3 is Bar[i]
            // We need to access Bars.HighPrices.Last(i), Bars.LowPrices.Last(i), etc.
            // Minimum index for Last() is 1 (last closed bar).
            // If lookbackBars = 20, we check patterns ending from bar Last(1) up to Last(20-1 = 19)
            // The pattern itself uses 3 bars. The last bar of the pattern (Bar 3) can be Bars.Last(1).
            // So, Bar 2 is Bars.Last(2), Bar 1 is Bars.Last(3).
            // Iteration for Bar 3 index: from 1 (most recent) up to lookbackBars -1 +1 = lookbackBars

            if (series.Count < Math.Max(lookbackBars, 3)) // General check if enough bars for any FVG
            {
                //Print("Not enough bars on M15 to find FVGs.");
                return fvgs;
            }

            // Iterate for the third bar of the 3-bar FVG pattern
            // Bar3_idx is the index from the end (1 = last closed bar)
            for (int bar3_idx = 1; bar3_idx <= Math.Min(lookbackBars, series.Count - 2); bar3_idx++)
            {
                int bar2_idx = bar3_idx + 1;
                int bar1_idx = bar3_idx + 2;

                // Bar 1 (oldest in pattern)
                double bar1High = series.HighPrices.Last(bar1_idx);
                double bar1Low = series.LowPrices.Last(bar1_idx);
                // Bar 2 (middle bar - creates imbalance)
                DateTime bar2Time = series.OpenTimes.Last(bar2_idx);
                // Bar 3 (newest in pattern)
                double bar3High = series.HighPrices.Last(bar3_idx);
                double bar3Low = series.LowPrices.Last(bar3_idx);

                // Check for Bullish FVG
                // Low of Bar 1 is above High of Bar 3
                if (bar1Low > bar3High)
                {
                    fvgs.Add(new FVGInfo 
                    {
                        Time = bar2Time,
                        Top = bar1Low,
                        Bottom = bar3High,
                        IsBullish = true,
                        SourceBarIndex = bar2_idx
                    });
                }
                // Check for Bearish FVG
                // High of Bar 1 is below Low of Bar 3
                else if (bar1High < bar3Low)
                {
                    fvgs.Add(new FVGInfo
                    {
                        Time = bar2Time,
                        Top = bar1High,      // For Bearish FVG, Top is Bar1.High
                        Bottom = bar3Low,    // For Bearish FVG, Bottom is Bar3.Low
                        IsBullish = false,
                        SourceBarIndex = bar2_idx
                    });
                }
            }
            return fvgs.OrderBy(f => f.SourceBarIndex).ToList(); // Newest first (smallest SourceBarIndex)
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
        private void CheckAndMarkFVGTest(FVGInfo fvg, Bars series, int testWindowBars)
        {
            if (fvg.IsTested) return; // Already marked as tested

            // Iterate through recent bars to see if any of them tested this FVG
            // FVG is defined by its SourceBarIndex (middle bar). Test occurs on bars newer than FVG.SourceBarIndex.
            // We need to compare current bars (from index 1 up to testWindowBars) against an older FVG.
            for (int i = 1; i <= Math.Min(testWindowBars, series.Count -1 ); i++)
            {
                // Only check bars that are more recent than the FVG itself.
                // FVG's SourceBarIndex is from the end. A smaller index 'i' is more recent.
                if (i >= fvg.SourceBarIndex) continue;

                double barHigh = series.HighPrices.Last(i);
                double barLow = series.LowPrices.Last(i);

                if (fvg.IsBullish)
                {
                    // Bullish FVG (gap is between fvg.Bottom and fvg.Top, fvg.Bottom < fvg.Top)
                    // Test is if a bar's Low dips into the FVG (barLow <= fvg.Top) and is above or at fvg.Bottom
                    if (barLow <= fvg.Top && barHigh >= fvg.Bottom) // Price entered the FVG zone
                    {
                        fvg.IsTested = true;
                        fvg.TestBarIndex = i; // Store the index of the testing bar
                        // Print($"Bullish FVG at {fvg.Time} (idx {fvg.SourceBarIndex}) TESTED by bar at {series.OpenTimes.Last(i)} (idx {i})");
                        return; // Mark as tested and exit
                    }
                }
                else // Bearish FVG
                {
                    // Bearish FVG (gap is between fvg.Bottom and fvg.Top, fvg.Bottom < fvg.Top)
                    // Test is if a bar's High rises into the FVG (barHigh >= fvg.Bottom) and is below or at fvg.Top
                    if (barHigh >= fvg.Bottom && barLow <= fvg.Top) // Price entered the FVG zone
                    {
                        fvg.IsTested = true;
                        fvg.TestBarIndex = i; // Store the index of the testing bar
                        // Print($"Bearish FVG at {fvg.Time} (idx {fvg.SourceBarIndex}) TESTED by bar at {series.OpenTimes.Last(i)} (idx {i})");
                        return; // Mark as tested and exit
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
            public FVGInfo PrimaryFVGTest { get; set; } 
            public FVGInfo SecondaryFVGTest { get; set; } 

            public ArmingInfo(bool isBullish)
            {
                IsBullish = isBullish;
            }
        }

        // Renamed from CheckForSetups. Now returns ArmingInfo and doesn't do SL/TP.
        private ArmingInfo IdentifyArmingCondition(List<LiquiditySweepInfo> m15Rule1Sweeps, List<LiquiditySweepInfo> m15SecondarySweeps, List<FVGInfo> m15FVGs, string d1Context)
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
            public FVGInfo EntryFVG { get; set; } // if entry was FVG based
            public LiquiditySweepInfo EntryLS { get; set; } // if entry was LS based

            public TradeExecutionInfo(bool isBullish)
            {
                IsBullish = isBullish;
            }
        }

        // Renamed from TryFinalizeSetupWithRule3 - role changed
        // Now this function is primarily responsible for calculating SL (new logic), TP (fractal), RR and Volume
        // based on a CONFIRMED entry trigger (new FVG test or new LS).
        // The 'setup' argument should now be of type TradeExecutionInfo, 
        // and it will also need access to _armingDetails for context if necessary for SL.
        private bool TryCalculateTradeParameters(TradeExecutionInfo tradeInfo, List<FractalInfo> m15Fractals, List<FractalInfo> h1Fractals)
        {
            // tradeInfo.EntryPrice should already be set by LookForEntryTriggerAndExecute()
            // tradeInfo.IsBullish also set.
            // tradeInfo.EntryFVG or tradeInfo.EntryLS should be set.

            double entryPrice = tradeInfo.EntryPrice;
            double stopLossPrice = 0;
            double slBasePrice = 0;
            string slReason = "";

            // New SL Logic ("Вариант В")
            if (tradeInfo.EntryFVG != null) // FVG-based entry trigger
            {
                FVGInfo entryFVG = tradeInfo.EntryFVG;
                // Find swing low/high of the impulse that created this entryFVG
                int impulseStartIndex = entryFVG.SourceBarIndex + 1; // Bar 1 of the FVG
                double relevantPrice = tradeInfo.IsBullish ? double.MaxValue : double.MinValue;

                for (int i = 0; i < FVGImpulseLookbackBars; ++i)
                {
                    int barIdxToInspect = impulseStartIndex + i;
                    if (barIdxToInspect >= Bars.Count) break; // Don't go out of bounds

                    if (tradeInfo.IsBullish)
                    {
                        relevantPrice = Math.Min(relevantPrice, Bars.LowPrices.Last(barIdxToInspect));
                    }
                    else
                    {
                        relevantPrice = Math.Max(relevantPrice, Bars.HighPrices.Last(barIdxToInspect));
                    }
                }
                if ((tradeInfo.IsBullish && relevantPrice == double.MaxValue) || (!tradeInfo.IsBullish && relevantPrice == double.MinValue))
                {
                     Print($"Error finding SL base for FVG entry: No valid low/high in FVGImpulseLookbackBars ({FVGImpulseLookbackBars}) from FVG at {entryFVG.Time}");
                     return false;
                }
                slBasePrice = relevantPrice;
                slReason = $"{(tradeInfo.IsBullish ? "SwingLow" : "SwingHigh")} of FVG impulse (FVG @{entryFVG.Time}, Lookback {FVGImpulseLookbackBars} bars)";
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

            // Min SL check (placeholder value for minStopLossPipsParamValue, could be a param if needed)
            double minStopLossPipsParamValue = 1.0; 
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
            // Entry price for market order is indicative. Actual entry will be based on market.
            // double entry = tradeToExecute.EntryPrice; 
            double stopLoss = tradeToExecute.StopLossPrice;
            double takeProfit = tradeToExecute.TakeProfitTargetPrice;

            double normalizedStopLoss = Math.Round(stopLoss, Symbol.Digits); 
            double normalizedTakeProfit = Math.Round(takeProfit, Symbol.Digits); 

            Print($"Attempting to open {tradeType} trade for {SymbolName} based on entry trigger at {tradeToExecute.EntryTriggerTime}.");
            Print($"  ARMING Pattern was: {_armingDetails?.ArmingPattern} at {_armingDetails?.ArmingSignalTime}");
            Print($"  Entry (indicative): {tradeToExecute.EntryPrice.ToString("F" + Symbol.Digits)}");
            Print($"  Stop Loss (calculated): {stopLoss.ToString("F" + Symbol.Digits)}, Normalized SL: {normalizedStopLoss.ToString("F" + Symbol.Digits)}");
            Print($"  Take Profit (calculated): {takeProfit.ToString("F" + Symbol.Digits)}, Normalized TP: {normalizedTakeProfit.ToString("F" + Symbol.Digits)}");
            
            string tpFractalTFStr = tradeToExecute.TakeProfitFractal == null ? "N/A" : tradeToExecute.TakeProfitFractal.TF.ToString();
            string tpFractalTimeStr = tradeToExecute.TakeProfitFractal == null ? "N/A" : tradeToExecute.TakeProfitFractal.Time.ToString();
            Print($"  Take Profit Fractal: {tpFractalTFStr} at {tpFractalTimeStr}");
            
            Print($"  Volume (Units): {volumeInUnits}");
            Print($"  Volume (Lots): {Symbol.VolumeInUnitsToQuantity(volumeInUnits)}");
            Print($"  Risk/Reward: {tradeToExecute.RiskRewardRatio:F2}");

            var result = ExecuteMarketOrder(tradeType, symbolName, volumeInUnits, TradeLabel, null, null);

            if (result.IsSuccessful)
            {
                Print($"Market Order Sent: {tradeType} {Symbol.VolumeInUnitsToQuantity(volumeInUnits)} lots of {SymbolName}. Position ID: {result.Position.Id}");
                Position position = result.Position;
                var modifyResult = ModifyPosition(position, normalizedStopLoss, normalizedTakeProfit);
                if (modifyResult.IsSuccessful)
                {
                    Print($"Position Modified: SL set to {normalizedStopLoss.ToString("F" + Symbol.Digits)}, TP set to {normalizedTakeProfit.ToString("F" + Symbol.Digits)}");
                    Print($"Trade Confirmed: Entry at {position.EntryPrice.ToString("F" + Symbol.Digits)}. SL Actual: {position.StopLoss?.ToString("F" + Symbol.Digits) ?? "N/A"}, TP Actual: {position.TakeProfit?.ToString("F" + Symbol.Digits) ?? "N/A"}");
                }
                else
                {
                    Print($"Error modifying position {position.Id} to set SL/TP: {modifyResult.Error}");
                }
            }
            else
            {
                Print($"Error opening trade: {result.Error}");
            }
        }
    }
}
