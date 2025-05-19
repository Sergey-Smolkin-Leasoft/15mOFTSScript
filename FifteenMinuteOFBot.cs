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

        [Parameter("---- Fractal Parameters ----", Group = "Strategy - Fractals")]
        public string SeparatorFr { get; set; }

        [Parameter("Fractal Lookback Bars", DefaultValue = 30, MinValue = 3, MaxValue = 100, Group = "Strategy - Fractals")]
        public int FractalLookbackBars { get; set; } // How many bars to scan for fractals on M15 and H1

        [Parameter("---- FVG Parameters ----", Group = "Strategy - FVG")]
        public string SeparatorFVG { get; set; } // Dummy for spacing

        [Parameter("FVG Lookback Bars", DefaultValue = 20, MinValue = 3, Group = "Strategy - FVG")]
        public int FVGLookbackBars { get; set; }

        [Parameter("FVG Test Window Bars", DefaultValue = 3, MinValue = 1, MaxValue = 5, Group = "Strategy - FVG")]
        public int FVGTestWindowBars { get; set; } // How many recent bars to check for an FVG test

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

        // Session times are now hardcoded as per user request
        // Frankfurt: 06:00-07:00 UTC
        // London:    07:00-12:00 UTC
        // New York:  12:00-20:00 UTC

        // Internal state variables
        private string _currentD1Context = "Initializing...";
        private DateTime _lastD1ContextUpdateDate = DateTime.MinValue;

        private Bars _d1Bars;
        private Bars _h1Bars; 

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
            Print($"FVG Params: Lookback={FVGLookbackBars}, TestWindow={FVGTestWindowBars}");
            Print($"Fractal Params: Lookback={FractalLookbackBars}");

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
            // Determine D1 context only once per new D1 bar or on initial run
            bool newD1BarFormed = _d1Bars.OpenTimes.LastValue.Date > _lastD1ContextUpdateDate;

            if (!IsTradingSessionActive())
            {
                // Optional: Print log only if it changes state or periodically
                // Print("Trading session is not active. Skipping strategy logic.");
                return;
            }

            if (newD1BarFormed || _currentD1Context == "Initializing...")
            {
                 Print("New D1 bar formed or initial run, re-evaluating D1 context...");
                 DetermineD1Context();
                 _lastD1ContextUpdateDate = _d1Bars.OpenTimes.LastValue.Date;
            }

            // Main strategy logic will go here, gated by _currentD1Context
            if (_currentD1Context == "Uptrend")
            {
                // Look for 15mOF Long setups
                // Print("D1 Context: Uptrend. Looking for LONG 15mOF setups...");
                List<FVGInfo> m15fvgs = FindFVGs(Bars, FVGLookbackBars);
                List<LiquiditySweepInfo> m15sweeps = FindLiquiditySweeps(Bars);
                List<FractalInfo> m15Fractals = FindFractals(Bars, TimeFrame.Minute15, FractalLookbackBars);
                List<FractalInfo> h1Fractals = FindFractals(_h1Bars, TimeFrame.Hour, FractalLookbackBars); 
                // Mark FVG as tested
                foreach(var fvg in m15fvgs) { CheckAndMarkFVGTest(fvg, Bars, FVGTestWindowBars); }

                List<SetupInfo> setups = CheckForSetups(m15sweeps, m15fvgs, m15Fractals, h1Fractals, _currentD1Context);
                // Process setups
                ProcessSetups(setups);
            }
            else if (_currentD1Context == "Downtrend")
            {
                // Look for 15mOF Short setups
                // Print("D1 Context: Downtrend. Looking for SHORT 15mOF setups...");
                List<FVGInfo> m15fvgs = FindFVGs(Bars, FVGLookbackBars);
                List<LiquiditySweepInfo> m15sweeps = FindLiquiditySweeps(Bars);
                List<FractalInfo> m15Fractals = FindFractals(Bars, TimeFrame.Minute15, FractalLookbackBars);
                List<FractalInfo> h1Fractals = FindFractals(_h1Bars, TimeFrame.Hour, FractalLookbackBars); 
                // Mark FVG as tested
                foreach(var fvg in m15fvgs) { CheckAndMarkFVGTest(fvg, Bars, FVGTestWindowBars); }

                List<SetupInfo> setups = CheckForSetups(m15sweeps, m15fvgs, m15Fractals, h1Fractals, _currentD1Context);
                // Process setups
                ProcessSetups(setups);
            }
            else
            {
                // Print($"D1 Context: {_currentD1Context}. No 15mOF trades.");
            }
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
        private List<LiquiditySweepInfo> FindLiquiditySweeps(Bars series) // Changed to accept Bars
        {
            var sweeps = new List<LiquiditySweepInfo>();
            // We need at least LSLookbackBars + LSDetectionWindowBars 
            if (series.Count < LSLookbackBars + LSDetectionWindowBars + 1) // +1 because Last() is 1-indexed for recent bar
            {
                return sweeps; 
            }

            // Iterate through the detection window (recent bars)
            // Detection window starts from the last closed bar (index 1) up to LSDetectionWindowBars
            for (int i = 1; i <= LSDetectionWindowBars; i++)
            {
                var detectionBar = series.Last(i); // Bar being checked if it performed a sweep
                DateTime detectionBarTime = detectionBar.OpenTime;
                double detectionBarHigh = detectionBar.High;
                double detectionBarLow = detectionBar.Low;
                double detectionBarClose = detectionBar.Close;

                // Define the lookback period for finding the High/Low to be swept
                // This period is *before* the detectionBar
                int lookbackStartIdx = i + 1; // Start looking from the bar just before the detection bar
                int lookbackEndIdx = i + LSLookbackBars;

                if (series.Count <= lookbackEndIdx) continue; // Not enough history for this detection bar

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
                
                // Check for Bullish Sweep (High Swept - potential bearish signal)
                if (highBarIndex != -1 && detectionBarHigh > recentHigh && detectionBarClose < recentHigh)
                {
                    sweeps.Add(new LiquiditySweepInfo
                    {
                        Time = detectionBarTime,
                        SweptLevel = recentHigh,
                        IsBullishSweepAbove = true, // High was swept
                        ConfirmationBarIndex = i,
                        WickHigh = detectionBarHigh,
                        WickLow = detectionBarLow
                    });
                }

                // Check for Bearish Sweep (Low Swept - potential bullish signal)
                if (lowBarIndex != -1 && detectionBarLow < recentLow && detectionBarClose > recentLow)
                {
                    sweeps.Add(new LiquiditySweepInfo
                    {
                        Time = detectionBarTime,
                        SweptLevel = recentLow,
                        IsBullishSweepAbove = false, // Low was swept
                        ConfirmationBarIndex = i,
                        WickHigh = detectionBarHigh,
                        WickLow = detectionBarLow
                    });
                }
            }
            return sweeps.OrderBy(s => s.ConfirmationBarIndex).ToList(); // Return sweeps ordered by recency
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

        // Class to hold information about a potential trading setup
        public class SetupInfo
        {
            public bool IsValid { get; set; } = false;
            public string Rule2Pattern { get; set; } // e.g., "LS+LS", "LS+FVGTest", "FVGTest+FVGTest"
            public DateTime SignalTime { get; set; } // Time of the *second* (triggering) event in the pattern
            public bool IsBullish { get; set; }    // True for long setup, false for short
            
            public LiquiditySweepInfo PrimaryLS { get; set; } 
            public LiquiditySweepInfo SecondaryLS { get; set; } 
            public FVGInfo PrimaryFVGTest { get; set; } 
            public FVGInfo SecondaryFVGTest { get; set; } 

            // Entry, SL, TP details
            public double EntryPrice { get; set; }
            public double StopLossPrice { get; set; }
            public double TakeProfitTargetPrice { get; set; }
            public FractalInfo TakeProfitFractal { get; set; } // The fractal used as TP
            public double RiskRewardRatio { get; set; }
            public double CalculatedVolumeInUnits { get; set; }

            public SetupInfo(bool isBullish)
            {
                IsBullish = isBullish;
            }
        }

        // Method to check for 15mOF Setups based on Rules 1 & 2
        private List<SetupInfo> CheckForSetups(List<LiquiditySweepInfo> m15Sweeps, List<FVGInfo> m15FVGs, List<FractalInfo> m15Fractals, List<FractalInfo> h1Fractals, string d1Context)
        {
            var validSetups = new List<SetupInfo>();
            if (d1Context == "Ranging/Undefined" || d1Context == "Insufficient Data" || d1Context == "Initializing..." || d1Context.StartsWith("Error"))
            {
                return validSetups; // No trading if D1 context is not clear
            }

            bool lookingForBullish = d1Context == "Uptrend";

            // Rule 2.1: LS + LS
            // Iterate through all M15 Sweeps as the potential second (triggering) LS
            foreach (var ls2 in m15Sweeps)
            {
                // Check D1 alignment for ls2
                // Bullish setup: ls2 must be a low sweep (!ls2.IsBullishSweepAbove)
                // Bearish setup: ls2 must be a high sweep (ls2.IsBullishSweepAbove)
                if ((lookingForBullish && !ls2.IsBullishSweepAbove) || (!lookingForBullish && ls2.IsBullishSweepAbove))
                {
                    // Look for an earlier M15 Sweep (ls1)
                    foreach (var ls1 in m15Sweeps)
                    {
                        if (ls1 == ls2) continue; // Cannot be the same sweep

                        // ls1 must also align with D1 context
                        if (!((lookingForBullish && !ls1.IsBullishSweepAbove) || (!lookingForBullish && ls1.IsBullishSweepAbove))) continue;
                        
                        // ls1 must be older than or same bar as ls2
                        // ls1.ConfirmationBarIndex >= ls2.ConfirmationBarIndex means ls1 is older or same.
                        // ls1.Time <= ls2.Time
                        if (ls1.ConfirmationBarIndex >= ls2.ConfirmationBarIndex && ls1.Time < ls2.Time) // Strictly ls1 older
                        {
                            var setup = new SetupInfo(lookingForBullish)
                            {
                                Rule2Pattern = "LS+LS",
                                SignalTime = ls2.Time, // Time of the triggering (second) LS
                                PrimaryLS = ls1,
                                SecondaryLS = ls2,
                                IsValid = true // Placeholder for Rule 3 check
                            };
                            // Print($"Potential LS+LS: ls1 ({ls1.Time} idx {ls1.ConfirmationBarIndex}), ls2 ({ls2.Time} idx {ls2.ConfirmationBarIndex})");
                            validSetups.Add(setup);
                        }
                    }
                }
            }

            // Rule 2.2: LS + FVG Test
            // Iterate through all tested M15 FVGs as the potential FVG Test part
            foreach (var fvg_tested in m15FVGs)
            {
                if (!fvg_tested.IsTested) continue; // Must be a tested FVG
                if (fvg_tested.TestBarIndex < 1) continue; // Ensure TestBarIndex is valid

                // Check D1 alignment for the FVG Test (Bullish FVG for Uptrend, Bearish FVG for Downtrend)
                if ((lookingForBullish && fvg_tested.IsBullish) || (!lookingForBullish && !fvg_tested.IsBullish))
                {
                    // Look for an earlier M15 LS
                    foreach (var ls1 in m15Sweeps)
                    {
                        // ls1 must align with D1 context
                        if (!((lookingForBullish && !ls1.IsBullishSweepAbove) || (!lookingForBullish && ls1.IsBullishSweepAbove))) continue;

                        // ls1 (sweep) must be older than or same bar as the FVG test
                        // ls1.ConfirmationBarIndex >= fvg_tested.TestBarIndex
                        // ls1.Time <= Time of FVG test bar
                        DateTime fvgTestBarTime = Bars.OpenTimes.Last(fvg_tested.TestBarIndex);
                        if (ls1.ConfirmationBarIndex >= fvg_tested.TestBarIndex && ls1.Time < fvgTestBarTime) // Strictly ls1 older
                        {
                             var setup = new SetupInfo(lookingForBullish)
                            {
                                Rule2Pattern = "LS+FVGTest",
                                SignalTime = fvgTestBarTime, 
                                PrimaryLS = ls1,
                                PrimaryFVGTest = fvg_tested,
                                IsValid = true 
                            };
                            // Print($"Potential LS+FVGTest: ls1 ({ls1.Time} idx {ls1.ConfirmationBarIndex}), fvg_test ({fvgTestBarTime} idx {fvg_tested.TestBarIndex})");
                            validSetups.Add(setup);
                        }
                    }
                }
            }
            
            // Rule 2.3: FVG Test + FVG Test (and a preceding LS for Rule 1)
            // Iterate through all tested M15 FVGs as the potential second (triggering) FVG Test
            foreach (var fvg2_tested in m15FVGs)
            {
                if (!fvg2_tested.IsTested) continue;
                if (fvg2_tested.TestBarIndex < 1) continue;

                // Check D1 alignment for fvg2_tested
                if ((lookingForBullish && fvg2_tested.IsBullish) || (!lookingForBullish && !fvg2_tested.IsBullish))
                {
                    DateTime fvg2TestBarTime = Bars.OpenTimes.Last(fvg2_tested.TestBarIndex);
                    // Look for an earlier tested M15 FVG (fvg1_tested)
                    foreach (var fvg1_tested in m15FVGs)
                    {
                        if (fvg1_tested == fvg2_tested || !fvg1_tested.IsTested || fvg1_tested.TestBarIndex < 1) continue;
                        
                        // Check D1 alignment for fvg1_tested
                        if (!((lookingForBullish && fvg1_tested.IsBullish) || (!lookingForBullish && !fvg1_tested.IsBullish))) continue;

                        // fvg1_tested must be older than or same bar as fvg2_tested
                        DateTime fvg1TestBarTime = Bars.OpenTimes.Last(fvg1_tested.TestBarIndex);
                        if (fvg1_tested.TestBarIndex >= fvg2_tested.TestBarIndex && fvg1TestBarTime < fvg2TestBarTime) // Strictly fvg1_test older
                        {
                            // Now check for Rule 1: A preceding/concurrent M15 LS before fvg1_tested
                            LiquiditySweepInfo precedingLS = null;
                            foreach (var ls_check in m15Sweeps)
                            {
                                // LS must align with D1
                                if (!((lookingForBullish && !ls_check.IsBullishSweepAbove) || (!lookingForBullish && ls_check.IsBullishSweepAbove))) continue;

                                // LS must occur before or at the same time as the first FVG test (fvg1_tested)
                                // ls_check.ConfirmationBarIndex >= fvg1_tested.TestBarIndex
                                if (ls_check.ConfirmationBarIndex >= fvg1_tested.TestBarIndex && ls_check.Time <= fvg1TestBarTime)
                                {
                                    precedingLS = ls_check;
                                    break; 
                                }
                            }

                            if (precedingLS != null)
                            {
                                var setup = new SetupInfo(lookingForBullish)
                                {
                                    Rule2Pattern = "FVGTest+FVGTest",
                                    SignalTime = fvg2TestBarTime,
                                    PrimaryLS = precedingLS, // Rule 1 LS
                                    PrimaryFVGTest = fvg1_tested,
                                    SecondaryFVGTest = fvg2_tested,
                                    IsValid = true 
                                };
                                // Print($"Potential FVGTest+FVGTest: fvg1_test ({fvg1TestBarTime} idx {fvg1_tested.TestBarIndex}), fvg2_test ({fvg2TestBarTime} idx {fvg2_tested.TestBarIndex}), precedingLS ({precedingLS.Time})");
                                validSetups.Add(setup);
                            }
                        }
                    }
                }
            }
            
            // Filter setups by Rule 3 (TP/RR) and finalize valid ones
            var finalizedSetups = new List<SetupInfo>();
            foreach (var potentialSetup in validSetups)
            {
                if (TryFinalizeSetupWithRule3(potentialSetup, m15Fractals, h1Fractals))
                {
                    finalizedSetups.Add(potentialSetup);
                }
            }

            if (finalizedSetups.Any())
            {
                Print($"Found {finalizedSetups.Count} finalized setups for D1: {d1Context}.");
                // foreach(var s in finalizedSetups) { Print($"  - {s.Rule2Pattern} at {s.SignalTime}, Entry: {s.EntryPrice}, SL: {s.StopLossPrice}, TP: {s.TakeProfitTargetPrice}, RR: {s.RiskRewardRatio:F2}"); }
            }
            return finalizedSetups;
        }

        private bool TryFinalizeSetupWithRule3(SetupInfo setup, List<FractalInfo> m15Fractals, List<FractalInfo> h1Fractals)
        {
            double entryPrice = 0;
            double stopLossPrice = 0;
            double minStopLossPipsParamValue = 1.0; // This was the old minStopLossPips, used for threshold calc

            // Determine Entry Price based on the trigger of the Rule 2 pattern
            // And determine the base price level from which SL will be calculated
            double slBasePrice = 0;
            string slReason = "";

            if (setup.Rule2Pattern == "LS+LS" && setup.SecondaryLS != null)
            {
                if (setup.SecondaryLS.ConfirmationBarIndex < 1 || setup.SecondaryLS.ConfirmationBarIndex > Bars.Count) return false;
                entryPrice = Bars.ClosePrices.Last(setup.SecondaryLS.ConfirmationBarIndex);
                slBasePrice = setup.IsBullish ? setup.SecondaryLS.WickLow : setup.SecondaryLS.WickHigh;
                slReason = setup.IsBullish ? "SecondaryLS.WickLow" : "SecondaryLS.WickHigh";
            }
            else if (setup.Rule2Pattern == "LS+FVGTest" && setup.PrimaryFVGTest != null)
            {
                if (setup.PrimaryFVGTest.TestBarIndex < 1 || setup.PrimaryFVGTest.TestBarIndex > Bars.Count) return false;
                entryPrice = Bars.ClosePrices.Last(setup.PrimaryFVGTest.TestBarIndex);
                // For SL, use the Low/High of the bar that tested the FVG
                slBasePrice = setup.IsBullish ? Bars.LowPrices.Last(setup.PrimaryFVGTest.TestBarIndex) : Bars.HighPrices.Last(setup.PrimaryFVGTest.TestBarIndex);
                slReason = setup.IsBullish ? $"Low of FVG Test Bar (idx {setup.PrimaryFVGTest.TestBarIndex})" : $"High of FVG Test Bar (idx {setup.PrimaryFVGTest.TestBarIndex})";
            }
            else if (setup.Rule2Pattern == "FVGTest+FVGTest" && setup.SecondaryFVGTest != null)
            {
                if (setup.SecondaryFVGTest.TestBarIndex < 1 || setup.SecondaryFVGTest.TestBarIndex > Bars.Count) return false;
                entryPrice = Bars.ClosePrices.Last(setup.SecondaryFVGTest.TestBarIndex);
                // For SL, use the Low/High of the bar that tested the second FVG
                slBasePrice = setup.IsBullish ? Bars.LowPrices.Last(setup.SecondaryFVGTest.TestBarIndex) : Bars.HighPrices.Last(setup.SecondaryFVGTest.TestBarIndex);
                slReason = setup.IsBullish ? $"Low of Second FVG Test Bar (idx {setup.SecondaryFVGTest.TestBarIndex})" : $"High of Second FVG Test Bar (idx {setup.SecondaryFVGTest.TestBarIndex})";
            }
            else
            {
                Print($"Error: Setup has unknown Rule2Pattern '{setup.Rule2Pattern}' or missing components for SL/Entry.");
                return false; 
            }

            if (entryPrice == 0) 
            {
                Print("Error: Entry price is zero after pattern evaluation.");
                return false;
            }
            
            if (slBasePrice <= 0 || (slBasePrice < entryPrice * 0.5 && entryPrice > 0) || (slBasePrice > entryPrice * 1.5 && entryPrice > 0) )
            {
                Print($"Setup invalidated: SL base price ({slBasePrice.ToString("F" + Symbol.Digits)} from {slReason}) is zero, negative or absurd relative to entry price ({entryPrice.ToString("F" + Symbol.Digits)}).");
                setup.IsValid = false;
                return false;
            }

            stopLossPrice = setup.IsBullish 
                ? (slBasePrice - StopLossOffsetTicks * Symbol.TickSize) 
                : (slBasePrice + StopLossOffsetTicks * Symbol.TickSize);
            
            // Ensure SL is not on the wrong side of entry or exactly at entry
            if ((setup.IsBullish && stopLossPrice >= entryPrice) || (!setup.IsBullish && stopLossPrice <= entryPrice))
            {
                Print($"Setup invalidated: Stop Loss Price ({stopLossPrice.ToString("F" + Symbol.Digits)}) is on the wrong side or at entry price ({entryPrice.ToString("F" + Symbol.Digits)}). SL Base: {slBasePrice.ToString("F" + Symbol.Digits)}, Offset: {StopLossOffsetTicks} ticks.");
                setup.IsValid = false;
                return false;
            }

            // Print($"Debug SL Calc: Entry={entryPrice.ToString("F" + Symbol.Digits)}, SLBase={slBasePrice.ToString("F" + Symbol.Digits)} ({slReason}), SLOffsetTicks={StopLossOffsetTicks}, TickSize={Symbol.TickSize}, Calculated SL={stopLossPrice.ToString("F" + Symbol.Digits)}");

            double strategicPipSize = 10 * Symbol.TickSize; 
            if (strategicPipSize == 0) strategicPipSize = Symbol.TickSize; // Fallback if 10*TickSize is zero

            if (strategicPipSize == 0) {
                Print("Error: strategicPipSize is zero, cannot validate SL or calculate volume.");
                return false;
            }
            
            double riskDistanceInPrice = Math.Abs(entryPrice - stopLossPrice);
            if (riskDistanceInPrice < Symbol.TickSize) // Minimum possible SL is 1 tick
            {
                Print($"Setup invalidated: Risk distance ({riskDistanceInPrice}) is less than TickSize ({Symbol.TickSize}). SL likely too close or error in calculation.");
                setup.IsValid = false;
                return false;
            }

            double stopLossInStrategicPips = riskDistanceInPrice / strategicPipSize;
            double minStopLossInStrategicPipsThreshold = (minStopLossPipsParamValue * (Symbol.PipSize > 0 ? Symbol.PipSize : strategicPipSize) / strategicPipSize);
            if (stopLossInStrategicPips < minStopLossInStrategicPipsThreshold) 
            {
                 Print($"Setup invalidated: SL (in strategic pips) {stopLossInStrategicPips:F2} is less than min threshold {minStopLossInStrategicPipsThreshold:F2} for {setup.Rule2Pattern} at {setup.SignalTime}. Entry: {entryPrice}, SL: {stopLossPrice}");
                setup.IsValid = false;
                return false;
            }

            setup.EntryPrice = entryPrice;
            setup.StopLossPrice = stopLossPrice;

            // --- New Take Profit Logic using Fractals and R/R Range ---
            List<FractalInfo> potentialTPs = new List<FractalInfo>();
            if (setup.IsBullish)
            {
                potentialTPs.AddRange(h1Fractals.Where(f => f.IsHighFractal && f.Price > entryPrice));
                potentialTPs.AddRange(m15Fractals.Where(f => f.IsHighFractal && f.Price > entryPrice));
                potentialTPs = potentialTPs.OrderBy(f => f.Price).ToList(); // Closest TP first
            }
            else // Bearish
            {
                potentialTPs.AddRange(h1Fractals.Where(f => !f.IsHighFractal && f.Price < entryPrice));
                potentialTPs.AddRange(m15Fractals.Where(f => !f.IsHighFractal && f.Price < entryPrice));
                potentialTPs = potentialTPs.OrderByDescending(f => f.Price).ToList(); // Closest TP first
            }

            if (!potentialTPs.Any())
            {
                // Print($"Setup invalidated: No suitable fractals found for TP for {setup.Rule2Pattern} at {setup.SignalTime}. Bullish: {setup.IsBullish}");
                setup.IsValid = false;
                return false;
            }

            FractalInfo chosenFractal = null;
            double chosenTpPrice = 0;
            double chosenRR = 0;

            foreach (var fractalTp in potentialTPs)
            {
                double currentTpPrice = fractalTp.Price;
                double rewardDistance = Math.Abs(currentTpPrice - entryPrice);

                if (rewardDistance < Symbol.TickSize) continue; // TP must be at least 1 tick away from entry

                double currentRR = riskDistanceInPrice / rewardDistance; // Standard definition: Reward / Risk
                if (riskDistanceInPrice > 0) // Recalculate RR as Reward/Risk
                {
                     currentRR = rewardDistance / riskDistanceInPrice;
                } else {
                    currentRR = 0; // Avoid division by zero if risk is somehow zero
                }


                if (currentRR >= MinRR && currentRR <= MaxRR)
                {
                    chosenFractal = fractalTp;
                    chosenTpPrice = currentTpPrice;
                    chosenRR = currentRR;
                    break; // Found a suitable TP
                }
            }

            if (chosenFractal == null)
            {
                // Print($"Setup invalidated: No fractal TP found that meets R/R criteria [{MinRR}-{MaxRR}] for {setup.Rule2Pattern} at {setup.SignalTime}.");
                setup.IsValid = false;
                return false;
            }
            
            setup.TakeProfitTargetPrice = chosenTpPrice;
            setup.RiskRewardRatio = chosenRR;
            setup.TakeProfitFractal = chosenFractal;

            double calculatedVolume = CalculatePositionVolume(stopLossPrice, entryPrice);
            if (calculatedVolume > 0)
            {
                setup.CalculatedVolumeInUnits = calculatedVolume;
                setup.IsValid = true;
                Print($"Validated Setup (Fractal TP): {setup.Rule2Pattern} at {setup.SignalTime}, Entry: {setup.EntryPrice.ToString("F" + Symbol.Digits)}, SL: {setup.StopLossPrice.ToString("F" + Symbol.Digits)}, TP: {setup.TakeProfitTargetPrice.ToString("F" + Symbol.Digits)} (Fractal {setup.TakeProfitFractal.TF} {setup.TakeProfitFractal.Time} @ {setup.TakeProfitFractal.Price.ToString("F"+Symbol.Digits)}), RR: {setup.RiskRewardRatio:F2}, VolLots: {Symbol.VolumeInUnitsToQuantity(calculatedVolume)}");
                return true;
            }
            else
            {
                Print($"Setup invalidated (Fractal TP): Calculated volume is 0 for {setup.Rule2Pattern} at {setup.SignalTime}. Chosen TP: {setup.TakeProfitTargetPrice.ToString("F" + Symbol.Digits)}, RR: {setup.RiskRewardRatio:F2}");
                setup.IsValid = false;
                return false;
            }
        }

        private void ProcessSetups(List<SetupInfo> setups)
        {
            if (!setups.Any()) return;

            // Check if there are already positions with this label
            var existingPosition = Positions.FirstOrDefault(p => p.Label == TradeLabel && p.SymbolName == SymbolName);
            if (existingPosition != null)
            {
                // Print("A position with this label already exists. No new trade will be opened.");
                return;
            }

            // Take the first valid setup found (could be more sophisticated later, e.g. best RR)
            SetupInfo setupToTrade = setups.FirstOrDefault(s => s.IsValid);

            if (setupToTrade != null)
            {
                TradeType tradeType = setupToTrade.IsBullish ? TradeType.Buy : TradeType.Sell;
                double volumeInUnits = setupToTrade.CalculatedVolumeInUnits;
                string symbolName = SymbolName;
                double entry = setupToTrade.EntryPrice; // For market order, this is indicative
                double stopLoss = setupToTrade.StopLossPrice;
                double takeProfit = setupToTrade.TakeProfitTargetPrice;

                // Normalize prices before execution (manual approach)
                double normalizedStopLoss = Math.Round(stopLoss, Symbol.Digits); 
                double normalizedTakeProfit = Math.Round(takeProfit, Symbol.Digits); 

                Print($"Attempting to open {tradeType} trade for {SymbolName} based on {setupToTrade.Rule2Pattern}.");
                Print($"  Signal Time: {setupToTrade.SignalTime}");
                Print($"  Entry (indicative): {entry.ToString("F" + Symbol.Digits)}");
                Print($"  Stop Loss (calculated): {stopLoss.ToString("F" + Symbol.Digits)}, Normalized SL: {normalizedStopLoss.ToString("F" + Symbol.Digits)}");
                Print($"  Take Profit (calculated): {takeProfit.ToString("F" + Symbol.Digits)}, Normalized TP: {normalizedTakeProfit.ToString("F" + Symbol.Digits)}");
                
                string tpFractalTFStr = setupToTrade.TakeProfitFractal == null ? "N/A" : setupToTrade.TakeProfitFractal.TF.ToString();
                string tpFractalTimeStr = setupToTrade.TakeProfitFractal == null ? "N/A" : setupToTrade.TakeProfitFractal.Time.ToString();
                Print($"  Take Profit: {takeProfit.ToString("F" + Symbol.Digits)} (Fractal on {tpFractalTFStr} at {tpFractalTimeStr})");
                
                Print($"  Volume (Units): {volumeInUnits}");
                Print($"  Volume (Lots): {Symbol.VolumeInUnitsToQuantity(volumeInUnits)}");
                Print($"  Risk/Reward: {setupToTrade.RiskRewardRatio:F2}");

                // Execute order without SL/TP first
                var result = ExecuteMarketOrder(tradeType, symbolName, volumeInUnits, TradeLabel, null, null);

                if (result.IsSuccessful)
                {
                    Print($"Market Order Sent: {tradeType} {Symbol.VolumeInUnitsToQuantity(volumeInUnits)} lots of {SymbolName}. Position ID: {result.Position.Id}");
                    Position position = result.Position;
                    var modifyResult = ModifyPosition(position, normalizedStopLoss, normalizedTakeProfit);
                    if (modifyResult.IsSuccessful)
                    {
                        Print($"Position Modified: SL set to {normalizedStopLoss.ToString("F" + Symbol.Digits)}, TP set to {normalizedTakeProfit.ToString("F" + Symbol.Digits)}");
                        // Log the actual SL/TP from the position object after modification, if available and different
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
} 