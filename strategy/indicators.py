# strategy/indicators.py

import pandas as pd
import numpy as np
from typing import List, Dict, Any

def is_swing_high(data, i, lookback):
    """Check if candle at index i is a Swing High."""
    if i < lookback or i >= len(data) - lookback:
        return False
    # Ensure the index i is within the bounds to check lookback candles before and after
    if i - lookback < 0 or i + lookback >= len(data):
        return False

    return data['High'].iloc[i] == data['High'].iloc[i-lookback : i+lookback+1].max()

def is_swing_low(data, i, lookback):
    """Check if candle at index i is a Swing Low."""
    if i < lookback or i >= len(data) - lookback:
        return False
    # Ensure the index i is within the bounds to check lookback candles before and after
    if i - lookback < 0 or i + lookback >= len(data):
        return False

    return data['Low'].iloc[i] == data['Low'].iloc[i-lookback : i+lookback+1].min()

def find_liquidity_levels(data: pd.DataFrame, lookback: int, is_swing_points=False) -> List[Dict[str, Any]]:
    """
    Finds Swing High/Low levels (potential liquidity) or just Swing Points.
    is_swing_points=True returns all identified Swing Points as dictionaries.
    When called with is_swing_points=False (e.g., for H1 context), it also returns all identified SH/SL as dictionaries.
    Filtering for 'swept' levels happens in the strategy logic.
    """
    levels = []
    # Need enough data to check lookback candles on both sides, minimum is 2*lookback + 1
    if data is None or data.empty or len(data) < lookback * 2 + 1:
        return levels

    # Iterate only where we can check the full lookback window
    for i in range(lookback, len(data) - lookback):
        if is_swing_high(data, i, lookback):
            # CORRECTED: Append dictionary instead of tuple
            levels.append({'time': data.index[i], 'price': data['High'].iloc[i], 'type': 'BSL', 'is_swept': False})
        if is_swing_low(data, i, lookback):
            # CORRECTED: Append dictionary instead of tuple
            levels.append({'time': data.index[i], 'price': data['Low'].iloc[i], 'type': 'SSL', 'is_swept': False})

    # The filtering logic for is_swing_points=False is commented out in the provided code
    # If it were active, it should build a list of dictionaries and return it.
    # Since it's commented out, we just return the raw 'levels' list which should now contain dictionaries.

    return levels # Return a list of dictionaries


def find_imbalances(data: pd.DataFrame) -> List[Dict[str, Any]]:
    """
    Finds Fair Value Gaps (FVG) or Imbalances in the data.
    Returns a list of dictionaries describing the imbalances.
    """
    imbalances = []
    if data is None or data.empty or len(data) < 3:
        return imbalances

    for i in range(1, len(data) - 1):
        candle_i_minus_1 = data.iloc[i-1]
        candle_i = data.iloc[i]
        candle_i_plus_1 = data.iloc[i+1]

        # Bullish FVG: Low[i+1] < High[i-1]
        if candle_i_plus_1['Low'] < candle_i_minus_1['High']:
            fvg_high = candle_i_minus_1['High']
            fvg_low = candle_i_plus_1['Low']
            # Check if candle i (the middle candle) does not fill the gap
            if not (candle_i['Low'] <= fvg_high and candle_i['High'] >= fvg_low):
                 imbalances.append({
                    'type': 'Bullish',
                    'start_time': candle_i_minus_1.name,
                    'end_time': candle_i_plus_1.name,
                    'start_price': fvg_high,
                    'end_price': fvg_low,
                    'gap_high': fvg_high,
                    'gap_low': fvg_low
                 })

        # Bearish FVG: High[i+1] > Low[i-1]
        if candle_i_plus_1['High'] > candle_i_minus_1['Low']:
             fvg_low = candle_i_minus_1['Low']
             fvg_high = candle_i_plus_1['High']
             # Check if candle i (the middle candle) does not fill the gap
             if not (candle_i['Low'] <= fvg_high and candle_i['High'] >= fvg_low):
                  imbalances.append({
                     'type': 'Bearish',
                     'start_time': candle_i_minus_1.name,
                     'end_time': candle_i_plus_1.name,
                     'start_price': fvg_low,
                     'end_price': fvg_high,
                     'gap_low': fvg_low,
                     'gap_high': fvg_high
                  })

    return imbalances

# Note: The find_structure_break function is NOT present in this file.