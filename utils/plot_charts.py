# utils/plot_charts.py - MODIFIED

import matplotlib.pyplot as plt
import mplfinance as mpf
import pandas as pd
import os
from datetime import datetime
import numpy as np
# Импортируем LIQUIDITY_LOOKBACK из config для определения объема данных для графика
from config import LIQUIDITY_LOOKBACK, TD_OUTPUT_SIZES

# Директория для сохранения графиков
CHARTS_DIR = 'signal_charts'

def ensure_chart_directory():
    """Создает директорию для сохранения графиков, если она не существует."""
    if not os.path.exists(CHARTS_DIR):
        os.makedirs(CHARTS_DIR)
        print(f"Создана директория для графиков: {CHARTS_DIR}")

# --- ИЗМЕНЕНА ПОДПИСЬ ФУНКЦИИ: добавлен swept_level ---
def save_candlestick_chart(data: pd.DataFrame, asset: str, timeframe: str, entry: float | None, sl: float | None, tp: float | None, swept_level: float | None):
# ---------------------------------------------------
    """
    Сохраняет свечной график с уровнями Entry, SL, TP и уровнем снятой ликвидности.
    Включает больше исторических данных.
    """
    if data is None or data.empty:
        print(f"Нет данных для построения графика {asset} {timeframe}.")
        return

    # mplfinance требует колонки Open, High, Low, Close (с большой буквы!) и DatetimeIndex
    # Наши данные из data_fetcher уже в таком формате.

    # --- ИЗМЕНЕНИЕ: Берем данные за последние N свечей, а не только за сегодня ---
    # Используем количество свечей из TD_OUTPUT_SIZES, но можно ограничить, если нужно
    # Например, взять последние LIQUIDITY_LOOKBACK * 4 свечей, чтобы видеть контекст свипа
    num_candles_to_plot = min(len(data), TD_OUTPUT_SIZES.get(timeframe, 500)) # Не больше, чем есть данных, и не больше чем запросили у TD
    num_candles_to_plot = max(num_candles_to_plot, LIQUIDITY_LOOKBACK * 4) # Убедимся, что есть хотя бы минимальный контекст

    plot_data = data.iloc[-num_candles_to_plot:].copy()
    # ----------------------------------------------------------------------------


    if plot_data.empty:
         print(f"Недостаточно данных для построения графика {asset} {timeframe} за последние {num_candles_to_plot} свечей.")
         return

    ensure_chart_directory() # Убедимся, что папка существует

    # --- Подготавливаем данные для горизонтальных линий ---
    hlines_to_draw = []

    # Форматируем уровни для включения в заголовок и подписи к линиям
    format_str = ".5f" if asset in ["GBP/USD", "EUR/USD"] else ".2f"
    levels_title = ""

    if entry is not None:
        hlines_to_draw.append({'y': float(entry), 'color': 'green', 'label': f'Вход ({entry:{format_str}})'})
        levels_title += f" Entry: {entry:{format_str}}"
    if sl is not None:
        hlines_to_draw.append({'y': float(sl), 'color': 'red', 'label': f'SL ({sl:{format_str}})'})
        levels_title += f" SL: {sl:{format_str}}"
    if tp is not None:
        hlines_to_draw.append({'y': float(tp), 'color': 'purple', 'label': f'TP ({tp:{format_str}})'})
        levels_title += f" TP: {tp:{format_str}}"
    # --- ИЗМЕНЕНИЕ: Добавляем уровень снятой ликвидности ---
    if swept_level is not None:
        hlines_to_draw.append({'y': float(swept_level), 'color': 'orange', 'linestyle': '-', 'linewidth': 1, 'label': f'Свип ({swept_level:{format_str}})'})
    # ----------------------------------------------------

    # Создаем строку заголовка
    title_time_start = plot_data.index[0].strftime('%Y-%m-%d %H:%M UTC') # Время начала данных на графике
    title_time_end = plot_data.index[-1].strftime('%Y-%m-%d %H:%M UTC')
    chart_title = f"{asset} - {timeframe} Setup: {title_time_start} - {title_time_end}{levels_title}"


    # Генерируем имя файла
    timestamp_str = datetime.now().strftime('%Y%m%d_%H%M%S')
    safe_asset_name = asset.replace('/', '_').replace('=', '_')
    # Имя файла теперь содержит диапазон дат графика
    filename = f"{safe_asset_name}_{timeframe}_chart_{plot_data.index[0].strftime('%Y%m%d_%H%M')}_to_{plot_data.index[-1].strftime('%Y%m%d_%H%M')}_{timestamp_str}.png"
    filepath = os.path.join(CHARTS_DIR, filename)

    # Параметры для mplfinance
    mc = mpf.make_marketcolors(up='green', down='red', wick='inherit', edge='inherit', volume='in')
    s  = mpf.make_mpf_style(base_mpf_style='yahoo', marketcolors=mc)

    fig = None # Инициализируем fig = None на случай ошибки до создания фигуры

    try:
        # --- Используем mplfinance для построения свечей на осях, возвращаем figure/axes ---
        fig, axes = mpf.plot(plot_data,
                             type='candle',
                             style=s,
                             title=chart_title,
                             ylabel='Цена',
                             figscale=1.5,
                             volume=False,
                             tight_layout=True,
                             returnfig=True
                            )

        # Получаем объект осей, на котором нарисованы свечи
        ax = axes[0] if isinstance(axes, (list, np.ndarray)) else axes

        # --- Вручную добавляем горизонтальные линии с помощью matplotlib ---
        for line_info in hlines_to_draw:
             ax.axhline(line_info['y'], color=line_info['color'], linestyle=line_info.get('linestyle', '--'), linewidth=line_info.get('linewidth', 1.5), label=line_info['label'])

        # Добавляем легенду для hlines
        if hlines_to_draw:
             ax.legend()

        # --- Сохраняем figure вручную ---
        fig.savefig(filepath)
        print(f"График сохранен: {filepath}")

    except Exception as e:
        print(f"Ошибка при сохранении графика {filepath}: {e}")
        print(f"Детали ошибки: {e}")
        print(f"Попытка нарисовать линии: {hlines_to_draw}")
        # Дополнительная информация для отладки диапазона данных
        if data is not None and not data.empty:
             print(f"Диапазон исходных данных: {data.index[0]} - {data.index[-1]}")
             if plot_data is not None and not plot_data.empty:
                  print(f"Диапазон данных для графика: {plot_data.index[0]} - {plot_data.index[-1]}")
             else:
                  print("Данные для графика пустые после фильтрации/среза.")


    finally:
        # --- Всегда закрываем figure вручную ---
        if fig is not None: # Проверяем, был ли объект фигуры успешно создан
             plt.close(fig)
        else:
             print("Объект фигуры не был создан, нет необходимости закрывать.")


# Example Usage (Optional, for testing plot function directly) - Обновите, если нужно
if __name__ == '__main__':
     print("Тестирование сохранения свечных графиков...")
     # Create dummy data covering several days
     dates = pd.to_datetime([f'2023-01-0{d} {h}:{m}:00' for d in range(1, 5) for h in range(24) for m in range(0, 60, 15)]) # 4 дня M15
     # Убедимся, что данных достаточно для lookback
     dates = dates[:min(len(dates), LIQUIDITY_LOOKBACK * 10)] # Ограничим для примера

     dummy_data = pd.DataFrame({
         'Open': np.linspace(1.0000, 1.0500, len(dates)),
         'High': np.linspace(1.0005, 1.0505, len(dates)),
         'Low': np.linspace(0.9995, 1.0495, len(dates)),
         'Close': np.linspace(1.0002, 1.0502, len(dates)),
         'Volume': 100
     }, index=dates)

     # Add some price swings and a potential sweep
     dummy_data.loc[dummy_data.index[50]:dummy_data.index[60], ['High', 'Close']] += 0.01 # Swing up
     dummy_data.loc[dummy_data.index[70]:dummy_data.index[80], ['Low', 'Close']] -= 0.015 # Swing down (potential liquidity)
     sweep_level_test = dummy_data.loc[dummy_data.index[80], 'Low'] # Price of the swing low
     dummy_data.loc[dummy_data.index[81]:dummy_data.index[85], ['Low', 'Close']] = sweep_level_test - 0.001 # Sweep below the low


     dummy_data.index.name = 'Datetime'

     # Test saving chart with levels and swept level
     entry_test = dummy_data.loc[dummy_data.index[90], 'Close'] # Example entry after sweep
     sl_test = sweep_level_test * 0.999 # Example SL below swept level
     tp_test = entry_test + (entry_test - sl_test) * 2 # Example TP with RR=2

     save_candlestick_chart(dummy_data, "TEST/SWEEP", "M15", entry_test, sl_test, tp_test, sweep_level_test)

     # Test saving chart with no levels
     save_candlestick_chart(dummy_data.tail(100), "TEST/NOLEVELS", "M15", None, None, None, None)

     print("Тестирование сохранения свечных графиков завершено.")