# utils/data_fetcher.py

import pandas as pd
import time
from twelvedata import TDClient
from config import (
    TWELVEDATA_API_KEY, TIMEFRAMES, TD_OUTPUT_SIZES,
    TD_BACKTEST_OUTPUT_SIZE_H1, TD_BACKTEST_OUTPUT_SIZE_M15,
    LIQUIDITY_LOOKBACK
)
from datetime import datetime, timedelta

CHUNK_FETCH_DELAY_SECONDS = 1.0

def get_twelvedata_client():
    """Инициализирует и возвращает клиента Twelve Data."""
    try:
        td = TDClient(apikey=TWELVEDATA_API_KEY)
        return td
    except Exception as e:
        print(f"Ошибка инициализации клиента Twelve Data: {e}")
        return None

def get_backtest_chunk_size(timeframe_name: str):
    """Returns the appropriate chunk size for backtesting based on timeframe."""
    if timeframe_name == 'M15':
        return TD_BACKTEST_OUTPUT_SIZE_M15
    elif timeframe_name == 'H1':
        return TD_BACKTEST_OUTPUT_SIZE_H1
    else:
        print(f"Предупреждение: Неизвестный таймфрейм '{timeframe_name}' для определения размера порции бэктеста. Использование размера M15 ({TD_BACKTEST_OUTPUT_SIZE_M15}).")
        return TD_BACKTEST_OUTPUT_SIZE_M15

def fetch_historical_data_range(asset: str, timeframe_name: str, start_date_str: str, end_date_str: str):
    """
    Получает исторические данные для актива в заданном диапазоне дат, используя загрузку по частям,
    сдвигая конечную дату запроса назад во времени.
    asset: Символ актива (например, 'EUR/USD')
    timeframe_name: Название таймфрейма из config (например, 'M15')
    start_date_str: Начальная дата в формате YYYY-MM-DD HH:MM:SS (UTC)
    end_date_str: Конечная дата в формате YYYY-MM-DD HH:MM:SS (UTC)
    """
    td = get_twelvedata_client()
    if td is None:
        return None

    timeframe_interval = TIMEFRAMES.get(timeframe_name)
    chunk_size = get_backtest_chunk_size(timeframe_name)

    if timeframe_interval is None:
        print(f"Ошибка конфигурации интервала для таймфрейма '{timeframe_name}'.")
        return None

    all_data_chunks = []
    current_end_date_str = end_date_str
    total_fetched = 0

    print(f"Начало загрузки исторических данных для {asset} ({timeframe_interval}) с {start_date_str} по {end_date_str} по частям (размер порции: {chunk_size})...")

    start_ts = pd.to_datetime(start_date_str)
    end_ts = pd.to_datetime(end_date_str)

    while True:
        try:
            print(f"  Запрос порции до {current_end_date_str}...")
            ts = td.time_series(
                symbol=asset,
                interval=timeframe_interval,
                start_date=start_date_str,
                end_date=current_end_date_str,
                outputsize=chunk_size,
            ).as_pandas()

            if ts is None or ts.empty:
                print(f"  Получена пустая порция данных до {current_end_date_str}. Загрузка, вероятно, завершена или нет данных в диапазоне.")
                break

            all_data_chunks.append(ts.copy())
            fetched_count = len(ts)
            total_fetched += fetched_count
            print(f"  Получено {fetched_count} свечей в этой порции. Всего получено: {total_fetched}.")

            oldest_timestamp_in_chunk = ts.index.min()
            newest_timestamp_in_chunk = ts.index.max()


            if oldest_timestamp_in_chunk <= start_ts or fetched_count < chunk_size:
                 print("  Достигнута или пройдена начальная дата диапазона или получена неполная порция. Загрузка завершена.")
                 break

            next_end_ts = oldest_timestamp_in_chunk - timedelta(seconds=1)
            if next_end_ts < start_ts:
                 next_end_ts = start_ts

            current_end_date_str = next_end_ts.strftime('%Y-%m-%d %H:%M:%S')

            time.sleep(CHUNK_FETCH_DELAY_SECONDS)

        except Exception as e:
            print(f"  Ошибка при получении порции данных до {current_end_date_str}: {e}")
            print("  Произошла ошибка при загрузке порции. Прерывание загрузки.")
            break

    if not all_data_chunks:
        print("Не удалось загрузить ни одной порции данных за указанный диапазон.")
        return None

    combined_data = pd.concat(all_data_chunks)

    combined_data = combined_data[~combined_data.index.duplicated(keep='first')]

    combined_data = combined_data.sort_index(ascending=True)

    combined_data.rename(columns={
         'open': 'Open',
         'high': 'High',
         'low': 'Low',
         'close': 'Close',
         'volume': 'Volume',
    }, inplace=True)

    if not isinstance(combined_data.index, pd.DatetimeIndex):
         combined_data.index = pd.to_datetime(combined_data.index)

    combined_data.dropna(inplace=True)

    print(f"Всего загружено и объединено {len(combined_data)} уникальных свечей перед обрезкой.")
    final_data = combined_data.loc[(combined_data.index >= start_ts) & (combined_data.index <= end_ts)].copy()

    print(f"Данные обрезаны до диапазона {start_date_str} - {end_date_str}. Итоговое количество свечей: {len(final_data)}.")

    if final_data.empty:
        print("После обрезки по диапазону дат данные отсутствуют.")
        return None

    return final_data

def fetch_data(asset: str, timeframe_name: str):
    """
    Получает исторические данные для актива по Twelve Data (последние N свечей).
    asset: Символ актива (например, 'EUR/USD')
    timeframe_name: Название таймфрейма из config (например, 'M15')
    """
    td = get_twelvedata_client()
    if td is None:
        return None

    timeframe_interval = TIMEFRAMES.get(timeframe_name)
    output_size = TD_OUTPUT_SIZES.get(timeframe_name)


    if timeframe_interval is None or output_size is None:
        print(f"Ошибка конфигурации для таймфрейма '{timeframe_name}'.")
        return None

    try:
        print(f"Запрос последних {output_size} свечей для {asset} ({timeframe_interval})...")
        ts = td.time_series(
            symbol=asset,
            interval=timeframe_interval,
            outputsize=output_size
        ).as_pandas()

        if ts is None or ts.empty:
            print(f"Не удалось получить данные для {asset} на интервале {timeframe_interval} (outputsize={output_size})")
            return None

        data = ts.iloc[::-1].copy()

        data.rename(columns={
             'open': 'Open',
             'high': 'High',
             'low': 'Low',
             'close': 'Close',
             'volume': 'Volume',
        }, inplace=True)

        if not isinstance(data.index, pd.DatetimeIndex):
             data.index = pd.to_datetime(data.index)

        data.dropna(inplace=True)
        print(f"Получено {len(data)} свечей для {asset} ({timeframe_interval}).")
        return data

    except Exception as e:
        print(f"Ошибка при получении данных для {asset} ({timeframe_interval}): {e}")
        return None

if __name__ == '__main__':
    print("Тестирование data_fetcher...")
    try:
        from config import (
            ASSETS, TIMEFRAMES, TWELVEDATA_API_KEY,
            BACKTESTING_ASSET, BACKTESTING_START_DATE, BACKTESTING_END_DATE,
            TD_BACKTEST_OUTPUT_SIZE_H1, TD_BACKTEST_OUTPUT_SIZE_M15
        )

        print("\n--- Тестирование fetch_data (последние N свечей) ---")
        if ASSETS and TIMEFRAMES:
            test_asset_live = ASSETS[0]
            test_tf_live = list(TIMEFRAMES.keys())[0]
            print(f"Получение {test_asset_live} - {test_tf_live} (последние свечи)...")
            data_live = fetch_data(test_asset_live, test_tf_live)
            if data_live is not None:
                print(f"Получено {len(data_live)} свечей.")
                if not data_live.empty:
                    print(data_live.head())
                    print(data_live.tail())
            print("-" * 20)
        else:
             print("ASSETS или TIMEFRAMES не заданы в config.py для тестирования fetch_data.")


        print("\n--- Тестирование fetch_historical_data_range (диапазон дат с разбивкой) ---")
        if BACKTESTING_ASSET and BACKTESTING_START_DATE and BACKTESTING_END_DATE:
            test_asset_hist = BACKTESTING_ASSET
            test_start_date_hist = BACKTESTING_START_DATE
            test_end_date_hist = BACKTESTING_END_DATE

            print(f"Загрузка H1 данных для диапазона {test_start_date_hist} - {test_end_date_hist}...")
            data_h1_hist = fetch_historical_data_range(test_asset_hist, "H1", test_start_date_hist, test_end_date_hist)
            if data_h1_hist is not None:
                print(f"Получено {len(data_h1_hist)} свечей H1 в диапазоне.")
                if not data_h1_hist.empty:
                    print(data_h1_hist.head())
                    print(data_h1_hist.tail())
            print("-" * 20)

            print(f"Загрузка M15 данных для диапазона {test_start_date_hist} - {test_end_date_hist}...")
            data_m15_hist = fetch_historical_data_range(test_asset_hist, "M15", test_start_date_hist, test_end_date_hist)
            if data_m15_hist is not None:
                 print(f"Получено {len(data_m15_hist)} свечей M15 в диапазоне.")
                 if not data_m15_hist.empty:
                    print(data_m15_hist.head())
                    print(data_m15_hist.tail())
            print("-" * 20)
        else:
             print("Параметры бэктестинга в config.py не заданы для тестирования fetch_historical_data_range.")


    except ImportError:
        print("Не удалось импортировать параметры из config.py. Убедитесь, что config.py существует и содержит необходимые константы.")
    except Exception as e:
         print(f"Произошла ошибка при тестировании data_fetcher: {e}")

    print("Тестирование завершено.")