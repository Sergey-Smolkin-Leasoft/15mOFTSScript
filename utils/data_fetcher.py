# utils/data_fetcher.py

import pandas as pd
import time
from twelvedata import TDClient
# Import config
from config import (
    TWELVEDATA_API_KEY, TIMEFRAMES, TD_OUTPUT_SIZES,
    TD_BACKTEST_OUTPUT_SIZE_H1, TD_BACKTEST_OUTPUT_SIZE_M15,
    LIQUIDITY_LOOKBACK # Possibly needed, but not directly in fetcher
)
from datetime import datetime, timedelta

# Задержка между запросами при загрузке по частям (в секундах)
# Установите это значение, чтобы избежать превышения лимитов API.
# Может потребоваться увеличить, если видите ошибки лимитов.
CHUNK_FETCH_DELAY_SECONDS = 1.0

# Инициализация клиента Twelve Data
def get_twelvedata_client():
    """Инициализирует и возвращает клиента Twelve Data."""
    try:
        # Передаем apikey при инициализации
        td = TDClient(apikey=TWELVEDATA_API_KEY)
        return td
    except Exception as e:
        print(f"Ошибка инициализации клиента Twelve Data: {e}")
        return None

# Helper to get the correct chunk size based on timeframe
def get_backtest_chunk_size(timeframe_name: str):
    """Returns the appropriate chunk size for backtesting based on timeframe."""
    if timeframe_name == 'M15':
        return TD_BACKTEST_OUTPUT_SIZE_M15
    elif timeframe_name == 'H1':
        return TD_BACKTEST_OUTPUT_SIZE_H1
    else:
        # Fallback or handle unsupported timeframes for backtesting chunking
        print(f"Предупреждение: Неизвестный таймфрейм '{timeframe_name}' для определения размера порции бэктеста. Использование размера M15 ({TD_BACKTEST_OUTPUT_SIZE_M15}).")
        return TD_BACKTEST_OUTPUT_SIZE_M15 # Default to M15 size or raise error

# --- MODIFIED Function for fetching historical range with chunking by shifting end_date ---
def fetch_historical_data_range(asset: str, timeframe_name: str, start_date_str: str, end_date_str: str):
    """
    Получает исторические данные для актива в заданном диапазоне дат, используя загрузку по частям,
    сдвигая конечную дату запроса назад во времени.
    asset: Символ актива (например, 'EUR/USD')
    timeframe_name: Название таймфрейма из config (например, 'M15')
    start_date_str: Начальная дата в формате ГГГГ-ММ-ДД ЧЧ:ММ:СС (UTC)
    end_date_str: Конечная дата в формате ГГГГ-ММ-ДД ЧЧ:ММ:СС (UTC)
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
    # Начинаем загрузку с конца периода
    current_end_date_str = end_date_str
    total_fetched = 0

    print(f"Начало загрузки исторических данных для {asset} ({timeframe_interval}) с {start_date_str} по {end_date_str} по частям (размер порции: {chunk_size})...")

    # Преобразуем строки дат диапазона в Timestamps для сравнения
    start_ts = pd.to_datetime(start_date_str)
    end_ts = pd.to_datetime(end_date_str)

    while True:
        try:
            # Запрос данных, заканчивающихся на current_end_date_str, в количестве chunk_size
            # API Twelve Data по умолчанию возвращает данные в DESC порядке (от новейших к старым)
            print(f"  Запрос порции до {current_end_date_str}...")
            ts = td.time_series(
                symbol=asset,
                interval=timeframe_interval,
                # Используем общую начальную дату как ограничение, но основной сдвиг происходит через end_date
                start_date=start_date_str, # Может помочь API ограничить поиск
                end_date=current_end_date_str,
                outputsize=chunk_size,
                # order='desc' is default
            ).as_pandas()

            if ts is None or ts.empty:
                print(f"  Получена пустая порция данных до {current_end_date_str}. Загрузка, вероятно, завершена или нет данных в диапазоне.")
                break # Данные закончились или что-то пошло не так

            # Twelve Data возвращает данные в обратном хронологическом порядке (самые новые сверху)
            # Сохраняем порцию как есть
            all_data_chunks.append(ts.copy()) # Добавляем копию DataFrame порции
            fetched_count = len(ts)
            total_fetched += fetched_count
            print(f"  Получено {fetched_count} свечей в этой порции. Всего получено: {total_fetched}.")

            # Находим самый старый Timestamp в полученной порции
            oldest_timestamp_in_chunk = ts.index.min()
            # Находим самый новый Timestamp в полученной порции (должен быть близок к current_end_date_str)
            newest_timestamp_in_chunk = ts.index.max()

            # Условие выхода:
            # 1. Если самая старая свеча в порции раньше или равна общей начальной дате бэктеста.
            # 2. Если количество полученных свечей меньше запрошенного размера (значит, достигнут начало истории в диапазоне).
            if oldest_timestamp_in_chunk <= start_ts or fetched_count < chunk_size:
                 print("  Достигнута или пройдена начальная дата диапазона или получена неполная порция. Загрузка завершена.")
                 break

            # Определяем новую конечную дату для следующего запроса:
            # Это Timestamp самой старой свечи в текущей порции минус минимальный интервал времени.
            # Вычитаем 1 секунду, чтобы гарантировать, что мы получим свечу, предшествующую самой старой в текущей порции.
            # Убедимся, что не уходим раньше start_ts
            next_end_ts = oldest_timestamp_in_chunk - timedelta(seconds=1)
            if next_end_ts < start_ts:
                 next_end_ts = start_ts # Не уходим раньше общего старта

            current_end_date_str = next_end_ts.strftime('%Y-%m-%d %H:%M:%S')

            # Пауза для соблюдения лимитов API
            time.sleep(CHUNK_FETCH_DELAY_SECONDS)

        except Exception as e:
            print(f"  Ошибка при получении порции данных до {current_end_date_str}: {e}")
            # В случае ошибки можно добавить логику повтора или просто прервать
            print("  Произошла ошибка при загрузке порции. Прерывание загрузки.")
            break

    if not all_data_chunks:
        print("Не удалось загрузить ни одной порции данных за указанный диапазон.")
        return None

    # Объединяем все полученные порции данных в один DataFrame
    # Поскольку мы запрашивали назад во времени, самый старый кусок будет последним в списке,
    # а самый новый - первым. Concat по умолчанию сохраняет порядок списка.
    combined_data = pd.concat(all_data_chunks)

    # Twelve Data может вернуть дубликаты на границах порций. Удаляем дубликаты по индексу (времени).
    combined_data = combined_data[~combined_data.index.duplicated(keep='first')]

    # Сортируем данные в хронологическом порядке (от старых к новым)
    combined_data = combined_data.sort_index(ascending=True)


    # Twelve Data возвращает колонки с маленькой буквы, переименуем их
    combined_data.rename(columns={
         'open': 'Open',
         'high': 'High',
         'low': 'Low',
         'close': 'Close',
         'volume': 'Volume',
    }, inplace=True)

    # Убедимся, что индекс является DatetimeIndex и удаляем строки с NaN
    if not isinstance(combined_data.index, pd.DatetimeIndex):
         combined_data.index = pd.to_datetime(combined_data.index)

    combined_data.dropna(inplace=True)

    # Обрезаем данные точно по запрошенному диапазону дат,
    # т.к. загрузка по частям может вернуть данные немного за пределами start_date_str
    print(f"Всего загружено и объединено {len(combined_data)} уникальных свечей перед обрезкой.")
    final_data = combined_data.loc[(combined_data.index >= start_ts) & (combined_data.index <= end_ts)].copy()

    print(f"Данные обрезаны до диапазона {start_date_str} - {end_date_str}. Итоговое количество свечей: {len(final_data)}.")

    if final_data.empty:
        print("После обрезки по диапазону дат данные отсутствуют.")
        return None

    return final_data


# The original fetch_data function (keep it for main.py live trading)
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
    output_size = TD_OUTPUT_SIZES.get(timeframe_name) # Use live output sizes here


    if timeframe_interval is None or output_size is None:
        print(f"Ошибка конфигурации для таймфрейма '{timeframe_name}'.")
        return None

    try:
        # Запрос исторических данных
        print(f"Запрос последних {output_size} свечей для {asset} ({timeframe_interval})...")
        ts = td.time_series(
            symbol=asset,
            interval=timeframe_interval,
            outputsize=output_size
            # order='desc' is default
        ).as_pandas()

        if ts is None or ts.empty:
            print(f"Не удалось получить данные для {asset} на интервале {timeframe_interval} (outputsize={output_size})")
            return None

        # Twelve Data возвращает данные в обратном хронологическом порядке (самые новые сверху)
        # Для стратегии обычно нужны данные в прямом порядке (самые старые сверху)
        data = ts.iloc[::-1].copy() # Переворачиваем DataFrame и создаем копию

        # Rename columns
        data.rename(columns={
             'open': 'Open',
             'high': 'High',
             'low': 'Low',
             'close': 'Close',
             'volume': 'Volume',
        }, inplace=True)

        # Ensure DatetimeIndex
        if not isinstance(data.index, pd.DatetimeIndex):
             data.index = pd.to_datetime(data.index)

        data.dropna(inplace=True)
        print(f"Получено {len(data)} свечей для {asset} ({timeframe_interval}).")
        return data

    except Exception as e:
        print(f"Ошибка при получении данных для {asset} ({timeframe_interval}): {e}")
        # Twelve Data API может возвращать ошибки, их нужно обрабатывать
        # (например, лимиты запросов, неверный ключ, неверный символ)
        return None

# Example Usage (Optional, for testing data fetchers)
if __name__ == '__main__':
    print("Тестирование data_fetcher...")
    # Убедитесь, что в config.py заданы:
    # TWELVEDATA_API_KEY
    # ASSETS = ["EUR/USD", "GBP/USD"] # Или другие активы
    # TIMEFRAMES = {"H1": "1h", "M15": "15min"} # Или другие таймфреймы
    # TD_OUTPUT_SIZES = {"H1": ..., "M15": ...} # Размеры для лайв режима
    # BACKTESTING_ASSET = "GBP/USD" # Или другой актив
    # BACKTESTING_START_DATE = "2023-01-01 00:00:00" # Диапазон бэктеста
    # BACKTESTING_END_DATE = "2023-01-31 23:59:59"
    # TD_BACKTEST_OUTPUT_SIZE_H1 = 5000 # Максимум по тарифу
    # TD_BACKTEST_OUTPUT_SIZE_M15 = 5000 # Максимум по тарифу


    try:
        from config import (
            ASSETS, TIMEFRAMES, TWELVEDATA_API_KEY,
            BACKTESTING_ASSET, BACKTESTING_START_DATE, BACKTESTING_END_DATE,
            TD_BACKTEST_OUTPUT_SIZE_H1, TD_BACKTEST_OUTPUT_SIZE_M15
        )

        print("\n--- Тестирование fetch_data (последние N свечей) ---")
        # Тестируем только первый актив и таймфрейм из конфига для примера
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
