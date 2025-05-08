# main.py - MODIFIED

import time
import pandas as pd
import os
from utils.data_fetcher import fetch_data
# generate_signal теперь возвращает h1_context
from strategy.strategy import generate_signal, determine_h1_context
# Импортируем функцию сохранения графиков и название папки
from utils.plot_charts import save_candlestick_chart, CHARTS_DIR
# Импортируем обновленные конфиги
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
    clear_chart_directory()
    print("Запуск сигнального бота 15mOF с фильтрацией по H1 контексту...")

    while True:
        print("=" * 40)
        current_time = pd.Timestamp.now()
        print(f"Время проверки: {current_time}")
        print("=" * 40)

        for asset in ASSETS:
            # --- Получаем данные H1 для определения контекста ---
            print(f"\n--- Анализ {asset} (H1) для определения контекста ---")
            data_h1 = fetch_data(asset, "H1")

            if data_h1 is None or data_h1.empty:
                 print(f"  Не удалось получить данные H1 для {asset}. Пропускаем анализ этого актива.")
                 continue

            # --- Сохраняем H1 график ---
            print(f"  Сохранение графика H1 для {asset}...")
            # Для H1 графика не передаем уровни M15 сетапа
            # Можно передать уровни H1 свипа, если determine_h1_context их возвращает
            # Пока сохраняем просто свечи
            save_candlestick_chart(data_h1, asset, "H1", None, None, None, None)
            print("  График H1 сохранен.")
            # --------------------------

            # --- Получаем данные M15 для поиска сетапа ---
            timeframe_m15 = "M15"
            print(f"\n--- Анализ {asset} ({timeframe_m15}) для поиска сетапа ---")
            data_m15 = fetch_data(asset, timeframe_m15)

            if data_m15 is None or data_m15.empty:
                 print(f"  Не удалось получить данные M15 для {asset}. Пропускаем поиск сетапа.")
                 continue


            # Генерируем сигнал: передаем данные H1 и M15
            # generate_signal теперь возвращает h1_context
            signal, entry, stop_loss, take_profit, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context = generate_signal(
                asset, data_h1, data_m15
            )

            # Выводим определенный H1 контекст (он уже определен в generate_signal)
            print(f"\n  Определенный H1 контекст: {h1_context}")


            # Выводим статус условий на M15
            print(f"  Условие M15 1 (Снятие SSL/BSL): {'Выполнено' if status_sweep_m15 else 'Не выполнено'}")
            print(f"  Условие M15 2 (2 ступени OF и вход по M15 FVG): {'Выполнено' if status_of_m15 else 'Не выполнено'}")
            print(f"  Условие M15 3 (Цель с RR >= 1.5): {'Выполнено' if status_target_m15 else 'Не выполнено'}")


            print(f"\n  Итоговый сигнал: {signal}")

            # Выводим детали, если сигнал есть
            if signal != 'NONE':
                format_str = ".5f" if asset in ["GBP/USD", "EUR/USD"] else ".2f"
                print(f"  Потенциальный вход: {entry:{format_str}}")
                print(f"  Потенциальный Stop Loss: {stop_loss:{format_str}}")
                print(f"  Потенциальный Take Profit: {take_profit:{format_str}}")
                print(f"  Reward/Risk: {rr_val:.2f}")
                if swept_level_m15 is not None:
                    print(f"  Уровень снятой ликвидности M15: {swept_level_m15:{format_str}}")


            # --- Сохраняем M15 график с уровнями (уже было) ---
            print(f"  Сохранение графика {timeframe_m15} для {asset}...")

            if data_m15 is not None and not data_m15.empty:
                 # Передаем уровни entry, sl, tp, swept_level_m15 (могут быть None)
                 save_candlestick_chart(data_m15, asset, timeframe_m15, entry, stop_loss, take_profit, swept_level_m15)
            else:
                  print(f"  Не удалось сохранить график {timeframe_m15} для {asset}: нет данных.")

            print(f"  Комментарий стратегии: {comment}")
            print("-" * (len(f"Анализ актива: {asset}") + len(timeframe_m15) + 5))


        print(f"\nПроверка по всем активам завершена.")
        print(f"Следующая проверка через {CHECK_INTERVAL_SECONDS // 60} минут. Графики сохранены в папке '{CHARTS_DIR}/'")
        time.sleep(CHECK_INTERVAL_SECONDS)

if __name__ == "__main__":
    main()