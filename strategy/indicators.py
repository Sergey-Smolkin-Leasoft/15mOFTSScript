# strategy/indicators.py

import pandas as pd
import numpy as np

def find_liquidity_levels(data: pd.DataFrame, lookback: int):
    """
    Определяет потенциальные уровни ликвидности (Swing High/Low)
    с использованием простого фрактального подхода.
    """
    highs = data['High']
    lows = data['Low']
    n = lookback // 2

    # Определяем Swing High: свеча является максимальной в окне lookback
    is_swing_high = highs.rolling(window=lookback, center=True).max() == highs
    # Определяем Swing Low: свеча является минимальной в окне lookback
    is_swing_low = lows.rolling(window=lookback, center=True).min() == lows

    # Фильтруем, оставляя только True значения и соответствующие индексы
    swing_highs = data.index[is_swing_high].tolist()
    swing_lows = data.index[is_swing_low].tolist()

    # Очистка: удаляем крайние точки, где lookback не может быть применен полностью
    swing_highs = [idx for idx in swing_highs if idx >= data.index[n] and idx <= data.index[-n-1]]
    swing_lows = [idx for idx in swing_lows if idx >= data.index[n] and idx <= data.index[-n-1]]


    # Возвращаем индексы и соответствующие цены
    high_levels = [(idx, data['High'][idx]) for idx in swing_highs]
    low_levels = [(idx, data['Low'][idx]) for idx in swing_lows]

    return high_levels, low_levels

def find_imbalances(data: pd.DataFrame, threshold: float):
    """
    Находит имбалансы (Fair Value Gaps - FVG).
    Возвращает список кортежей (индекс_первой_свечи_fvg, верхняя_граница_fvg, нижняя_граница_fvg, тип).
    Тип: 'bullish' (цена ниже), 'bearish' (цена выше).
    """
    imbalances = []
    for i in range(1, len(data) - 1):
        # Bullish FVG: High[i-1] < Low[i+1] -> Gap between Low[i-1] and High[i+1]
        # Bearish FVG: Low[i-1] > High[i+1] -> Gap between High[i-1] and Low[i+1]

        # Bullish FVG (Low of current > High of previous) with gap to the next candle low
        # This definition might vary. Let's use the standard ICT definition:
        # Bullish FVG: The gap between the low of candle i-1 and the high of candle i+1
        if data['High'].iloc[i-1] < data['Low'].iloc[i+1]:
             # Gap is between data['Low'].iloc[i-1] and data['High'].iloc[i+1]
             # Let's use the gap between candle i-1 and i+1 wicks around candle i
             lower_bound = data['Low'].iloc[i-1]
             upper_bound = data['High'].iloc[i+1]
             if upper_bound - lower_bound > threshold:
                 imbalances.append((data.index[i-1], lower_bound, upper_bound, 'bearish')) # Bearish FVG = price expected to go DOWN to fill it


        # Bearish FVG: The gap between the high of candle i-1 and the low of candle i+1
        if data['Low'].iloc[i-1] > data['High'].iloc[i+1]:
            # Gap is between data['High'].iloc[i-1] and data['Low'].iloc[i+1]
            lower_bound = data['Low'].iloc[i+1]
            upper_bound = data['High'].iloc[i-1]
            if upper_bound - lower_bound > threshold:
                 imbalances.append((data.index[i-1], lower_bound, upper_bound, 'bullish')) # Bullish FVG = price expected to go UP to fill it


    # Let's refine FVG definition based on common use:
    # Bullish FVG: Between Low[i-1] and High[i+1]
    # Bearish FVG: Between High[i-1] and Low[i+1]

    imbalances_refined = []
    for i in range(1, len(data) - 1):
        # Bullish FVG: Low[i-1] > High[i+1] (Gap below candle i)
        if data['Low'].iloc[i-1] > data['High'].iloc[i+1]:
            lower_bound = data['High'].iloc[i+1] # Lower end of the gap
            upper_bound = data['Low'].iloc[i-1] # Upper end of the gap
            if upper_bound - lower_bound > threshold:
                 imbalances_refined.append((data.index[i-1], lower_bound, upper_bound, 'bullish'))

        # Bearish FVG: High[i-1] < Low[i+1] (Gap above candle i)
        if data['High'].iloc[i-1] < data['Low'].iloc[i+1]:
            lower_bound = data['High'].iloc[i-1] # Lower end of the gap
            upper_bound = data['Low'].iloc[i+1] # Upper end of the gap
            if upper_bound - lower_bound > threshold:
                 imbalances_refined.append((data.index[i-1], lower_bound, upper_bound, 'bearish'))


    # Let's use the refined definition
    return imbalances_refined

# Пример использования (можно удалить в финальной версии)
if __name__ == '__main__':
    print("Тестирование indicators...")
    # Создаем фиктивные данные
    data = pd.DataFrame({
        'Open': [10, 11, 12, 13, 12, 11, 10, 9, 8, 9, 10, 11],
        'High': [12, 13, 14, 15, 13, 12, 11, 10, 9, 10, 12, 13],
        'Low': [9, 10, 11, 12, 11, 10, 9, 8, 7, 8, 9, 10],
        'Close': [11, 12, 13, 12, 11, 10, 9, 8, 9, 10, 11, 12],
        'Volume': 100
    }, index=pd.to_datetime(['2023-01-01 00:00', '2023-01-01 00:15', '2023-01-01 00:30', '2023-01-01 00:45',
                             '2023-01-01 01:00', '2023-01-01 01:15', '2023-01-01 01:30', '2023-01-01 01:45',
                             '2023-01-01 02:00', '2023-01-01 02:15', '2023-01-01 02:30', '2023-01-01 02:45']))

    # Найдем ликвидность
    high_levels, low_levels = find_liquidity_levels(data, lookback=5)
    print("Swing Highs:", high_levels)
    print("Swing Lows:", low_levels)

    # Найдем имбалансы
    imbalances = find_imbalances(data, threshold=0.01)
    print("Imbalances (FVG):", imbalances)

    print("Тестирование завершено.")