# main.py

import time
import pandas as pd
import os
from utils.data_fetcher import fetch_data
from strategy.strategy import generate_signal
# Импортируем функцию сохранения графиков и название папки
from utils.plot_charts import save_candlestick_chart, CHARTS_DIR
# Импортируем обновленные конфиги (они теперь только для M15)
from config import ASSETS, TIMEFRAMES, CHECK_INTERVAL_SECONDS, TD_OUTPUT_SIZES

def clear_chart_directory():
    """Очищает содержимое папки для графиков."""
    folder_path = CHARTS_DIR
    if os.path.exists(folder_path):
        print(f"Очистка папки графиков: {folder_path}")
        for filename in os.listdir(folder_path):
            file_path = os.path.join(folder_path, filename)
            try:
                if os.path.isfile(file_path):
                    os.remove(file_path)
            except Exception as e:
                print(f"Ошибка при удалении файла {file_path}: {e}")
    else:
        print(f"Папка графиков '{folder_path}' не найдена, очистка не требуется.")


def main():
    # --- Очищаем папку с графиками при каждом запуске ---
    clear_chart_directory()
    # ----------------------------------------------------

    print("Запуск сигнального бота 15mOF (только M15, график за сегодня)...")


    while True:
        print("=" * 40)
        current_time = pd.Timestamp.now()
        print(f"Время проверки: {current_time}")
        print("=" * 40)

        for asset in ASSETS:
            timeframe = "M15" # Единственный используемый таймфрей

            print(f"\n--- Анализ актива: {asset} ({timeframe}) ---")

            # Получаем только данные M15
            # TD_OUTPUT_SIZES['M15'] определяет сколько всего свечей получаем (например, 500)
            # Из этих 500 свечей функция save_candlestick_chart выберет только те, что за сегодня
            data_m15 = fetch_data(asset, timeframe)


            # Генерируем сигнал: передаем только data_m15
            signal, entry, stop_loss, take_profit, rr_val, status_sweep, status_of, status_target, comment = generate_signal(
                asset, data_m15
            )

            # Выводим статус каждого из трех главных условий
            print(f"  Условие 1 (Снятие SSL/BSL): {'Выполнено' if status_sweep else 'Не выполнено'}")
            print(f"  Условие 2 (Подтверждение M15 OF и вход по M15 имбалансу): {'Выполнено' if status_of else 'Не выполнено'}")
            print(f"  Условие 3 (Цель с RR >= 1.5): {'Выполнено' if status_target else 'Не выполнено'}")

            print(f"\n  Итоговый сигнал: {signal}")

            # Выводим детали, если сигнал есть
            if signal != 'NONE':
                format_str = ".5f" if asset in ["GBP/USD", "EUR/USD"] else ".2f" # Форматирование все еще может зависеть от актива
                print(f"  Потенциальный вход: {entry:{format_str}}")
                print(f"  Потенциальный Stop Loss: {stop_loss:{format_str}}")
                print(f"  Потенциальный Take Profit: {take_profit:{format_str}}")
                print(f"  Reward/Risk: {rr_val:.2f}")

            # --- Сохраняем только M15 график за сегодня В ЛЮБОМ СЛУЧАЕ ---
            # Функция save_candlestick_chart теперь сама определяет диапазон "сегодня"
            print(f"  Сохранение графика {timeframe} за сегодня для {asset} (даже если нет сигнала)...")

            # Передаем только M15 данные. num_candles больше не нужен в вызове.
            if data_m15 is not None and not data_m15.empty:
                 # Передаем уровни entry, sl, tp, которые могут быть None
                 # --- УБРАН num_candles из вызова ---
                 save_candlestick_chart(data_m15, asset, timeframe, entry, stop_loss, take_profit)
                 # ---------------------------------
            else:
                  print(f"  Не удалось сохранить график {timeframe} для {asset}: нет данных.")

            print(f"  Комментарий стратегии: {comment}")
            print("-" * (len(f"Анализ актива: {asset}") + len(timeframe) + 5))


        # Проверка завершена по всем активным активам (сейчас только GBP/USD)
        print(f"\nПроверка по всем активам завершена.")
        print(f"Следующая проверка через {CHECK_INTERVAL_SECONDS // 60} минут. Графики сохранены в папке '{CHARTS_DIR}/'")
        time.sleep(CHECK_INTERVAL_SECONDS)

if __name__ == "__main__":
    main()