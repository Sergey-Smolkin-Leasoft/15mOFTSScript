# tests/conftest.py

import pytest
import pandas as pd
from datetime import datetime, timedelta
import sys
import os

# --- Добавляем корневую директорию проекта в sys.path ---
# Это позволяет тестам в папке 'tests/' импортировать модули из 'strategy/' и 'utils/'
current_dir = os.path.dirname(os.path.abspath(__file__)) # Получаем путь к текущей папке (tests/)
project_root = os.path.dirname(current_dir)           # Получаем путь к родительской папке (корню проекта)
sys.path.insert(0, project_root) # Добавляем корень проекта в начало sys.path
# --------------------------------------------------------


# Теперь можно импортировать модули из корневой папки и ее подпапок
# Импорты из strategy и config теперь должны работать в тестовых файлах
from config import LIQUIDITY_LOOKBACK, IMBALANCE_THRESHOLD, MIN_RR, LONDON_OPEN_HOUR, LONDON_CLOSE_HOUR
from strategy.indicators import find_liquidity_levels # Импорт индикаторов для анализа в фикстурах (опционально, для отладки)


# --- Фикстуры с тестовыми данными ---

@pytest.fixture
def sample_data_m15_buy_setup():
    """
    Фиктивные M15 данные, настроенные для генерации BUY сигнала:
    Нисходящий тренд -> Четкий SSL -> Снятие SSL последней свечой -> Бычья реакция (Order Flow)
    -> Бычий FVG после свипа -> Тест FVG -> Цель с RR >= 1.5
    """
    dates = [datetime(2023, 10, 27, 7, 0) + timedelta(minutes=15*i) for i in range(30)] # 30 свечей

    # Создаем данные, явно формирующие нужные паттерны
    data = pd.DataFrame({
        'Open':  [], 'High':  [], 'Low':   [], 'Close': [], 'Volume': []
    })

    # Нисходящий тренд, формирующий SSL (например, на индексе 10-14)
    downtrend_data = {
        'Open':  [1.0100, 1.0090, 1.0080, 1.0070, 1.0060, 1.0050, 1.0040, 1.0030, 1.0020, 1.0015,
                  1.0010, 1.0008, 1.0005, 1.0003, 1.0000], # Low[14] = 1.0000 (Potential SSL)
        'High':  [1.0105, 1.0095, 1.0085, 1.0075, 1.0065, 1.0055, 1.0045, 1.0035, 1.0025, 1.0020,
                  1.0015, 1.0012, 1.0010, 1.0008, 1.0005],
        'Low':   [1.0090, 1.0080, 1.0070, 1.0060, 1.0050, 1.0040, 1.0030, 1.0020, 1.0010, 1.0005, # Low[9] = 1.0005 (Potential SSL)
                  1.0000, 0.9998, 0.9995, 0.9992, 0.9990], # Low[14] = 0.9990 (More pronounced SSL)
        'Close': [1.0090, 1.0080, 1.0070, 1.0060, 1.0050, 1.0040, 1.0030, 1.0020, 1.0010, 1.0015,
                  1.0005, 0.9998, 0.9995, 0.9992, 0.9990], # Close[14] = 0.9990
        'Volume': [100] * 15
    }
    data = pd.concat([data, pd.DataFrame(downtrend_data, index=dates[:15])])

    # Снятие SSL (последней свечой) и начало реакции
    # Low[14] = 0.9990. Свип должен быть ниже этого уровня.
    sweep_react_data = {
        'Open':  [0.9990, 0.9995, 1.0005, 1.0015, 1.0025], # Открытие последней свечи ниже/на SSL
        'High':  [0.9995, 1.0000, 1.0010, 1.0020, 1.0030],
        'Low':   [0.9985, 0.9990, 1.0000, 1.0010, 1.0020], # Low[0] = 0.9985 (Свип SSL)
        'Close': [0.9990, 1.0005, 1.0015, 1.0025, 1.0030], # Рост после свипа
        'Volume': [100] * 5
    }
    data = pd.concat([data, pd.DataFrame(sweep_react_data, index=dates[15:20])])

    # Формирование Order Flow и FVG
    # Ищем пробой локального хая (например, на 1.0030) и формирование бычьего FVG
    of_fvg_data = {
        'Open':  [1.0030, 1.0020, 1.0035, 1.0030, 1.0045], # Open[1] = 1.0020, High[3] = 1.0040 (Low[1]=1.0020 > High[3]=1.0040 ? No FVG)
                                                        # Let's force a Bullish FVG: Low[i-1] > High[i+1]
                                                        # FVG around index 22: Low[21] > High[23]
        'High':  [1.0035, 1.0025, 1.0040, 1.0040, 1.0050], # High[23] = 1.0040
        'Low':   [1.0025, 1.0015, 1.0030, 1.0035, 1.0040], # Low[21] = 1.0015. Is 1.0015 > 1.0040? No.
                                                        # Let's try index 21 as i-1, 22 as i, 23 as i+1
                                                        # Need Low[21] > High[23]
                                                        # New data:
        'Open':  [1.0030, 1.0030, 1.0020, 1.0035, 1.0030, 1.0045],
        'High':  [1.0035, 1.0035, 1.0025, 1.0040, 1.0035, 1.0050],
        'Low':   [1.0025, 1.0025, 1.0015, 1.0030, 1.0030, 1.0040], # Low[21]=1.0015
        'Close': [1.0030, 1.0020, 1.0035, 1.0030, 1.0045, 1.0050],
        'Volume': [100] * 6
    } # Try again to force FVG: Low[i-1] > High[i+1] -> Low[20]>High[22]. Idx 20 is last from prev block.
      # Need new data starting from index 20.
    of_fvg_data = {
        'Open':  [1.0030, 1.0030, 1.0020, 1.0035, 1.0030, 1.0045],
        'High':  [1.0035, 1.0035, 1.0025, 1.0040, 1.0035, 1.0050],
        'Low':   [1.0025, 1.0025, 1.0015, 1.0030, 1.0030, 1.0040], # Low[22]=1.0015
        'Close': [1.0030, 1.0020, 1.0035, 1.0030, 1.0045, 1.0050],
        'Volume': [100] * 6
    }
    # Let's make FVG between index 21 and 23 (around index 22). Low[21] > High[23].
    of_fvg_data = {
        'Open':  [1.0030, 1.0040, 1.0020, 1.0035, 1.0030, 1.0045], # Idx 20-25
        'High':  [1.0035, 1.0045, 1.0025, 1.0040, 1.0035, 1.0050], # High[23]=1.0040
        'Low':   [1.0025, 1.0030, 1.0015, 1.0030, 1.0030, 1.0040], # Low[21]=1.0030 -> Low[21]=1.0030 > High[23]=1.0040 ? No
                                                                # Need Low[21] > High[23]. Make High[23] low.
        'Open':  [1.0030, 1.0040, 1.0020, 1.0035, 1.0030, 1.0045], # Idx 20-25
        'High':  [1.0035, 1.0045, 1.0025, 1.0040, 1.0035, 1.0050], # High[23]=1.0032
        'Low':   [1.0025, 1.0030, 1.0015, 1.0030, 1.0030, 1.0040], # Low[21]=1.0030
        'Close': [1.0030, 1.0020, 1.0035, 1.0030, 1.0045, 1.0050],
        'Volume': [100] * 6
    } # Still not working with simple numbers. Let's use the previous calculation which worked: Low[20](1.0040) > High[22](1.0038) FVG @ idx 21.

    # FVG data construction from previous thought process:
    fvg_test_data = {
        'Open':  [1.0040, 1.0030, 1.0035, 1.0040, 1.0035, 1.0040, 1.0050, 1.0060, 1.0070, 1.0080], # Idx 20-29
        'High':  [1.0045, 1.0035, 1.0040, 1.0045, 1.0038, 1.0040, 1.0055, 1.0065, 1.0075, 1.0085], # High[22]=1.0038
        'Low':   [1.0035, 1.0025, 1.0030, 1.0035, 1.0030, 1.0035, 1.0045, 1.0055, 1.0065, 1.0075], # Low[20]=1.0035 --> Oh, previous data was wrong. Let's use the one that had Low[20]=1.0040
        'Open':  [1.0040, 1.0030, 1.0035, 1.0040, 1.0035, 1.0040, 1.0050, 1.0060, 1.0070, 1.0080], # Idx 20-29
        'High':  [1.0045, 1.0035, 1.0040, 1.0045, 1.0038, 1.0040, 1.0055, 1.0065, 1.0075, 1.0085], # High[22]=1.0038
        'Low':   [1.0035, 1.0025, 1.0030, 1.0035, 1.0030, 1.0035, 1.0045, 1.0055, 1.0065, 1.0075], # Low[20]=1.0035. Still wrong.

    } # Need to ensure the data before index 20 creates the correct price for Low[20].
    # Let's assume candles 15-19 end such that Candle 19 (index 19) has Low 1.0040.
    # Previous block ends at index 19 with close 1.0030, low 0.9990.
    # Need to transition from there to a structure where Low[20] > High[22].
    # Let's rebuild the last part (index 15 onwards)
    sweep_react_fvg_tp_data = {
        # Index 15-16: Sweep
        'Open':  [0.9990, 0.9985],
        'High':  [0.9995, 0.9990],
        'Low':   [0.9985, 0.9980], # Low[16]=0.9980 (Sweeps 0.9990)
        'Close': [0.9985, 0.9990], # Index 16 Close=0.9990
        'Volume': [100] * 2
    }
    data = data.iloc[:15] # Keep 0-14
    data = pd.concat([data, pd.DataFrame(sweep_react_fvg_tp_data, index=dates[15:17])])

    # Index 17-19: Reaction, break structure (assume a local high was around 1.0005-1.0010)
    react_data = {
        'Open': [0.9990, 1.0000, 1.0010],
        'High': [1.0005, 1.0015, 1.0025], # High[19]=1.0025 (Assume breaks structure)
        'Low':  [0.9988, 0.9995, 1.0005],
        'Close': [1.0000, 1.0010, 1.0020], # Index 19 Close=1.0020
        'Volume': [100] * 3
    }
    data = pd.concat([data, pd.DataFrame(react_data, index=dates[17:20])])

    # Index 20-22: Form Bullish FVG (Low[20] > High[22]). Around index 21.
    fvg_form_data = {
        'Open': [1.0020, 1.0030, 1.0025], # Index 20-22
        'High': [1.0030, 1.0035, 1.0028], # High[22]=1.0028
        'Low':  [1.0015, 1.0020, 1.0020], # Low[20]=1.0015 --> Still not > 1.0028. Need Low[20] > High[22].
                                       # Try: Low[20]=1.0030, High[22]=1.0028.
        'Open': [1.0020, 1.0030, 1.0025], # Index 20-22
        'High': [1.0035, 1.0035, 1.0028], # High[22]=1.0028
        'Low':  [1.0030, 1.0020, 1.0020], # Low[20]=1.0030 --> Low[20]=1.0030 > High[22]=1.0028. Yes! Bullish FVG between 1.0028 and 1.0030 at index 21.
        'Close': [1.0030, 1.0025, 1.0020], # Index 22 Close=1.0020
        'Volume': [100] * 3
    }
    data = pd.concat([data, pd.DataFrame(fvg_form_data, index=dates[20:23])])

    # Index 23-25: Test FVG
    fvg_test_data = {
        'Open': [1.0020, 1.0028, 1.0029], # Index 23-25
        'High': [1.0025, 1.0030, 1.0031],
        'Low':  [1.0018, 1.0025, 1.0028], # Low[23]=1.0018 enters FVG zone (1.0028-1.0030)
        'Close': [1.0028, 1.0029, 1.0030],
        'Volume': [100] * 3
    }
    data = pd.concat([data, pd.DataFrame(fvg_test_data, index=dates[23:26])])

    # Index 26-29: Move towards TP
    # SL = Low of sweep candle (index 16 Low = 0.9980)
    # Entry = Midpoint of FVG (1.0028 + 1.0030) / 2 = 1.0029
    # Risk = 1.0029 - 0.9980 = 0.0049
    # Need TP >= 1.0029 + 1.5 * 0.0049 = 1.0029 + 0.00735 = 1.01025
    tp_move_data = {
        'Open': [1.0030, 1.0040, 1.0060, 1.0080], # Index 26-29
        'High': [1.0040, 1.0055, 1.0075, 1.0105], # High[29]=1.0105 >= 1.01025 (TP level)
        'Low':  [1.0028, 1.0038, 1.0058, 1.0078],
        'Close': [1.0040, 1.0055, 1.0075, 1.0100], # Index 29 Close = 1.0100
        'Volume': [100] * 4
    }
    data = pd.concat([data, pd.DataFrame(tp_move_data, index=dates[26:30])])


    data.index.name = 'Datetime'
    # print("--- Generated BUY Setup Data ---") # Uncomment for debugging fixture
    # print(data)
    # highs, lows = find_liquidity_levels(data, lookback=LIQUIDITY_LOOKBACK)
    # imbalances = find_imbalances(data, threshold=IMBALANCE_THRESHOLD)
    # print("Highs:", highs)
    # print("Lows:", lows)
    # print("Imbalances:", imbalances)
    # print("Last candle Low:", data['Low'].iloc[-1], "Close:", data['Close'].iloc[-1])
    # print("--------------------------------")
    return data

# Добавь другие фикстуры (sample_data_m30_buy_setup, sample_data_h1_buy_setup, sample_data_m15_no_setup, sample_data_m15_not_enough_data)
# Оставляем их код из предыдущего ответа, они не сломаны.
# ... (код других фикстур) ...

@pytest.fixture
def sample_data_base():
    """
    Базовые фиктивные данные с простым трендом и коррекцией.
    """
    dates = [datetime(2023, 1, 1) + timedelta(minutes=15*i) for i in range(50)]
    data = pd.DataFrame({
        'Open': [1.0000 + i * 0.0002 for i in range(50)],
        'High': [1.0005 + i * 0.0002 + 0.0001 for i in range(50)],
        'Low': [0.9995 + i * 0.0002 - 0.0001 for i in range(50)],
        'Close': [1.0000 + i * 0.0002 for i in range(50)],
        'Volume': 100
    }, index=dates)
    data.iloc[40:45] = data.iloc[40:45] - 0.0010
    data.iloc[45:] = data.iloc[45:] - 0.0005
    data.index.name = 'Datetime'
    return data

@pytest.fixture
def sample_data_m30_buy_setup():
     """Фиктивные M30 данные для BUY setup (например, для теста альтернативного входа)."""
     dates = [datetime(2023, 10, 27, 7, 0) + timedelta(minutes=30*i) for i in range(15)]
     data = pd.DataFrame({
         'Open':  [1.0110, 1.0090, 1.0070, 1.0050, 1.0030, 1.0040, 1.0060, 1.0080, 1.0100, 1.0120, 1.0140, 1.0150, 1.0160, 1.0170, 1.0180],
         'High':  [1.0115, 1.0095, 1.0075, 1.0055, 1.0035, 1.0045, 1.0065, 1.0085, 1.0105, 1.0125, 1.0145, 1.0155, 1.0165, 1.0175, 1.0185],
         'Low':   [1.0100, 1.0080, 1.0060, 1.0040, 1.0020, 1.0030, 1.0050, 1.0070, 0.9990, 1.0110, 1.0130, 1.0140, 1.0150, 1.0160, 1.0170], # Low[8]=0.9990 - можно использовать для M30 теста
         'Close': [1.0090, 1.0070, 1.0050, 1.0030, 1.0025, 1.0040, 1.0060, 1.0080, 1.0100, 1.0120, 1.0140, 1.0150, 1.0160, 1.0170, 1.0180],
         'Volume': [200] * 15
     }, index=dates)
     data.index.name = 'Datetime'
     return data

@pytest.fixture
def sample_data_h1_buy_setup():
     """Фиктивные H1 данные для BUY setup."""
     dates = [datetime(2023, 10, 27, 7, 0) + timedelta(hours=i) for i in range(4)]
     data = pd.DataFrame({
         'Open':  [1.0200, 1.0100, 1.0050, 1.0150],
         'High':  [1.0210, 1.0110, 1.0060, 1.0160],
         'Low':   [1.0100, 1.0050, 1.0040, 1.0140],
         'Close': [1.0100, 1.0050, 1.0150, 1.0155],
         'Volume': [400] * 4
     }, index=dates)
     data.index.name = 'Datetime'
     return data

@pytest.fixture
def sample_data_m15_no_setup():
     """Фиктивные M15 данные, которые НЕ должны вызывать сигнал."""
     dates = [datetime(2023, 10, 27, 7, 0) + timedelta(minutes=15*i) for i in range(30)]
     data = pd.DataFrame({
         'Open':  [1.0100 + 0.0001*i for i in range(30)],
         'High':  [1.0105 + 0.0001*i + 0.00005 for i in range(30)],
         'Low':   [1.0095 + 0.0001*i - 0.00005 for i in range(30)],
         'Close': [1.0100 + 0.0001*i for i in range(30)],
         'Volume': [100] * 30
     }, index=dates) # Просто восходящий тренд без свипов или явных FVG
     data.index.name = 'Datetime'
     return data

@pytest.fixture
def sample_data_m15_not_enough_data():
    """Фиктивные M15 данные с недостаточным количеством свечей."""
    dates = [datetime(2023, 10, 27, 7, 0) + timedelta(minutes=15*i) for i in range(5)] # Меньше чем LIQUIDITY_LOOKBACK*2=20
    data = pd.DataFrame({
        'Open':  [1,2,3,4,5], 'High':  [1.1,2.1,3.1,4.1,5.1], 'Low':   [0.9,1.9,2.9,3.9,4.9], 'Close': [1,2,3,4,5], 'Volume': [100]*5
    }, index=dates)
    data.index.name = 'Datetime'
    return data