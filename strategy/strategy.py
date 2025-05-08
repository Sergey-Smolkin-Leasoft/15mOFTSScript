# strategy/strategy.py

import pandas as pd
import numpy as np
# ИСПРАВЛЕНО: Импортируем TIMEFRAME_DURATIONS_MINUTES
from config import LIQUIDITY_LOOKBACK, MIN_RR, TIMEFRAMES, TIMEFRAME_DURATIONS_MINUTES
# Удалена find_structure_break из импорта
from strategy.indicators import find_liquidity_levels, find_imbalances
# Импорт для логирования
# import logging
# logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

def determine_h1_context(data_h1: pd.DataFrame) -> str:
    """
    Определяет рыночный контекст (бычий, медвежий, нейтральный) на таймфрейме H1.
    Основано на недавнем снятии ликвидности и пробое структуры.
    """
    print(f"--- Определение H1 контекста ---")
    if data_h1 is None or data_h1.empty or len(data_h1) < LIQUIDITY_LOOKBACK * 2:
        print(f"H1 Контекст: Нейтральный (недостаточно данных - {len(data_h1)} свечей)")
        return 'NEUTRAL'

    h1_liquidity = find_liquidity_levels(data_h1, LIQUIDITY_LOOKBACK)
    print(f"H1 Ликвидность: {h1_liquidity}")

    recent_sweeps = []
    if h1_liquidity:
        last_h1_candle = data_h1.iloc[-1]
        for i in range(1, min(5, len(data_h1))):
            candle = data_h1.iloc[-i]
            for level in h1_liquidity:
                if candle.name > level['time']:
                    if level['type'] == 'BSL' and candle['High'] > level['price']:
                        if determine_h1_context_direction(sweep_type='BSL', h1_context_from_param='BEARISH'):
                            recent_sweeps.append({'type': 'BSL', 'time': candle.name, 'swept_level_time': level['time'], 'price': level['price']})
                            print(f"H1 Свип BSL найден на {candle.name}, уровень {level['price']:.5f} (изначальный уровень на {level['time']})")
                    elif level['type'] == 'SSL' and candle['Low'] < level['price']:
                         if determine_h1_context_direction(sweep_type='SSL', h1_context_from_param='BULLISH'):
                            recent_sweeps.append({'type': 'SSL', 'time': candle.name, 'swept_level_time': level['time'], 'price': level['price']})
                            print(f"H1 Свип SSL найден на {candle.name}, уровень {level['price']:.5f} (изначальный уровень на {level['time']})")

    if not recent_sweeps:
         print(f"H1 Контекст: Нейтральный (нет релевантных недавних свипов)")
         return 'NEUTRAL'

    recent_sweeps.sort(key=lambda x: x['time'])
    context_determined = 'NEUTRAL'

    for sweep in recent_sweeps:
        data_after_sweep = data_h1.loc[data_h1.index > sweep['time']].copy()

        if not data_after_sweep.empty:
             data_before_sweep = data_h1.loc[data_h1.index < sweep['time']].copy()
             if data_before_sweep.empty:
                  print(f"H1: Нет данных до свипа {sweep['time']} для поиска SP")
                  continue

             h1_swing_points_before_sweep = find_liquidity_levels(data_before_sweep, LIQUIDITY_LOOKBACK, is_swing_points=True)
             print(f"H1 Swing Points до свипа {sweep['time']}: {h1_swing_points_before_sweep}")

             if sweep['type'] == 'BSL':
                bearish_bos_found = False
                h1_swing_lows_before_sweep = sorted([sp for sp in h1_swing_points_before_sweep if sp['type'] == 'SSL'], key=lambda x: x['time'], reverse=True)

                for sl_level in h1_swing_lows_before_sweep:
                     if data_after_sweep['Low'].min() < sl_level['price']:
                          bearish_bos_found = True
                          print(f"H1 Медвежий пробой структуры найден после свипа BSL {sweep['time']}. Пробит SL на {sl_level['price']:.5f} (время SP: {sl_level['time']}).")
                          break

                if bearish_bos_found:
                    context_determined = 'BEARISH'
                    break

             elif sweep['type'] == 'SSL':
                bullish_bos_found = False
                h1_swing_highs_before_sweep = sorted([sp for sp in h1_swing_points_before_sweep if sp['type'] == 'BSL'], key=lambda x: x['time'], reverse=True)

                for sh_level in h1_swing_highs_before_sweep:
                     if data_after_sweep['High'].max() > sh_level['price']:
                          bullish_bos_found = True
                          print(f"H1 Бычий пробой структуры найден после свипа SSL {sweep['time']}. Пробит SH на {sh_level['price']:.5f} (время SP: {sh_level['time']}).")
                          break

                if bullish_bos_found:
                    context_determined = 'BULLISH'
                    break

    print(f"H1 Контекст: {context_determined}")
    return context_determined

def determine_h1_context_direction(sweep_type: str, h1_context_from_param: str) -> bool:
    """Helper to check if sweep type matches the context being looked for."""
    if sweep_type == 'BSL' and h1_context_from_param == 'BEARISH':
        return True
    if sweep_type == 'SSL' and h1_context_from_param == 'BULLISH':
        return True
    return False

def check_order_flow(data_m15: pd.DataFrame, direction: str, sweep_candle_time: pd.Timestamp, swept_level: dict) -> tuple[str, str]:
    """
    Проверяет формирование Order Flow (ОФ) на M15 после снятия ликвидности.
    Реализует логику "2 ступеней" ОФ:
    1. Пробой структуры (CHoCH) после свечи, снявшей ликвидность.
    2. Наличие и тест (возврат цены) к соответствующему Imbalance (FVG) после CHoCH.
    """
    print(f"  [check_OF] Начало проверки Order Flow на M15 для направления {direction} после свипа на {sweep_candle_time}")

    if data_m15 is None or data_m15.empty:
        print("  [check_OF] Недостаточно данных M15 для проверки ОФ.")
        return 'NONE', "Недостаточно данных M15"

    data_after_sweep_candle = data_m15.loc[data_m15.index > sweep_candle_time].copy()
    if data_after_sweep_candle.empty:
         print("  [check_OF] Нет данных M15 после свечи свипа для поиска CHoCH.")
         return 'NONE', "Нет данных M15 после свипа для CHoCH"

    m15_lookback_sp = LIQUIDITY_LOOKBACK // 2
    data_before_sweep_candle = data_m15.loc[data_m15.index < sweep_candle_time].copy()
    if len(data_before_sweep_candle) < m15_lookback_sp * 2 + 1:
        print(f"  [check_OF] Недостаточно данных M15 перед свипом для поиска предыдущей структуры ({len(data_before_sweep_candle)} свечей).")
        return 'NONE', "Недостаточно данных M15 перед свипом для CHoCH"

    m15_swing_points_before = find_liquidity_levels(data_before_sweep_candle, lookback=m15_lookback_sp, is_swing_points=True)
    print(f"  [check_OF] M15 Swing Points перед свипом ({m15_lookback_sp} lookback): {m15_swing_points_before}")

    structure_point_to_break = None
    if direction == 'BUY':
        sh_points = sorted([sp for sp in m15_swing_points_before if sp['type'] == 'BSL'], key=lambda x: x['time'], reverse=True)
        if sh_points:
            structure_point_to_break = sh_points[0]
            print(f"  [check_OF] Бычий CHoCH: Цель для пробоя (ближайший SH) на {structure_point_to_break['price']:.5f} (время {structure_point_to_break['time']})")
        else:
             print("  [check_OF] Бычий CHoCH: Не найдено Swing High перед свипом.")
             pass

    elif direction == 'SELL':
        sl_points = sorted([sp for sp in m15_swing_points_before if sp['type'] == 'SSL'], key=lambda x: x['time'], reverse=True)
        if sl_points:
            structure_point_to_break = sl_points[0]
            print(f"  [check_OF] Медвежий CHoCH: Цель для пробоя (ближайший SL) на {structure_point_to_break['price']:.5f} (время {structure_point_to_break['time']})")
        else:
             print("  [check_OF] Медвежий CHoCH: Не найдено Swing Low перед свипом.")
             pass


    if structure_point_to_break is None:
        print(f"  [check_OF] 1я ступень ОФ (CHoCH): Не найдена цель для пробоя.")
        return 'NONE', "1я ступень ОФ: Не найдена цель для пробоя"

    data_after_sweep_to_current = data_m15.loc[data_m15.index > sweep_candle_time].copy()

    choch_found = False
    choch_candle_time = None

    if direction == 'BUY':
        break_candidates = data_after_sweep_to_current[data_after_sweep_to_current['High'] > structure_point_to_break['price']]
        if not break_candidates.empty:
            choch_found = True
            choch_candle_time = break_candidates.index[0]
            print(f"  [check_OF] 1я ступень ОФ (Бычий CHoCH): Найден пробой вверх на свече {choch_candle_time}")

    elif direction == 'SELL':
        break_candidates = data_after_sweep_to_current[data_after_sweep_to_current['Low'] < structure_point_to_break['price']]
        if not break_candidates.empty:
            choch_found = True
            choch_candle_time = break_candidates.index[0]
            print(f"  [check_OF] 1я ступень ОФ (Медвежий CHoCH): Найден пробой вниз на свече {choch_candle_time}")


    if not choch_found:
        print(f"  [check_OF] 1я ступень ОФ (CHoCH): Пробой структуры не найден после свипа.")
        return 'NONE', "1я ступень ОФ: CHoCH не найден"

    data_after_choch_to_current = data_m15.loc[data_m15.index > choch_candle_time].copy()
    if data_after_choch_to_current.empty:
         print("  [check_OF] Нет данных M15 после свечи CHoCH для поиска FVG.")
         return 'NONE', "Нет данных M15 после CHoCH"

    imbalances = find_imbalances(data_after_choch_to_current)
    print(f"  [check_OF] Найдены FVG после CHoCH: {imbalances}")

    relevant_imbalance = None
    if direction == 'BUY':
        bullish_fugs = sorted([imb for imb in imbalances if imb['type'] == 'Bullish'], key=lambda x: x['start_time'])
        bullish_fugs = [imb for imb in bullish_fugs if imb['start_time'] > choch_candle_time]
        if bullish_fugs:
            relevant_imbalance = bullish_fugs[0]
            print(f"  [check_OF] 2я ступень ОФ (Бычий FVG): Найден FVG на {relevant_imbalance['start_time']} - {relevant_imbalance['end_time']}")
        else:
             print("  [check_OF] 2я ступень ОФ (Бычий FVG): Не найден бычий FVG после CHoCH.")
             pass

    elif direction == 'SELL':
        bearish_fugs = sorted([imb for imb in imbalances if imb['type'] == 'Bearish'], key=lambda x: x['start_time'])
        bearish_fugs = [imb for imb in bearish_fugs if imb['start_time'] > choch_candle_time]

        if bearish_fugs:
            relevant_imbalance = bearish_fugs[0]
            print(f"  [check_OF] 2я ступень ОФ (Медвежий FVG): Найден FVG на {relevant_imbalance['start_time']} - {relevant_imbalance['end_time']}")
        else:
            print("  [check_OF] 2я ступень ОФ (Медвежий FVG): Не найден медвежий FVG после CHoCH.")
            pass


    if relevant_imbalance is None:
        print(f"  [check_OF] 2я ступень ОФ: Соответствующий FVG не найден.")
        return 'NONE', "2я ступень ОФ: FVG не найден"

    data_after_fvg_end_to_current = data_m15.loc[data_m15.index > relevant_imbalance['end_time']].copy()
    if data_after_fvg_end_to_current.empty:
         print("  [check_OF] Нет данных M15 после FVG для проверки теста.")
         return 'NONE', "Нет данных M15 после FVG для теста"

    fvg_tested = False
    if direction == 'BUY':
        fvg_high = relevant_imbalance['start_price']
        fvg_low = relevant_imbalance['end_price']

        test_candidates = data_after_fvg_end_to_current[(data_after_fvg_end_to_current['Low'] <= fvg_high) & (data_after_fvg_end_to_current['High'] >= fvg_low)]
        if not test_candidates.empty:
            fvg_tested = True
            print(f"  [check_OF] 2я ступень ОФ (Бычий FVG тест): Найден возврат в FVG на свече {test_candidates.index[0]}")

    elif direction == 'SELL':
        fvg_low = relevant_imbalance['start_price']
        fvg_high = relevant_imbalance['end_price']

        test_candidates = data_after_fvg_end_to_current[(data_after_fvg_end_to_current['Low'] <= fvg_high) & (data_after_fvg_end_to_current['High'] >= fvg_low)]
        if not test_candidates.empty:
            fvg_tested = True
            print(f"  [check_OF] 2я ступень ОФ (Медвежий FVG тест): Найден возврат в FVG на свече {test_candidates.index[0]}")


    if fvg_tested:
        print(f"  [check_OF] Order Flow {direction} подтвержден (CHoCH + FVG тест).")
        return 'CONFIRMED', "ОФ подтвержден (CHoCH + FVG тест)"
    else:
        print(f"  [check_OF] 2я ступень ОФ: Тест FVG не найден.")
        return 'NONE', "2я ступень ОФ: Тест FVG не найден"


def calculate_rr(entry_price, stop_loss_price, take_profit_price, direction):
    """
    Рассчитывает отношение Risk/Reward.
    """
    if entry_price is None or stop_loss_price is None or take_profit_price is None:
        print(f"  [calc_RR] Недостаточно данных для расчета RR.")
        return 0.0

    if direction == 'BUY':
        risk = entry_price - stop_loss_price
        reward = take_profit_price - entry_price
    elif direction == 'SELL':
        risk = stop_loss_price - entry_price
        reward = entry_price - take_profit_price
    else:
        print(f"  [calc_RR] Неизвестное направление: {direction}")
        return 0.0

    if risk is None or risk <= 0:
        print(f"  [calc_RR] Риск <= 0 или None ({risk}). Деление на ноль невозможно.")
        return 0.0

    rr = reward / risk
    print(f"  [calc_RR] Entry={entry_price:.5f}, SL={stop_loss_price:.5f}, TP={take_profit_price:.5f}. Риск={abs(risk):.5f}, Reward={abs(reward):.5f}. RR={rr:.2f}")
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

    return is_london_session_active


def generate_signal(asset: str, data_h1: pd.DataFrame, data_m15: pd.DataFrame):
    """
    Анализирует данные и генерирует торговый сигнал на M15 на основе контекста H1
    и подтверждения Order Flow на M15.
    """
    print(f"\n--- Генерация сигнала для {asset} на {data_m15.index[-1]} ---")
    current_m15_candle_time = data_m15.index[-1]

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


    min_candles_needed_m15 = LIQUIDITY_LOOKBACK * 2 + 5

    if data_m15 is None or data_m15.empty or len(data_m15) < min_candles_needed_m15:
        comment = f"Недостаточно данных M15 ({len(data_m15)}) для генерации сигнала с учетом lookback ({LIQUIDITY_LOOKBACK})"
        print(f"[{current_m15_candle_time}] {comment}")
        return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    h1_duration = TIMEFRAME_DURATIONS_MINUTES.get('H1')
    m15_duration = TIMEFRAME_DURATIONS_MINUTES.get('M15')

    if h1_duration is None or m15_duration is None or m15_duration == 0:
        comment = "Ошибка конфигурации: Не заданы или неверны длительности таймфреймов в минутах в config.py."
        print(f"[{current_m15_candle_time}] {comment}")
        return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    min_h1_candles_needed = int(min_candles_needed_m15 * (m15_duration / h1_duration))
    min_h1_candles_needed = max(1, min_h1_candles_needed)


    if data_h1 is None or data_h1.empty or len(data_h1) < min_h1_candles_needed:
         comment = f"Недостаточно данных H1 ({len(data_h1)}) для определения контекста с учетом lookback ({min_h1_candles_needed} H1 свечей требуется)"
         print(f"[{current_m15_candle_time}] {comment}")
         return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context


    h1_context = determine_h1_context(data_h1)
    print(f"[{current_m15_candle_time}] Определен H1 контекст: {h1_context}")

    if h1_context == 'NEUTRAL':
        comment = "H1 контекст нейтральный"
        print(f"[{current_m15_candle_time}] H1 контекст нейтральный, сигнал не генерируется.")
        return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    m15_liquidity_lookback = LIQUIDITY_LOOKBACK // 2
    if len(data_m15) < m15_liquidity_lookback * 2 + 1:
         comment = f"Недостаточно данных M15 ({len(data_m15)}) для поиска ликвидности с учетом lookback ({m15_liquidity_lookback})"
         print(f"[{current_m15_candle_time}] {comment}")
         return signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context


    m15_liquidity = find_liquidity_levels(data_m15, m15_liquidity_lookback)
    print(f"[{current_m15_candle_time}] M15 Ликвидность ({m15_liquidity_lookback} lookback): {m15_liquidity}")


    potential_signal_direction = 'NONE'

    if m15_liquidity:
        check_last_n_candles = min(5, len(data_m15))
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
                          comment = f"M15 свип BSL на {candle.name}"
                          print(f"[{current_m15_candle_time}] M15 Свип BSL найден на {candle.name} (уровень на {level['time']:.5f}). Контекст H1 медвежий. Ожидаем SELL.")
                          break

                  elif level['type'] == 'SSL' and candle['Low'] < level['price']:
                      if h1_context == 'BULLISH':
                          sweep_m15_found = True
                          potential_signal_direction = 'BUY'
                          sweep_candle_time_m15 = candle.name
                          swept_level_m15 = level
                          comment = f"M15 свип SSL на {candle.name}"
                          print(f"[{current_m15_candle_time}] M15 Свип SSL найден на {candle.name} (уровень на {level['time']:.5f}). Контекст H1 бычий. Ожидаем BUY.")
                          break
             if sweep_m15_found:
                 break

    if not sweep_m15_found:
        comment = "M15 снятие ликвидности не найдено или не соответствует H1 контексту"
        print(f"[{current_m15_candle_time}] {comment}")
        return 'NONE', entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    signal = potential_signal_direction

    data_after_sweep_for_of = data_m15.loc[data_m15.index > sweep_candle_time_m15].copy()
    if len(data_after_sweep_for_of) < 3:
        comment = f"Недостаточно данных M15 ({len(data_after_sweep_for_of)} свечей) после свипа для проверки ОФ"
        print(f"[{current_m15_candle_time}] {comment}.")
        return 'NONE', entry, sl, tp, rr_val, sweep_m15_found, 'NONE', False, comment, swept_level_m15, h1_context


    print(f"[{current_m15_candle_time}] Проверка 2 ступеней Order Flow на M15...")
    status_of_m15, of_comment = check_order_flow(data_m15, signal, sweep_candle_time_m15, swept_level_m15)

    if status_of_m15 != 'CONFIRMED':
        comment = f"ОФ на M15 не подтвержден: {of_comment}"
        print(f"[{current_m15_candle_time}] {comment}. Сигнал не генерируется.")
        return 'NONE', entry, sl, tp, rr_val, sweep_m15_found, status_of_m15, False, comment, swept_level_m15, h1_context

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
        print(f"[{current_m15_candle_time}] {comment}.")
        return 'NONE', entry, sl, tp, rr_val, sweep_m15_found, status_of_m15, False, comment, swept_level_m15, h1_context

    if signal == 'BUY':
        entry = relevant_imbalance_for_entry['start_price']
    elif signal == 'SELL':
        entry = relevant_imbalance_for_entry['start_price']

    sweep_candle_m15 = data_m15.loc[sweep_candle_time_m15]
    if signal == 'BUY':
        sl = sweep_candle_m15['Low'] * 0.9998
    elif signal == 'SELL':
        sl = sweep_candle_m15['High'] * 1.0002

    data_after_fvg_end = data_m15.loc[data_m15.index > relevant_imbalance_for_entry['end_time']].copy()
    if data_after_fvg_end.empty:
         comment = "Нет данных M15 после FVG для поиска цели TP"
         print(f"[{current_m15_candle_time}] {comment}.")
         return 'NONE', entry, sl, tp, rr_val, sweep_m15_found, status_of_m15, False, comment, swept_level_m15, h1_context

    m15_targets_lookback = LIQUIDITY_LOOKBACK // 2
    if len(data_after_fvg_end) < m15_targets_lookback * 2 + 1:
         comment = f"Недостаточно данных M15 ({len(data_after_fvg_end)} свечей) после FVG для поиска целей TP с учетом lookback ({m15_targets_lookback})"
         print(f"[{current_m15_candle_time}] {comment}.")
         return 'NONE', entry, sl, tp, rr_val, sweep_m15_found, status_of_m15, False, comment, swept_level_m15, h1_context

    m15_swing_points_after_fvg = find_liquidity_levels(data_after_fvg_end, m15_targets_lookback, is_swing_points=True)
    print(f"[{current_m15_candle_time}] M15 Swing Points после FVG ({m15_targets_lookback} lookback) для целей TP: {m15_swing_points_after_fvg}")

    target_level = None
    if signal == 'BUY':
        target_candidates = sorted([sp for sp in m15_swing_points_after_fvg if sp['type'] == 'BSL'], key=lambda x: x['time'])
        target_candidates = [t for t in target_candidates if t['price'] > entry]
        if target_candidates:
            target_level = target_candidates[0]
            tp = target_level['price']
            print(f"[{current_m15_candle_time}] Цель TP BUY найдена: BSL на {tp:.5f} (время {target_level['time']})")
        else:
             comment = "Не найдена цель TP (BSL) после FVG для покупки выше точки входа"
             print(f"[{current_m15_candle_time}] {comment}.")

    elif signal == 'SELL':
        target_candidates = sorted([sp for sp in m15_swing_points_after_fvg if sp['type'] == 'SSL'], key=lambda x: x['time'])
        target_candidates = [t for t in target_candidates if t['price'] < entry]
        if target_candidates:
            target_level = target_candidates[0]
            tp = target_level['price']
            print(f"[{current_m15_candle_time}] Цель TP SELL найдена: SSL на {tp:.5f} (время {target_level['time']})")
        else:
             comment = "Не найдена цель TP (SSL) после FVG для продажи ниже точки входа"
             print(f"[{current_m15_candle_time}] {comment}.")

    if entry is not None and sl is not None and tp is not None:
         if signal == 'BUY' and (sl >= entry or tp <= entry):
              print(f"[{current_m15_candle_time}] Ошибка уровней для BUY: SL={sl:.5f}, Entry={entry:.5f}, TP={tp:.5f}. SL должен быть < Entry, TP > Entry.")
              tp = None
              comment = "Ошибка уровней (SL/TP относительно Entry)"
         elif signal == 'SELL' and (sl <= entry or tp >= entry):
              print(f"[{current_m15_candle_time}] Ошибка уровней для SELL: SL={sl:.5f}, Entry={entry:.5f}, TP={tp:.5f}. SL должен быть > Entry, TP < Entry.")
              tp = None
              comment = "Ошибка уровней (SL/TP относительно Entry)"

    if tp is None:
         comment = comment
         print(f"[{current_m15_candle_time}] Цель TP не определена или уровни некорректны. Сигнал не генерируется.")
         return 'NONE', entry, sl, tp, rr_val, sweep_m15_found, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    rr_val = calculate_rr(entry, sl, tp, signal)
    status_target_m15 = False
    if rr_val is not None and rr_val >= MIN_RR:
        status_target_m15 = True
        print(f"[{current_m15_candle_time}] Рассчитанный RR: {rr_val:.2f} >= {MIN_RR}. Условие RR выполнено.")
    else:
        comment = f"RR ({rr_val:.2f}) ниже минимального ({MIN_RR})" if rr_val is not None else "RR не рассчитан"
        print(f"[{current_m15_candle_time}] {comment}. Сигнал не генерируется.")
        return 'NONE', entry, sl, tp, rr_val, sweep_m15_found, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

    comment = "Сигнал сгенерирован"
    print(f"\n[{current_m15_candle_time}] >>> СИГНАЛ {signal} СГЕНЕРИРОВАН! Entry={entry:.5f}, SL={sl:.5f}, TP={tp:.5f}, RR={rr_val:.2f}\n")

    return signal, entry, sl, tp, rr_val, sweep_m15_found, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context

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