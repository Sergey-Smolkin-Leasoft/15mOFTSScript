# utils/plot_charts.py - MODIFIED

import matplotlib.pyplot as plt
import mplfinance as mpf
import pandas as pd
import os
from datetime import datetime
import numpy as np

# Директория для сохранения графиков
CHARTS_DIR = 'signal_charts'

def ensure_chart_directory():
    """Создает директорию для сохранения графиков, если она не существует."""
    if not os.path.exists(CHARTS_DIR):
        os.makedirs(CHARTS_DIR)
        print(f"Создана директория для графиков: {CHARTS_DIR}")

# --- ИЗМЕНЕНА ПОДПИСЬ ФУНКЦИИ: УБРАН num_candles ---
def save_candlestick_chart(data: pd.DataFrame, asset: str, timeframe: str, entry: float | None, sl: float | None, tp: float | None):
# ---------------------------------------------------
    """
    Сохраняет свечной график за текущий день (с 00:00 UTC) с уровнями Entry, SL, TP.
    Использует mplfinance для свечей и matplotlib для горизонтальных линий.

    Args:
        data: DataFrame с данными цены (обязательно с DatetimeIndex и колонками Open, High, Low, Close).
              Предполагается, что индекс данных в UTC или является наивным UTC.
        asset: Название актива (для заголовка и имени файла).
        timeframe: Таймфрейм (для заголовка и имени файла).
        entry: Уровень входа (может быть None).
        sl: Уровень Stop Loss (может быть None).
        tp: Уровень Take Profit (может быть None).
        # num_candles больше не используется для определения диапазона
    """
    if data is None or data.empty:
        print(f"Нет данных для построения графика {asset} {timeframe}.")
        return

    # mplfinance требует колонки Open, High, Low, Close (с большой буквы!) и DatetimeIndex
    # Наши данные из data_fetcher уже в таком формате.

    # --- ИЗМЕНЕНИЕ: Берем данные только за сегодняшний день (на основе даты последней свечи) ---
    # Получаем дату последней свечи
    try:
        last_ts = data.index[-1]
        # Создаем временную метку на начало дня (00:00:00) для этой даты
        start_of_day_ts = pd.Timestamp(last_ts.date())
        # Если ваш индекс timezone-aware UTC, раскомментируйте следующую строку:
        # start_of_day_ts = pd.Timestamp(last_ts.date(), tz='UTC')
        # Если ваш индекс в другой TZ, нужна конвертация:
        # start_of_day_ts = pd.Timestamp(last_ts.date(), tz='YourTimeZone').tz_convert('UTC') # Пример конвертации в UTC


    except IndexError:
         print(f"Ошибка: Индекс данных пуст для {asset} {timeframe}.")
         return # Нет данных для получения даты

    # Фильтруем данные, оставляя только свечи с начала дня и до конца имеющихся данных
    plot_data = data[data.index >= start_of_day_ts].copy()
    # ----------------------------------------------------------------------------


    if plot_data.empty:
         # Это может произойти, если самая первая свеча данных уже позже начала дня
         # или если в данных всего пара свечей, но их DateTime не попадает в логику фильтрации
         print(f"Недостаточно данных за сегодня ({start_of_day_ts.strftime('%Y-%m-%d')}) для построения графика {asset} {timeframe}.")
         # Можно добавить логику, чтобы в этом случае сохранялось хотя бы несколько последних свечей
         # Но пока просто пропускаем сохранение графика за сегодня, если данных мало.
         return

    ensure_chart_directory() # Убедимся, что папка существует

    # --- Подготавливаем данные для горизонтальных линий для РУЧНОГО рисования ---
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

    # -------------------------------------------------------------------------

    # Создаем строку заголовка
    title_time_end = plot_data.index[-1].strftime('%Y-%m-%d %H:%M UTC')
    title_time_start = plot_data.index[0].strftime('%H:%M UTC') # Добавим время начала данных на графике
    chart_title = f"{asset} - {timeframe} Setup: {title_time_start} - {title_time_end}{levels_title}"


    # Генерируем имя файла
    timestamp_str = datetime.now().strftime('%Y%m%d_%H%M%S')
    safe_asset_name = asset.replace('/', '_').replace('=', '_')
    # Имя файла теперь содержит дату графика
    filename = f"{safe_asset_name}_{timeframe}_day_{plot_data.index[-1].strftime('%Y%m%d')}_{timestamp_str}.png"
    filepath = os.path.join(CHARTS_DIR, filename)

    # Параметры для mplfinance (без параметра hlines)
    mc = mpf.make_marketcolors(up='green', down='red', wick='inherit', edge='inherit', volume='in')
    s  = mpf.make_mpf_style(base_mpf_style='yahoo', marketcolors=mc)


    try:
        # --- Используем mplfinance для построения свечей на осях, но ВОЗВРАЩАЕМ figure/axes ---
        fig, axes = mpf.plot(plot_data,
                             type='candle',           # Тип графика: свечи
                             style=s,                 # Стиль графика
                             title=chart_title,       # Заголовок
                             ylabel='Цена',           # Подпись оси Y
                             figscale=1.5,            # Масштаб фигуры
                             volume=False,            # График объема (False отключает его под основным)
                             tight_layout=True,       # Корректировка макета
                             returnfig=True           # <-- ВАЖНО: Возвращаем figure и axes
                             # savefig и closefig не указываем
                            )

        # Получаем объект осей, на котором нарисованы свечи
        # Если volume=False, mplfinance возвращает один объект axes. Если volume=True, то список.
        ax = axes[0] if isinstance(axes, (list, np.ndarray)) else axes

        # --- Вручную добавляем горизонтальные линии с помощью matplotlib после рисования свечей ---
        for line_info in hlines_to_draw:
             ax.axhline(line_info['y'], color=line_info['color'], linestyle='--', linewidth=1.5, label=line_info['label'])

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
             try:
                last_ts_debug = data.index[-1]
                start_of_day_ts_debug = pd.Timestamp(last_ts_debug.date())
                print(f"Начало дня последней свечи: {start_of_day_ts_debug}")
                plot_data_debug = data[data.index >= start_of_day_ts_debug].copy()
                print(f"Количество свечей за сегодня после фильтрации: {len(plot_data_debug)}")
             except Exception as e_debug:
                print(f"Ошибка при отладке диапазона данных: {e_debug}")


        # ---------------------------
    finally:
        # --- Всегда закрываем figure вручную ---
        if 'fig' in locals() and fig is not None:
             plt.close(fig)


# Example Usage (Optional, for testing plot function directly)
if __name__ == '__main__':
     print("Тестирование сохранения свечных графиков за сегодня...")
     # Create dummy data covering parts of two days
     dates1 = pd.to_datetime([f'2023-01-01 23:{i:02}:00' for i in range(30)])
     dates2 = pd.to_datetime([f'2023-01-02 00:{i:02}:00' for i in range(100)])
     dates3 = pd.to_datetime([f'2023-01-02 10:{i:02}:00' for i in range(50)])
     # Using pd.concat for modern pandas versions
     dates = pd.concat([pd.Series(dates1), pd.Series(dates2), pd.Series(dates3)]).index


     dummy_data = pd.DataFrame({
         'Open': np.linspace(100, 120, len(dates)),
         'High': np.linspace(101, 121, len(dates)),
         'Low': np.linspace(99, 119, len(dates)),
         'Close': np.linspace(100.5, 120.5, len(dates)),
         'Volume': 100
     }, index=dates)
     # Simple price pattern
     dummy_data['Close'] = dummy_data['Open'] + np.sin(np.linspace(0, 20, len(dates))) * 5
     dummy_data['High'] = dummy_data[['Open', 'Close']].max(axis=1) + 1
     dummy_data['Low'] = dummy_data[['Open', 'Close']].min(axis=1) - 1
     dummy_data.index.name = 'Datetime'

     # Test saving chart for the second day (Jan 02)
     save_candlestick_chart(dummy_data, "TEST/DAY", "M15", 110, 105, 118)
     # Test saving chart when levels are None
     save_candlestick_chart(dummy_data.tail(50), "TEST/DAY", "M15", None, None, None)
     # Test saving chart with minimal data (should plot only the last few candles if they are the only ones today)
     save_candlestick_chart(dummy_data.tail(5), "TEST/MIN", "M15", 119, 117, 121)
     # Test with data that ends right at midnight (should get all data for that day)
     save_candlestick_chart(dummy_data.loc[:'2023-01-02 00:59:00'], "TEST/MIDNIGHT", "M15", 105, 104, 106)

     print("Тестирование сохранения свечных графиков за сегодня завершено.")