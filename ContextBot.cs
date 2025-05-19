using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class ContextBot : Robot
    {
        [Parameter("Days for Context", DefaultValue = 3, MinValue = 1, Group = "Context Parameters")]
        public int DaysForContext { get; set; }

        [Parameter("Min. % Change for Context", DefaultValue = 3.75, MinValue = 0.01, Step = 0.01, Group = "Context Parameters")]
        public double PercentageChangeForContext { get; set; }

        private string _currentContext = "Initializing...";

        protected override void OnStart()
        {
            Print("ContextBot started.");
            Print($"Parameters: DaysForContext = {DaysForContext}, PercentageChangeForContext = {PercentageChangeForContext}%");

            // Bars.TimeFrame refers to the TimeFrame of the chart the cBot is running on.
            if (TimeFrame != TimeFrame.Daily)
            {
                Print("--------------------------------------------------------------------");
                Print("WARNING: For accurate 'Days for Context', apply this bot to a DAILY (D1) chart.");
                Print($"Current chart timeframe is: {TimeFrame}. Calculations might not reflect full days.");
                Print("--------------------------------------------------------------------");
            }
            
            DetermineContext();
        }

        protected override void OnBar()
        {
            DetermineContext();
        }

        private void DetermineContext()
        {
            // Bars.Count gives the total number of bars available in the series.
            // We need DaysForContext + 1 closed bars to make the comparison.
            // The Last(x) accessor is 0-indexed for the current bar, 1-indexed for the last closed bar.
            // So if DaysForContext is 3, we need to access Last(1) and Last(1+3) = Last(4).
            // This means we need at least 1+3+1 = 5 bars in the history (Bars.Count >= DaysForContext + 2).
            if (Bars.Count <= DaysForContext + 1) // Equivalent to needing at least DaysForContext + 2 bars
            {
                Print($"Not enough historical data to determine context. Need at least {DaysForContext + 2} bars. Currently have: {Bars.Count} bars.");
                _currentContext = "Insufficient Data";
                UpdateChartText();
                return;
            }

            // Get the close of the most recently completed bar (e.g., yesterday's close if on D1 and OnBar just fired)
            double mostRecentClose = Bars.ClosePrices.Last(1);

            // Get the close of the bar 'DaysForContext' periods prior to the mostRecentClose bar.
            int pastBarIndex = 1 + DaysForContext;
            double pastClose = Bars.ClosePrices.Last(pastBarIndex);

            if (pastClose == 0) 
            {
                Print($"Error: Past closing price (bar index {pastBarIndex} from end) is zero. Cannot calculate percentage change.");
                _currentContext = "Error: Zero Price";
                UpdateChartText();
                return;
            }

            double priceChange = mostRecentClose - pastClose;
            double percentageChange = (priceChange / pastClose) * 100;

            string previousContext = _currentContext;
            
            if (percentageChange >= PercentageChangeForContext)
            {
                _currentContext = "Uptrend";
            }
            else if (percentageChange <= -PercentageChangeForContext)
            {
                _currentContext = "Downtrend";
            }
            else
            {
                _currentContext = "Ranging/Undefined";
            }

            if (previousContext != _currentContext || previousContext == "Initializing...")
            {
                // Get timestamps for the bars used
                var mostRecentCloseBarTime = Bars.OpenTimes.Last(1);
                var pastCloseBarTime = Bars.OpenTimes.Last(pastBarIndex);

                Print($"Market Context Updated: {_currentContext}. Change: {percentageChange.ToString("F2")}% over last {DaysForContext} bar(s).");
                Print($"  Calculation based on:");
                Print($"    - Recent Bar (End of Period): Close {mostRecentClose.ToString("F" + Symbol.Digits)} on {mostRecentCloseBarTime}");
                Print($"    - Past Bar (Start of Period): Close {pastClose.ToString("F" + Symbol.Digits)} on {pastCloseBarTime}");
            } 
            // else 
            // {
            //    Print($"Market Context remains: {_currentContext}. Check: {percentageChange.ToString("F2")}% over {DaysForContext} bar(s) (from {pastClose.ToString("F" + Symbol.Digits)} to {mostRecentClose.ToString("F" + Symbol.Digits)}).");
            // }
            
            UpdateChartText(percentageChange);
        }

        private void UpdateChartText(double percentageChange = 0.0)
        {
            string textToShow;
            if (_currentContext == "Insufficient Data" || _currentContext == "Error: Zero Price" || _currentContext == "Initializing...")
            {
                textToShow = $"Context: {_currentContext}";
            }
            else
            {
                // Using verbatim string literal for easier multiline text
                textToShow = @$"Context: {_currentContext}
                Change: {percentageChange.ToString("F2")}% / {DaysForContext}D";
            }
            
            // Use Chart.DrawStaticText with VerticalAlignment and HorizontalAlignment
            Chart.DrawStaticText("ContextStatusText", textToShow, VerticalAlignment.Top, HorizontalAlignment.Right, cAlgo.API.Color.Yellow);
            // textResult.IsInteractive = false; // DrawStaticText might not return an object with IsInteractive, or it might not be needed.
                                             // If Chart.DrawStaticText returns void or an object without this property, this line should be removed.
                                             // For now, let's comment it out to avoid potential errors. The default is usually non-interactive.
        }

        protected override void OnStop()
        {
            Print("ContextBot stopped.");
            Chart.RemoveObject("ContextStatusText");
        }
    }
} 