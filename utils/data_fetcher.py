# utils/data_fetcher.py

import pandas as pd
import time
from twelvedata import TDClient
from config import TWELVEDATA_API_KEY, TIMEFRAMES, TD_OUTPUT_SIZES

# Инициализация клиента Twelve Data
# Используем контекстный менеджер 'with' для автоматического закрытия соединения
def get_twelvedata_client():
    try:
        td = TDClient(apikey=TWELVEDATA_API_KEY)
        return td
    except Exception as e:
        print(f"Ошибка инициализации клиента Twelve Data: {e}")
        return None

def fetch_data(asset: str, timeframe_name: str):
    """
    Получает исторические данные для актива по Twelve Data.
    asset: Символ актива (например, 'EUR/USD')
    timeframe_name: Название таймфрейма из config (например, 'M15')
    """
    td = get_twelvedata_client()
    if td is None:
        return None

    timeframe_interval = TIMEFRAMES.get(timeframe_name)
    output_size = TD_OUTPUT_SIZES.get(timeframe_name)

    if timeframe_interval is None or output_size is None:
        print(f"Ошибка конфигурации для таймфрейма {timeframe_name}")
        return None

    try:
        # Запрос исторических данных
        ts = td.time_series(
            symbol=asset,
            interval=timeframe_interval,
            outputsize=output_size
        ).as_pandas() # Получаем данные сразу в виде pandas DataFrame

        if ts.empty:
            print(f"Не удалось получить данные для {asset} на интервале {timeframe_interval} (outputsize={output_size})")
            return None

        # Twelve Data возвращает данные в обратном хронологическом порядке (самые новые сверху)
        # Для стратегии обычно нужны данные в прямом порядке (самые старые сверху)
        data = ts.iloc[::-1] # Переворачиваем DataFrame

        # Twelve Data возвращает колонки с маленькой буквы, переименуем их
        data.rename(columns={
            'open': 'Open',
            'high': 'high', # Ошибка в api twelvedata или документации? Нет, просто надо быть внимательным
            'low': 'Low',   # Должны быть с маленькой буквы по их примеру, но документация может врать
            'close': 'Close',
            'volume': 'Volume',
        }, inplace=True)

        # Исправляем переименование на точное соответствие
        data.rename(columns={
             'open': 'Open',
             'high': 'High',
             'low': 'Low',
             'close': 'Close',
             'volume': 'Volume',
        }, inplace=True)


        # Убедимся, что индекс является DatetimeIndex
        if not isinstance(data.index, pd.DatetimeIndex):
             # Twelve Data обычно возвращает DatetimeIndex, но на всякий случай
             data.index = pd.to_datetime(data.index)


        data.dropna(inplace=True) # Удаляем строки с NaN
        return data

    except Exception as e:
        print(f"Ошибка при получении данных для {asset} ({timeframe_interval}): {e}")
        # Twelve Data API может возвращать ошибки, их нужно обрабатывать
        # (например, лимиты запросов, неверный ключ, неверный символ)
        return None

# Пример использования (можно удалить в финальной версии)
if __name__ == '__main__':
    print("Тестирование data_fetcher с Twelve Data...")
    # Используем API ключ из config
    config = {
        "TWELVEDATA_API_KEY": TWELVEDATA_API_KEY,
        "ASSETS": ["EUR/USD", "GBP/USD", "XAU/USD"],
        "TIMEFRAMES": {"H1": "1h", "M15": "15min"},
        "TD_OUTPUT_SIZES": {"H1": 100, "M15": 100}
    } # Переопределяем config для чистого теста

    for asset in config["ASSETS"]:
        for tf_name in config["TIMEFRAMES"].keys():
            print(f"Получение {asset} - {tf_name}...")
            data = fetch_data(asset, tf_name)
            if data is not None:
                print(f"Получено {len(data)} свечей.")
                print(data.head()) # Смотрим начало (самые старые)
                print(data.tail()) # Смотрим конец (самые новые)
            print("-" * 20)

    print("Тестирование завершено.")