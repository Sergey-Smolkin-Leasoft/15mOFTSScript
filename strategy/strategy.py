# strategy/strategy.py

import pandas as pd
import numpy as np
from datetime import timedelta
from config import LIQUIDITY_LOOKBACK, MIN_RR, TIMEFRAMES, TIMEFRAME_DURATIONS_MINUTES
from strategy.indicators import find_liquidity_levels, find_imbalances

# Символы для вывода
CHECK = "✓"
CROSS = "✗"
ARROW = "→"

def determine_h1_context(data_h1: pd.DataFrame) -> str:
    """
    Определяет рыночный контекст (бычий, медвежий, нейтральный) на таймфрейме H1.
    Основано на недавнем снятии ликвидности и пробое структуры.
    Выводит подробный статус проверки в консоль.
    """
    print(f"  [{ARROW}] Определение H1 контекста:")
    if data_h1 is None or data_h1.empty:
        print(f"    [{CROSS}] Недостаточно данных H1 (DataFrame пуст). Контекст: Нейтральный.")
        return 'NEUTRAL'

    min_h1_required = LIQUIDITY_LOOKBACK * 2
    if len(data_h1) < min_h1_required:
        print(f"    [{CROSS}] Недостаточно данных H1 ({len(data_h1)} свечей). Требуется минимум {min_h1_required}. Контекст: Нейтральный.")
        return 'NEUTRAL'

    h1_liquidity = find_liquidity_levels(data_h1, LIQUIDITY_LOOKBACK)
    print(f"    [{ARROW}] H1 Ликвидность (обнаруженные SH/SL): {h1_liquidity}")

    recent_sweeps = []
    if h1_liquidity:
        # Проверяем весь доступный срез данных H1 на наличие свипов
        # Свип должен произойти после формирования уровня ликвидности
        for i in range(len(data_h1)):
            candle = data_h1.iloc[i]
            # Ищем уровни, которые были сформированы ДО текущей свечи
            relevant_levels = [level for level in h1_liquidity if level['time'] < candle.name]

            for level in relevant_levels:
                if level['type'] == 'BSL' and candle['High'] > level['price']:
                    recent_sweeps.append({'type': 'BSL', 'time': candle.name, 'swept_level_time': level['time'], 'price': level['price']})
                    print(f"    [{ARROW}] H1 Свип BSL найден на {candle.name}, уровень {level['price']:.5f} (изначальный уровень на {level['time']})")
                elif level['type'] == 'SSL' and candle['Low'] < level['price']:
                     recent_sweeps.append({'type': 'SSL', 'time': candle.name, 'swept_level_time': level['time'], 'price': level['price']})
                     print(f"    [{ARROW}] H1 Свип SSL найден на {candle.name}, уровень {level['price']:.5f} (изначальный уровень на {level['time']})")

    # Нас интересуют только самые недавние свипы для определения контекста
    # Возьмем, например, свипы за последние несколько свечей H1, или просто последний свип
    # Для простоты, давайте сфокусируемся на ПОСЛЕДНЕМ релевантном свипе в срезе
    last_relevant_sweep = None
    if recent_sweeps:
        # Сортируем свипы по времени убывания, чтобы найти самый последний
        recent_sweeps.sort(key=lambda x: x['time'], reverse=True)
        last_relevant_sweep = recent_sweeps[0]
        print(f"    [{ARROW}] Последний релевантный H1 свип в срезе: {last_relevant_sweep['type']} на {last_relevant_sweep['time']}")


    if last_relevant_sweep is None:
         print(f"    [{CROSS}] Нет недавних свипов на H1 в пределах окна данных. Контекст: Нейтральный.")
         return 'NEUTRAL'

    # Теперь ищем пробой структуры ПОСЛЕ последнего релевантного свипа
    print(f"    [{ARROW}] Анализ BOS после последнего свипа ({last_relevant_sweep['time']})...")
    data_after_last_sweep = data_h1.loc[data_h1.index > last_relevant_sweep['time']].copy()

    context_determined = 'NEUTRAL'

    if not data_after_last_sweep.empty:
         # Ищем пробой ближайшего соответствующего Swing Point перед свипом
         # Находим Swing Points ДО свечи последнего свипа
         data_before_last_sweep = data_h1.loc[data_h1.index < last_relevant_sweep['time']].copy()
         if data_before_last_sweep.empty:
              print(f"      [{CROSS}] Нет данных H1 до последнего свипа {last_relevant_sweep['time']} для поиска SP.")
              print(f"    [{CROSS}] Пробой структуры после свипа не найден. Контекст: Нейтральный.")
              return 'NEUTRAL'

         h1_swing_points_before_last_sweep = find_liquidity_levels(data_before_last_sweep, LIQUIDITY_LOOKBACK, is_swing_points=True)
         print(f"      [{ARROW}] H1 Swing Points до последнего свипа ({LIQUIDITY_LOOKBACK} lookback): {h1_swing_points_before_last_sweep}")

         if last_relevant_sweep['type'] == 'BSL': # Ищем медвежий контекст после свипа верхов
            bearish_bos_found = False
            h1_swing_lows_before_last_sweep = sorted([sp for sp in h1_swing_points_before_last_sweep if sp['type'] == 'SSL'], key=lambda x: x['time'], reverse=True)

            for sl_level in h1_swing_lows_before_last_sweep:
                 if data_after_last_sweep['Low'].min() < sl_level['price']:
                      bearish_bos_found = True
                      print(f"      [{CHECK}] H1 Медвежий пробой структуры найден после свипа BSL {last_relevant_sweep['time']}. Пробит SL на {sl_level['price']:.5f} (время SP: {sl_level['time']}).")
                      break

            if bearish_bos_found:
                context_determined = 'BEARISH'

         elif last_relevant_sweep['type'] == 'SSL': # Ищем бычий контекст после свипа низов
            bullish_bos_found = False
            h1_swing_highs_before_last_sweep = sorted([sp for sp in h1_swing_points_before_last_sweep if sp['type'] == 'BSL'], key=lambda x: x['time'], reverse=True)

            for sh_level in h1_swing_highs_before_last_sweep:
                 if data_after_last_sweep['High'].max() > sh_level['price']:
                      bullish_bos_found = True
                      print(f"      [{CHECK}] H1 Бычий пробой структуры найден после свипа SSL {last_relevant_sweep['time']}. Пробит SH на {sh_level['price']:.5f} (время SP: {sh_level['time']}).")
                      break

            if bullish_bos_found:
                context_determined = 'BULLISH'
            else:
                print(f"      [{CROSS}] Нет данных H1 после последнего свипа {last_relevant_sweep['time']} для поиска BOS.")


    if context_determined == 'NEUTRAL':
        print(f"    [{CROSS}] Пробой структуры после свипа не найден. Контекст: Нейтральный.")
    else:
        print(f"    [{CHECK}] Контекст H1 определен как: {context_determined}.")

    return context_determined


# Вспомогательная функция для проверки соответствия свипа H1 контексту (используется в determine_h1_context)
# Эта функция больше не нужна с новой логикой determine_h1_context
# def determine_h1_context_direction(sweep_type: str, h1_context_from_param: str) -> bool:
#     """Helper to check if sweep type matches the context being looked for."""
#     if sweep_type == 'BSL' and h1_context_from_param == 'BEARISH':
#         return True
#     if sweep_type == 'SSL' and h1_context_from_param == 'BULLISH':
#         return True
#     return False

def check_order_flow(data_m15: pd.DataFrame, direction: str, sweep_candle_time: pd.Timestamp, swept_level: dict) -> tuple[str, str]:
    """
    Проверяет формирование Order Flow (ОФ) на M15 после снятия ликвидности.
    Выводит подробный статус проверки в консоль.
    """
    print(f"  [{ARROW}] Проверка Order Flow на M15 ({direction}) после свипа на {sweep_candle_time}:")

    if data_m15 is None or data_m15.empty:
        print("    [{CROSS}] Недостаточно данных M15 (DataFrame пуст) для проверки ОФ.")
        return 'NONE', "Недостаточно данных M15"

    # 1. Поиск пробоя структуры (CHoCH) после свечи свипа
    print(f"    [{ARROW}] 1я ступень ОФ: Поиск CHoCH после {sweep_candle_time}...")
    m15_lookback_sp = LIQUIDITY_LOOKBACK // 2
    data_before_sweep_candle = data_m15.loc[data_m15.index < sweep_candle_time].copy()

    min_m15_for_sp = m15_lookback_sp * 2 + 1
    if len(data_before_sweep_candle) < min_m15_for_sp:
        print(f"      [{CROSS}] Недостаточно данных M15 перед свипом ({len(data_before_sweep_candle)} свечей). Требуется минимум {min_m15_for_sp} для поиска SP. CHoCH не найден.")
        return 'NONE', "Недостаточно данных M15 перед свипом для CHoCH"

    m15_swing_points_before = find_liquidity_levels(data_before_sweep_candle, lookback=m15_lookback_sp, is_swing_points=True)
    print(f"      [{ARROW}] M15 Swing Points перед свипом ({m15_lookback_sp} lookback): {m15_swing_points_before}")

    structure_point_to_break = None
    if direction == 'BUY':
        sh_points = sorted([sp for sp in m15_swing_points_before if sp['type'] == 'BSL'], key=lambda x: x['time'], reverse=True)
        if sh_points:
            structure_point_to_break = sh_points[0]
            print(f"      [{ARROW}] Бычий CHoCH: Цель для пробоя (ближайший SH) на {structure_point_to_break['price']:.5f} (время {structure_point_to_break['time']})")
        else:
             print("      [{CROSS}] Бычий CHoCH: Не найдено Swing High перед свипом.")
             pass

    elif direction == 'SELL':
        sl_points = sorted([sp for sp in m15_swing_points_before if sp['type'] == 'SSL'], key=lambda x: x['time'], reverse=True)
        if sl_points:
            structure_point_to_break = sl_points[0]
            print(f"      [{ARROW}] Медвежий CHoCH: Цель для пробоя (ближайший SL) на {structure_point_to_break['price']:.5f} (время {structure_point_to_break['time']})")
        else:
             print("      [{CROSS}] Медвежий CHoCH: Не найдено Swing Low перед свипом.")
             pass


    if structure_point_to_break is None:
        print(f"    [{CROSS}] 1я ступень ОФ: Цель для пробоя структуры не найдена. CHoCH не найден.")
        return 'NONE', "1я ступень ОФ: Не найдена цель для пробоя"

    data_after_sweep_to_current = data_m15.loc[data_m15.index > sweep_candle_time].copy()
    if data_after_sweep_to_current.empty:
         print("      [{CROSS}] Нет данных M15 после свечи свипа для поиска CHoCH.")
         return 'NONE', "Нет данных M15 после свипа для CHoCH"


    choch_found = False
    choch_candle_time = None

    if direction == 'BUY':
        break_candidates = data_after_sweep_to_current[data_after_sweep_to_current['High'] > structure_point_to_break['price']]
        if not break_candidates.empty:
            choch_found = True
            choch_candle_time = break_candidates.index[0]
            print(f"    [{CHECK}] 1я ступень ОФ (Бычий CHoCH): Найден пробой вверх на свече {choch_candle_time}")

    elif direction == 'SELL':
        break_candidates = data_after_sweep_to_current[data_after_sweep_to_current['Low'] < structure_point_to_break['price']]
        if not break_candidates.empty:
            choch_found = True
            choch_candle_time = break_candidates.index[0]
            print(f"    [{CHECK}] 1я ступень ОФ (Медвежий CHoCH): Найден пробой вниз на свече {choch_candle_time}")


    if not choch_found:
        print(f"    [{CROSS}] 1я ступень ОФ: Пробой структуры (CHoCH) не найден после свипа.")
        return 'NONE', "1я ступень ОФ: CHoCH не найден"

    # 2. Поиск Imbalance (FVG) ПОСЛЕ CHoCH и проверка его теста
    print(f"    [{ARROW}] 2я ступень ОФ: Поиск FVG после CHoCH на {choch_candle_time}...")
    data_after_choch_to_current = data_m15.loc[data_m15.index > choch_candle_time].copy()
    if data_after_choch_to_current.empty:
         print("      [{CROSS}] Нет данных M15 после свечи CHoCH для поиска FVG.")
         return 'NONE', "Нет данных M15 после CHoCH"

    imbalances = find_imbalances(data_after_choch_to_current)
    print(f"      [{ARROW}] Найдены FVG после CHoCH: {imbalances}")

    relevant_imbalance = None
    if direction == 'BUY':
        bullish_fugs = sorted([imb for imb in imbalances if imb['type'] == 'Bullish'], key=lambda x: x['start_time'])
        bullish_fugs = [imb for imb in bullish_fugs if imb['start_time'] > choch_candle_time]
        if bullish_fugs:
            relevant_imbalance = bullish_fugs[0]
            print(f"      [{CHECK}] 2я ступень ОФ (Бычий FVG): Найден ближайший FVG на {relevant_imbalance['start_time']} - {relevant_imbalance['end_time']}")
        else:
             print("      [{CROSS}] 2я ступень ОФ (Бычий FVG): Не найден бычий FVG после CHoCH.")
             pass

    elif direction == 'SELL':
        bearish_fugs = sorted([imb for imb in imbalances if imb['type'] == 'Bearish'], key=lambda x: x['start_time'])
        bearish_fugs = [imb for imb in bearish_fugs if imb['start_time'] > choch_candle_time]

        if bearish_fugs:
            relevant_imbalance = bearish_fugs[0]
            print(f"      [{CHECK}] 2я ступень ОФ (Медвежий FVG): Найден ближайший FVG на {relevant_imbalance['start_time']} - {relevant_imbalance['end_time']}")
        else:
            print("      [{CROSS}] 2я ступень ОФ (Медвежий FVG): Не найден медвежий FVG после CHoCH.")
            pass


    if relevant_imbalance is None:
        print(f"    [{CROSS}] 2я ступень ОФ: Соответствующий FVG не найден после CHoCH.")
        return 'NONE', "2я ступень ОФ: FVG не найден"

    print(f"    [{ARROW}] 2я ступень ОФ: Проверка теста FVG {relevant_imbalance['start_time']} - {relevant_imbalance['end_time']}...")
    data_after_fvg_end_to_current = data_m15.loc[data_m15.index > relevant_imbalance['end_time']].copy()
    if data_after_fvg_end_to_current.empty:
         print("      [{CROSS}] Нет данных M15 после FVG для проверки теста.")
         return 'NONE', "Нет данных M15 после FVG для теста"

    fvg_tested = False
    if direction == 'BUY':
        fvg_high = relevant_imbalance['start_price']
        fvg_low = relevant_imbalance['end_price']

        test_candidates = data_after_fvg_end_to_current[(data_after_fvg_end_to_current['Low'] <= fvg_high) & (data_after_fvg_end_to_current['High'] >= fvg_low)]
        if not test_candidates.empty:
            fvg_tested = True
            print(f"      [{CHECK}] 2я ступень ОФ (Бычий FVG тест): Найден возврат в FVG на свече {test_candidates.index[0]}")

    elif direction == 'SELL':
        fvg_low = relevant_imbalance['start_price']
        fvg_high = relevant_imbalance['end_price']

        test_candidates = data_after_fvg_end_to_current[(data_after_fvg_end_to_current['Low'] <= fvg_high) & (data_after_fvg_end_to_current['High'] >= fvg_low)]
        if not test_candidates.empty:
            fvg_tested = True
            print(f"      [{CHECK}] 2я ступень ОФ (Медвежий FVG тест): Найден возврат в FVG на свече {test_candidates.index[0]}")


    if fvg_tested:
        print(f"    [{CHECK}] Order Flow ({direction}) подтвержден (CHoCH + FVG тест).")
        return 'CONFIRMED', "ОФ подтвержден (CHoCH + FVG тест)"
    else:
        print(f"    [{CROSS}] 2я ступень ОФ: Тест FVG не найден.")
        return 'NONE', "2я ступень ОФ: Тест FVG не найден"


def calculate_rr(entry_price, stop_loss_price, take_profit_price, direction, asset):
    """
    Рассчитывает отношение Risk/Reward.
    """
    print(f"  [{ARROW}] Расчет Risk/Reward:")
    if entry_price is None or stop_loss_price is None or take_profit_price is None:
        print(f"    [{CROSS}] Недостаточно данных для расчета RR.")
        return 0.0

    if direction == 'BUY':
        risk = entry_price - stop_loss_price
        reward = take_profit_price - entry_price
    elif direction == 'SELL':
        risk = stop_loss_price - entry_price
        reward = entry_price - take_profit_price
    else:
        print(f"    [{CROSS}] Неизвестное направление: {direction}")
        return 0.0

    if risk is None or risk <= 0:
        print(f"    [{CROSS}] Риск <= 0 или None ({risk}). Деление на ноль невозможно.")
        return 0.0

    rr = reward / risk
    print(f"    [{ARROW}] Entry={entry_price:.5f}, SL={stop_loss_price:.5f}, TP={take_profit_price:.5f}. Риск={abs(risk):.5f}, Reward={abs(reward):.5f}. RR={rr:.2f}")

    return rr


def is_london_session_active(current_time: pd.Timestamp) -> bool:
    """
    Проверяет, активна ли Лондонская сессия в заданное время (UTC).
    Лондонская сессия: 08:00 - 17:00 UTC (может варьироваться с переходом на летнее время)
    """
    london_open_hour = 8
    london_close_hour = 17

    current_hour_utc = current_time.hour

    is_active = london_open_hour <= current_hour_utc < london_close_hour

    return is_active


def generate_signal(asset: str, data_h1: pd.DataFrame, data_m15: pd.DataFrame):
    """
    Анализирует данные и генерирует торговый сигнал на M15 на основе контекста H1
    и подтверждения Order Flow на M15.
    Выводит подробный статус проверки в консоль.
    """
    print(f"\n  [{ARROW}] Запуск генерации сигнала на {data_m15.index[-1]}...")

    signal = 'NONE'
    entry = None
    sl = None
    tp = None
    rr_val = None
    comment = "Нет условий"
    status_sweep_m15 = False
    status_of_m15 = 'NONE'
    status_target_m15 = False
    swept_level_m15 = None
    h1_context = 'NEUTRAL'


    min_h1_for_context_needed = LIQUIDITY_LOOKBACK * 2
    min_m15_for_strategy_needed = (LIQUIDITY_LOOKBACK // 2) * 2 + 5

    if data_h1 is None or data_h1.empty or len(data_h1) < min_h1_for_context_needed:
         comment = f"Недостаточно данных H1 ({len(data_h1)} свечей) для определения контекста ({min_h1_for_context_needed} H1 свечей требуется)"
         print(f"  [{CROSS}] {comment}")
         return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    if data_m15 is None or data_m15.empty or len(data_m15) < min_m15_for_strategy_needed:
        comment = f"Недостаточно данных M15 ({len(data_m15)} свечей) для генерации сигнала с учетом lookback ({min_m15_for_strategy_needed} M15 свечей требуется)"
        print(f"  [{CROSS}] {comment}")
        return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context


    # 1. Определение контекста на H1
    h1_context = determine_h1_context(data_h1)

    if h1_context == 'NEUTRAL':
        comment = "H1 контекст нейтральный"
        print(f"  [{CROSS}] H1 контекст нейтральный, сигнал не генерируется.")
        return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    # 2. Поиск локального снятия ликвидности (свипа) на M15
    print(f"\n  [{ARROW}] Поиск M15 снятия ликвидности ({h1_context} контекст)...")
    m15_liquidity_lookback = LIQUIDITY_LOOKBACK // 2
    if len(data_m15) < m15_liquidity_lookback * 2 + 1:
         comment = f"Недостаточно данных M15 ({len(data_m15)}) для поиска ликвидности ({m15_liquidity_lookback} lookback)"
         print(f"  [{CROSS}] {comment}")
         return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context


    m15_liquidity = find_liquidity_levels(data_m15, m15_liquidity_lookback)
    print(f"    [{ARROW}] M15 Ликвидность (обнаруженные SH/SL) ({m15_liquidity_lookback} lookback): {m15_liquidity}")

    check_last_n_candles = min(5, len(data_m15))
    potential_signal_direction = 'NONE'

    print(f"    [{ARROW}] Проверка последних {check_last_n_candles} свечей M15 на релевантный свип...")
    for i in range(1, check_last_n_candles + 1):
         candle_idx = len(data_m15) - i
         if candle_idx < 0: break
         candle = data_m15.iloc[candle_idx]
         relevant_levels = [level for level in m15_liquidity if level['time'] < candle.name]

         for level in relevant_levels:
              if level['type'] == 'BSL' and candle['High'] > level['price']:
                  if h1_context == 'BEARISH':
                      sweep_m15_found = True
                      potential_signal_direction = 'SELL'
                      sweep_candle_time_m15 = candle.name
                      swept_level_m15 = level
                      print(f"      [{CHECK}] M15 Свип BSL найден на {candle.name} (уровень на {level['time']:.5f}). Соответствует медвежьему H1.")
                      break

              elif level['type'] == 'SSL' and candle['Low'] < level['price']:
                  if h1_context == 'BULLISH':
                      sweep_m15_found = True
                      potential_signal_direction = 'BUY'
                      sweep_candle_time_m15 = candle.name
                      swept_level_m15 = level
                      print(f"      [{CHECK}] M15 Свип SSL найден на {candle.name} (уровень на {level['time']:.5f}). Соответствует бычьему H1.")
                      break
         if sweep_m15_found:
             break

    if not sweep_m15_found:
        comment = "M15 снятие ликвидности не найдено или не соответствует H1 контексту"
        print(f"    [{CROSS}] {comment}")
        return 'NONE', entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    signal = potential_signal_direction


    # 3. Проверка формирования Order Flow на M15 после свипа
    status_of_m15, of_comment = check_order_flow(data_m15, signal, sweep_candle_time_m15, swept_level_m15)

    if status_of_m15 != 'CONFIRMED':
        comment = f"ОФ на M15 не подтвержден: {of_comment}"
        print(f"  [{CROSS}] {comment}. Сигнал не генерируется.")
        return 'NONE', entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    # 4. Определение точки входа, Stop Loss и Take Profit
    print(f"\n  [{ARROW}] Определение уровней входа/SL/TP...")

    imbalances_after_sweep = find_imbalances(data_m15.loc[data_m15.index > sweep_candle_time_m15].copy())
    relevant_imbalance_for_entry = None

    if signal == 'BUY':
        bullish_fugs = sorted([imb for imb in imbalances_after_sweep if imb['type'] == 'Bullish'], key=lambda x: x['start_time'])
        if bullish_fugs:
            relevant_imbalance_for_entry = bullish_fugs[0]

    elif signal == 'SELL':
        bearish_fugs = sorted([imb for imb in imbalances_after_sweep if imb['type'] == 'Bearish'], key=lambda x: x['start_time'])
        if bearish_fugs:
            relevant_imbalance_for_entry = bearish_fugs[0]

    if relevant_imbalance_for_entry is None:
        comment = "Не удалось найти релевантный FVG для точки входа после ОФ подтверждения (поиск после свипа)"
        print(f"    [{CROSS}] {comment}.")
        return 'NONE', entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    print(f"    [{CHECK}] Найден FVG для входа: {relevant_imbalance_for_entry['start_time']} - {relevant_imbalance_for_entry['end_time']} ({relevant_imbalance_for_entry['type']})")

    if signal == 'BUY':
        entry = relevant_imbalance_for_entry['start_price']
    elif signal == 'SELL':
        entry = relevant_imbalance_for_entry['start_price']

    print(f"    [{ARROW}] Расчетная точка входа (по FVG): {entry:.5f}")

    sweep_candle_m15 = data_m15.loc[sweep_candle_time_m15]
    if signal == 'BUY':
        sl = sweep_candle_m15['Low'] * 0.9998
    elif signal == 'SELL':
        sl = sweep_candle_m15['High'] * 1.0002

    print(f"    [{ARROW}] Расчетный Stop Loss (по свече свипа): {sl:.5f}")

    print(f"    [{ARROW}] Поиск цели TP...")
    data_after_fvg_end = data_m15.loc[data_m15.index > relevant_imbalance_for_entry['end_time']].copy()
    if data_after_fvg_end.empty:
         comment = "Нет данных M15 после FVG для поиска цели TP"
         print(f"    [{CROSS}] {comment}.")
         return 'NONE', entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    m15_targets_lookback = LIQUIDITY_LOOKBACK // 2
    if len(data_after_fvg_end) < m15_targets_lookback * 2 + 1:
         comment = f"Недостаточно данных M15 ({len(data_after_fvg_end)} свечей) после FVG для поиска целей TP ({m15_targets_lookback} lookback)"
         print(f"    [{CROSS}] {comment}.")
         return 'NONE', entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context


    m15_swing_points_after_fvg = find_liquidity_levels(data_after_fvg_end, m15_targets_lookback, is_swing_points=True)
    print(f"      [{ARROW}] M15 Swing Points после FVG ({m15_targets_lookback} lookback): {m15_swing_points_after_fvg}")

    target_level = None
    if signal == 'BUY':
        target_candidates = sorted([sp for sp in m15_swing_points_after_fvg if sp['type'] == 'BSL'], key=lambda x: x['time'])
        target_candidates = [t for t in target_candidates if t['price'] > entry]
        if target_candidates:
            target_level = target_candidates[0]
            tp = target_level['price']
            print(f"    [{CHECK}] Цель TP BUY найдена: BSL на {tp:.5f} (время {target_level['time']})")
        else:
             comment = "Не найдена цель TP (BSL) после FVG для покупки выше точки входа"
             print(f"    [{CROSS}] {comment}.")


    elif signal == 'SELL':
        target_candidates = sorted([sp for sp in m15_swing_points_after_fvg if sp['type'] == 'SSL'], key=lambda x: x['time'])
        target_candidates = [t for t in target_candidates if t['price'] < entry]
        if target_candidates:
            target_level = target_candidates[0]
            tp = target_level['price']
            print(f"    [{CHECK}] Цель TP SELL найдена: SSL на {tp:.5f} (время {target_level['time']})")
        else:
             comment = "Не найдена цель TP (SSL) после FVG для продажи ниже точки входа"
             print(f"    [{CROSS}] {comment}.")


    if entry is not None and sl is not None and tp is not None:
         if signal == 'BUY' and (sl >= entry or tp <= entry):
              print(f"    [{CROSS}] Ошибка уровней для BUY: SL={sl:.5f}, Entry={entry:.5f}, TP={tp:.5f}. SL должен быть < Entry, TP > Entry.")
              tp = None
              comment = "Ошибка уровней (SL/TP относительно Entry)"
         elif signal == 'SELL' and (sl <= entry or tp >= entry):
              print(f"    [{CROSS}] Ошибка уровней для SELL: SL={sl:.5f}, Entry={entry:.5f}, TP={tp:.5f}. SL должен быть > Entry, TP < Entry.")
              tp = None
              comment = "Ошибка уровней (SL/TP относительно Entry)"


    if tp is None:
         comment = comment
         print(f"  [{CROSS}] Цель TP не определена или уровни некорректны. Сигнал не генерируется.")
         return 'NONE', entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    print(f"    [{ARROW}] Расчетный Take Profit: {tp:.5f}")


    # 5. Проверка Risk/Reward
    rr_val = calculate_rr(entry, sl, tp, signal, asset)
    status_target_m15 = False
    if rr_val is not None and rr_val >= MIN_RR:
        status_target_m15 = True
        print(f"    [{CHECK}] Рассчитанный RR: {rr_val:.2f} >= {MIN_RR}. Условие RR выполнено.")
    else:
        comment = f"RR ({rr_val:.2f}) ниже минимального ({MIN_RR})" if rr_val is not None else "RR не рассчитан"
        print(f"  [{CROSS}] {comment}. Сигнал не генерируется.")
        return 'NONE', entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    # 6. Проверка Лондонской сессии (опционально, как фильтр)
    # is_london = is_london_session_active(data_m15.index[-1])
    # if not is_london:
    #     comment = "Не Лондонская сессия"
    #     print(f"  [{CROSS}] Не Лондонская сессия. Сигнал отменен.")
    #     return 'NONE', entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context
    # # else:
    #     # print(f"  [{CHECK}] Активна Лондонская сессия.")


    comment = "Сигнал сгенерирован"
    print(f"\n  [{CHECK}] >>> СИГНАЛ {signal} СГЕНЕРИРОВАН! Entry={entry:.5f}, SL={sl:.5f}, TP={tp:.5f}, RR={rr_val:.2f}\n")

    return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context


# Вспомогательная функция для проверки соответствия свипа H1 контексту (используется в determine_h1_context)
# Эта функция больше не нужна с новой логикой determine_h1_context
# def determine_h1_context_direction(sweep_type: str, h1_context_from_param: str) -> bool:
#     """Helper to check if sweep type matches the context being looked for."""
#     if sweep_type == 'BSL' and h1_context_from_param == 'BEARISH':
#         return True
#     if sweep_type == 'SSL' and h1_context_from_param == 'BULLISH':
#         return True
#     return False

# Функция определения активности Лондонской сессии (добавлена ранее)
def is_london_session_active(current_time: pd.Timestamp) -> bool:
    """
    Проверяет, активна ли Лондонская сессия в заданное время (UTC).
    Лондонская сессия: 08:00 - 17:00 UTC (может варьироваться с переходом на летнее время)
    """
    london_open_hour = 8
    london_close_hour = 17

    current_hour_utc = current_time.hour

    is_active = london_open_hour <= current_hour_utc < london_close_hour

    return is_active
