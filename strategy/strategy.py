# strategy/strategy.py - MODIFIED

import pandas as pd
import numpy as np
from datetime import datetime, time
import pytz
from .indicators import find_liquidity_levels, find_imbalances
from config import (
    LIQUIDITY_LOOKBACK, MIN_RR, IMBALANCE_THRESHOLD,
    LONDON_OPEN_HOUR, LONDON_CLOSE_HOUR,
    MIN_STRUCTURE_BREAK_CANDLES,
    MIN_H1_OF_CANDLES # Импортируем новую константу
)

# calculate_rr и is_london_session_active остаются без изменений
def calculate_rr(entry, sl, target):
    """Расчет Reward/Risk."""
    risk = abs(entry - sl)
    reward = abs(target - entry)
    if risk == 0 or risk is None or reward is None:
        return 0.0
    return float(reward) / float(risk) if risk != 0 else 0.0

def is_london_session_active(dt_index):
    """Проверяет, приходится ли время свечи на активную Лондонскую сессию (UTC)."""
    if dt_index is None: return False
    try:
        dt_utc = dt_index
        if hasattr(dt_utc, 'hour'):
            if dt_utc.hour >= LONDON_OPEN_HOUR and dt_utc.hour < LONDON_CLOSE_HOUR:
                return True
            return False
        return False
    except Exception:
        return False

# check_order_flow остается без изменений (это логика 15m OF)
def check_order_flow(data: pd.DataFrame, direction: str, sweep_candle_index):
    """
    Проверка на формирование Order Flow по концепции "2 ступеней" на M15.
    """
    entry_price = None
    comment_of = ""

    if sweep_candle_index is None or data is None or data.empty:
        return False, None, "Недостаточно данных или не определена свеча свипа на M15."

    data_after_sweep = data.loc[sweep_candle_index:].copy()

    if len(data_after_sweep) < MIN_STRUCTURE_BREAK_CANDLES + 3: # Минимум для пробоя и нескольких свечей после
        return False, None, f"Недостаточно свечей после свипа на M15 ({len(data_after_sweep)})."

    # --- Ступень 1 (M15): Поиск пробоя локальной структуры (CHoCH) ---
    of_structure_lookback = max(3, min(LIQUIDITY_LOOKBACK // 2, len(data_after_sweep) // 3))
    highs_after_sweep, lows_after_sweep = find_liquidity_levels(data_after_sweep, lookback=of_structure_lookback)

    structure_broken = False
    structure_break_level = None
    structure_break_candle_index = None
    comment_struct_break = ""

    if direction == 'bullish':
        recent_highs_for_break = [(idx, level) for idx, level in highs_after_sweep if idx < data_after_sweep.index[-max(1, len(data_after_sweep) // 3)]]
        if recent_highs_for_break:
            highest_recent_high_idx, highest_recent_high_level = max(recent_highs_for_break, key=lambda x: x[1])
            potential_break_candles = data_after_sweep.loc[highest_recent_high_idx:][data_after_sweep.loc[highest_recent_high_idx:]['Close'] > highest_recent_high_level]
            if not potential_break_candles.empty:
                first_break_candle = potential_break_candles.iloc[0]
                if first_break_candle.name > highest_recent_high_idx and \
                   len(data_after_sweep.loc[first_break_candle.name:]) >= MIN_STRUCTURE_BREAK_CANDLES:
                     structure_broken = True
                     structure_break_level = highest_recent_high_level
                     structure_break_candle_index = first_break_candle.name
                     comment_struct_break = f"Бычий пробой структуры на M15 выше {structure_break_level:.5f} свечой {structure_break_candle_index.strftime('%H:%M')}. "

    elif direction == 'bearish':
         recent_lows_for_break = [(idx, level) for idx, level in lows_after_sweep if idx < data_after_sweep.index[-max(1, len(data_after_sweep) // 3)]]
         if recent_lows_for_break:
              lowest_recent_low_idx, lowest_recent_low_level = min(recent_lows_for_break, key=lambda x: x[1])
              potential_break_candles = data_after_sweep.loc[lowest_recent_low_idx:][data_after_sweep.loc[lowest_recent_low_idx:]['Close'] < lowest_recent_low_level]
              if not potential_break_candles.empty:
                   first_break_candle = potential_break_candles.iloc[0]
                   if first_break_candle.name > lowest_recent_low_idx and \
                      len(data_after_sweep.loc[first_break_candle.name:]) >= MIN_STRUCTURE_BREAK_CANDLES:
                      structure_broken = True
                      structure_break_level = lowest_recent_low_level
                      structure_break_candle_index = first_break_candle.name
                      comment_struct_break = f"Медвежий пробой структуры на M15 ниже {structure_break_level:.5f} свечой {structure_break_candle_index.strftime('%H:%M')}. "

    comment_of += comment_struct_break

    if not structure_broken:
        comment_of += "Пробой локальной структуры на M15 (CHoCH) не обнаружен. "
        return False, None, comment_of


    # --- Ступень 2 (M15): Поиск релевантного FVG после пробоя структуры и проверка отката ---
    imbalances_after_break = find_imbalances(data_after_sweep.loc[structure_break_candle_index:].copy(), threshold=IMBALANCE_THRESHOLD)

    relevant_imbalances = []
    if direction == 'bullish':
         relevant_imbalances = [imb for imb in imbalances_after_break if imb[3] == 'bullish']
         relevant_imbalances = [imb for imb in relevant_imbalances if imb[2] < data.iloc[-1]['Close']] # imb_high < latest_price

    elif direction == 'bearish':
         relevant_imbalances = [imb for imb in imbalances_after_break if imb[3] == 'bearish']
         relevant_imbalances = [imb for imb in relevant_imbalances if imb[1] > data.iloc[-1]['Close']] # imb_low > latest_price

    if not relevant_imbalances:
        comment_of += "Не найден релевантный M15 FVG после пробоя структуры для входа. "
        return False, None, comment_of

    relevant_imbalances.sort(key=lambda x: x[0])
    first_relevant_fvg_idx, first_relevant_fvg_low, first_relevant_fvg_high, fvg_type = relevant_imbalances[0]

    data_after_fvg = data.loc[first_relevant_fvg_idx:].copy()

    price_entered_fvg = False
    if direction == 'bullish':
         if not data_after_fvg.empty and data_after_fvg['Low'].min() <= first_relevant_fvg_high:
              price_entered_fvg = True
    elif direction == 'bearish':
         if not data_after_fvg.empty and data_after_fvg['High'].max() >= first_relevant_fvg_low:
              price_entered_fvg = True


    if price_entered_fvg:
         entry_price = (first_relevant_fvg_low + first_relevant_fvg_high) / 2.0
         comment_of += f"Цена откатила в {fvg_type} M15 FVG ({first_relevant_fvg_low:.5f}-{first_relevant_fvg_high:.5f}) сформированный на {first_relevant_fvg_idx.strftime('%H:%M')}. Вход на середине FVG. "
         return True, entry_price, comment_of
    else:
         comment_of += f"Цена не откатила в релевантный {fvg_type} M15 FVG ({first_relevant_fvg_low:.5f}-{first_relevant_fvg_high:.5f}) сформированный на {first_relevant_fvg_idx.strftime('%H:%M')}. "
         return False, None, comment_of


# --- НОВАЯ ФУНКЦИЯ для определения H1 контекста ---
def determine_h1_context(data_h1: pd.DataFrame | None):
    """
    Определяет контекст рынка (направление Order Flow) на H1 таймфрейме.
    Ищет недавнее снятие ликвидности и последующее формирование Order Flow.

    Возвращает: 'BULLISH', 'BEARISH', или 'NEUTRAL'.
    """
    context = 'NEUTRAL'
    context_comment = "H1 контекст: Не определен. "

    if data_h1 is None or data_h1.empty or len(data_h1) < LIQUIDITY_LOOKBACK * 2:
         context_comment = f"H1 контекст: Недостаточно данных ({len(data_h1) if data_h1 is not None else 0} свечей, нужно >={LIQUIDITY_LOOKBACK*2}). "
         return context, context_comment

    # Находим ликвидность на H1
    highs_h1, lows_h1 = find_liquidity_levels(data_h1, lookback=LIQUIDITY_LOOKBACK)

    if not highs_h1 and not lows_h1:
        context_comment += "Нет значимых Swing High/Low на H1. "
        return context, context_comment

    # Ищем последнее значимое снятие ликвидности на H1
    # Смотрим на свечи после последнего H1 фрактала
    start_idx_check_sweep_h1 = None
    if highs_h1: start_idx_check_sweep_h1 = highs_h1[-1][0]
    if lows_h1:
        if start_idx_check_sweep_h1 is None or lows_h1[-1][0] < start_idx_check_sweep_h1:
            start_idx_check_sweep_h1 = lows_h1[-1][0]

    if start_idx_check_sweep_h1 is None:
        context_comment += "Нет недавних значимых Swing High/Low на H1 для поиска свипа. "
        return context, context_comment


    recent_candles_h1_for_sweep = data_h1.loc[start_idx_check_sweep_h1:].copy()

    sweep_candle_info_h1 = None # (Series свечи свипа, direction, swept_level)

    # Проверяем свип последнего H1 хая
    if highs_h1:
         last_h1_high_idx, last_h1_high_level = highs_h1[-1]
         sweeping_candles_bearish_h1 = recent_candles_h1_for_sweep[recent_candles_h1_for_sweep['High'] > last_h1_high_level]
         if not sweeping_candles_bearish_h1.empty:
              sweep_candle_info_h1 = (sweeping_candles_bearish_h1.iloc[0], 'bearish', last_h1_high_level)

    # Проверяем свип последнего H1 лоя (только если не нашли свип хая)
    if sweep_candle_info_h1 is None and lows_h1:
         last_h1_low_idx, last_h1_low_level = lows_h1[-1]
         sweeping_candles_bullish_h1 = recent_candles_h1_for_sweep[recent_candles_h1_for_sweep['Low'] < last_h1_low_level]
         if not sweeping_candles_bullish_h1.empty:
              sweep_candle_info_h1 = (sweeping_candles_bullish_h1.iloc[0], 'bullish', last_h1_low_level)


    if not sweep_candle_info_h1:
        context_comment += "Нет недавнего снятия ликвидности на H1. "
        return context, context_comment


    # Снятие ликвидности на H1 было. Теперь ищем подтверждение Order Flow на H1.
    # Для контекста достаточно простой проверки: был ли пробой структуры после свипа
    sweeping_candle_series_h1, sweep_direction_h1, swept_level_h1 = sweep_candle_info_h1
    h1_sweep_idx = sweeping_candle_series_h1.name

    data_after_h1_sweep = data_h1.loc[h1_sweep_idx:].copy()

    if len(data_after_h1_sweep) < MIN_H1_OF_CANDLES:
        context_comment += f"Недостаточно свечей на H1 после свипа ({len(data_after_h1_sweep)}) для подтверждения OF. "
        return context, context_comment

    # Ищем локальные фракталы ПОСЛЕ H1 свипа для определения пробоя структуры на H1
    h1_of_structure_lookback = max(3, min(LIQUIDITY_LOOKBACK // 2, len(data_after_h1_sweep) // 3))
    highs_after_h1_sweep, lows_after_h1_sweep = find_liquidity_levels(data_after_h1_sweep, lookback=h1_of_structure_lookback)

    h1_structure_broken = False

    if sweep_direction_h1 == 'bullish': # После свипа SSL на H1, ищем пробой локального хая на H1
        # Ищем любой хай ПОСЛЕ H1 свипа
        recent_highs_h1 = [level for idx, level in highs_after_h1_sweep]
        if recent_highs_h1:
             # Проверяем, было ли закрытие ВЫШЕ самого высокого локального хая после свипа на H1
             if data_after_h1_sweep['Close'].max() > max(recent_highs_h1):
                  h1_structure_broken = True

    elif sweep_direction_h1 == 'bearish': # После свипа BSL на H1, ищем пробой локального лоя на H1
        # Ищем любой лой ПОСЛЕ H1 свипа
        recent_lows_h1 = [level for idx, level in lows_after_h1_sweep]
        if recent_lows_h1:
             # Проверяем, было ли закрытие НИЖЕ самого низкого локального лоя после свипа на H1
             if data_after_h1_sweep['Close'].min() < min(recent_lows_h1):
                  h1_structure_broken = True

    if h1_structure_broken:
        if sweep_direction_h1 == 'bullish':
             context = 'BULLISH'
             context_comment += f"H1 контекст: Бычий (Свип SSL + Пробой структуры). "
        elif sweep_direction_h1 == 'bearish':
             context = 'BEARISH'
             context_comment += f"H1 контекст: Медвежий (Свип BSL + Пробой структуры). "
    else:
        context_comment += "H1 контекст: Снятие ликвидности H1 было, но нет подтверждения Order Flow (пробоя структуры). "

    return context, context_comment


# ИЗМЕНЕНА ПОДПИСЬ ФУНКЦИИ generate_signal: теперь принимает data_h1
def generate_signal(asset: str, data_h1: pd.DataFrame | None, data_m15: pd.DataFrame | None):
# ---------------------------------------------------------------------
    """
    Генерирует торговый сигнал на основе стратегии 15mOF с фильтрацией по H1 контексту.
    """
    status_sweep = False
    status_of = False
    status_target = False

    potential_signal = 'NONE'
    entry_price = None
    stop_loss = None
    take_profit = None
    rr = None
    comment = ""
    recent_sweep_idx_m15 = None # Переименована для ясности
    sweep_direction_m15 = None # Переименована для ясности
    swept_level_m15 = None


    # --- ШАГ 1: Определение H1 контекста ---
    h1_context, h1_context_comment = determine_h1_context(data_h1)
    comment += h1_context_comment
    # -------------------------------------

    # Проверка на наличие данных M15
    if data_m15 is None or data_m15.empty or len(data_m15) < LIQUIDITY_LOOKBACK * 3:
         comment += f"Недостаточно данных M15 для анализа ({len(data_m15) if data_m15 is not None else 0} свечей, нужно >={LIQUIDITY_LOOKBACK*3})."
         # Возвращаем контекст, но сигнал NONE
         return 'NONE', None, None, None, None, status_sweep, status_of, status_target, comment, swept_level_m15, h1_context


    # --- ШАГ 2: Проверка на снятие SSL/BSL на M15 ---
    highs_m15, lows_m15 = find_liquidity_levels(data_m15, lookback=LIQUIDITY_LOOKBACK)

    recent_highs_m15 = highs_m15[-3:] if len(highs_m15) > 3 else highs_m15
    recent_lows_m15 = lows_m15[-3:] if len(lows_m15) > 3 else lows_m15

    last_significant_high_m15 = recent_highs_m15[-1] if recent_highs_m15 else None
    last_significant_low_m15 = recent_lows_m15[-1] if recent_lows_m15 else None

    start_idx_check_sweep_m15 = None
    if last_significant_high_m15: start_idx_check_sweep_m15 = last_significant_high_m15[0]
    if last_significant_low_m15:
        if start_idx_check_sweep_m15 is None or last_significant_low_m15[0] < start_idx_check_sweep_m15:
            start_idx_check_sweep_m15 = last_significant_low_m15[0]

    if start_idx_check_sweep_m15 is None:
        comment += "Нет значимых Swing High/Low на M15 для определения свипа. "
        return 'NONE', None, None, None, None, status_sweep, status_of, status_target, comment, swept_level_m15, h1_context

    recent_candles_m15_for_sweep_check = data_m15.loc[start_idx_check_sweep_m15:].copy()

    sweep_candle_info_m15 = None # (Series свечи свипа, direction, swept_level)

    if last_significant_high_m15:
         sweeping_candles_bearish_m15 = recent_candles_m15_for_sweep_check[recent_candles_m15_for_sweep_check['High'] > last_significant_high_m15[1]]
         if not sweeping_candles_bearish_m15.empty:
              sweep_candle_info_m15 = (sweeping_candles_bearish_m15.iloc[0], 'bearish', last_significant_high_m15[1])

    if sweep_candle_info_m15 is None and last_significant_low_m15:
         sweeping_candles_bullish_m15 = recent_candles_m15_for_sweep_check[recent_candles_m15_for_sweep_check['Low'] < last_significant_low_m15[1]]
         if not sweeping_candles_bullish_m15.empty:
              sweep_candle_info_m15 = (sweeping_candles_bullish_m15.iloc[0], 'bullish', last_significant_low_m15[1])


    if sweep_candle_info_m15:
        sweeping_candle_series_m15, sweep_direction_m15, swept_level_price_m15 = sweep_candle_info_m15
        recent_sweep_idx_m15 = sweeping_candle_series_m15.name
        status_sweep = True
        swept_level_m15 = swept_level_price_m15

        # Определяем потенциальное направление сигнала на M15
        if sweep_direction_m15 == 'bearish':
             potential_signal = 'SELL'
             comment += f"Снятие BSL ({swept_level_m15:.5f}) на M15 свечой {recent_sweep_idx_m15.strftime('%Y-%m-%d %H:%M')}. "
             stop_loss = sweeping_candle_series_m15['High'] * 1.0005
        elif sweep_direction_m15 == 'bullish':
             potential_signal = 'BUY'
             comment += f"Снятие SSL ({swept_level_m15:.5f}) на M15 свечой {recent_sweep_idx_m15.strftime('%Y-%m-%d %H:%M')}. "
             stop_loss = sweeping_candle_series_m15['Low'] * 0.9995

    if not status_sweep:
        comment += "Нет недавнего снятия ликвидности на M15. "
        return 'NONE', None, None, None, None, status_sweep, status_of, status_target, comment, swept_level_m15, h1_context


    # --- ШАГ 3: Фильтрация по H1 контексту ---
    # Если потенциальный сигнал M15 не соответствует H1 контексту, отменяем сигнал
    if h1_context != 'NEUTRAL' and (
        (potential_signal == 'BUY' and h1_context != 'BULLISH') or
        (potential_signal == 'SELL' and h1_context != 'BEARISH')
    ):
        comment += f"Отмена сигнала: Потенциальный M15 {potential_signal} сигнал не соответствует H1 {h1_context} контексту. "
        # Возвращаем swept_level_m15 для отрисовки на графике
        return 'NONE', None, None, None, None, status_sweep, status_of, status_target, comment, swept_level_m15, h1_context


    # --- ШАГ 4: Проверка 2 ступеней OF на M15 и поиск входа (только если контекст совпал или нейтрален) ---
    of_confirmed_and_entry_found, entry_from_of, comment_of_step = check_order_flow(data_m15, sweep_direction_m15, recent_sweep_idx_m15)

    comment += comment_of_step # Добавляем комментарий из check_order_flow

    if of_confirmed_and_entry_found:
         status_of = True
         entry_price = entry_from_of
         if stop_loss is None: # Повторная проверка, если SL не определился при свипе
             status_of = False
             comment += "Ошибка: M15 OF подтвержден, но Stop Loss не определен. "
    # else: status_of остается False, comment_of_step уже добавлен

    # Если M15 OF не подтвержден, или SL не определился, дальше не ищем цель
    if not status_of:
        if status_sweep: # Только если был свип M15
             comment += "Пропуск анализа цели: не выполнены 2 ступени Order Flow на M15 или не найдена точка входа. "
        return 'NONE', entry_price, stop_loss, take_profit, rr, status_sweep, status_of, status_target, comment, swept_level_m15, h1_context


    # --- ШАГ 5: Определение цели на M15 и проверка RR ---
    highs_m15_after_sweep = [(idx, lvl) for idx, lvl in highs_m15 if recent_sweep_idx_m15 is not None and idx > recent_sweep_idx_m15]
    lows_m15_after_sweep = [(idx, lvl) for idx, lvl in lows_m15 if recent_sweep_idx_m15 is not None and idx > recent_sweep_idx_m15]

    target_found = False
    if potential_signal == 'BUY':
        highs_m15_after_sweep.sort(key=lambda x: x[1])
        for target_idx, potential_target in highs_m15_after_sweep:
             if potential_target > entry_price:
                 calculated_rr = calculate_rr(entry_price, stop_loss, potential_target)
                 if calculated_rr >= MIN_RR:
                     take_profit = potential_target
                     rr = calculated_rr
                     target_found = True
                     status_target = True
                     comment += f"Цель найдена (High на {target_idx.strftime('%Y-%m-%d %H:%M')}) с ценой {take_profit:.5f} (RR = {rr:.2f} >= {MIN_RR}). "
                     break

    elif potential_signal == 'SELL':
        lows_m15_after_sweep.sort(key=lambda x: x[1], reverse=True)
        for target_idx, potential_target in lows_m15_after_sweep:
            if potential_target < entry_price:
                 calculated_rr = calculate_rr(entry_price, stop_loss, potential_target)
                 if calculated_rr >= MIN_RR:
                     take_profit = potential_target
                     rr = calculated_rr
                     target_found = True
                     status_target = True
                     comment += f"Цель найдена (Low на {target_idx.strftime('%Y-%m-%d %H:%M')}) с ценой {take_profit:.5f} (RR = {rr:.2f} >= {MIN_RR}). "
                     break

    if not target_found:
        comment += f"Не удалось найти цель на M15 после свипа с RR >= {MIN_RR}. "
        return 'NONE', entry_price, stop_loss, take_profit, rr, status_sweep, status_of, status_target, comment, swept_level_m15, h1_context


    # --- Все условия соблюдены. Генерируем сигнал. ---
    # 6. Проверка условия Лондонской сессии (для комментария)
    latest_candle_time_m15 = data_m15.index[-1]
    if is_london_session_active(latest_candle_time_m15):
         comment += f"Активна Лондонская сессия ({latest_candle_time_m15.hour}:00 UTC). "
    else:
         comment += f"Лондонская сессия не активна ({latest_candle_time_m15.hour}:00 UTC). "


    # Возвращаем финальный сигнал и все статусы, включая swept_level_m15 и h1_context.
    return potential_signal, entry_price, stop_loss, take_profit, rr, status_sweep, status_of, status_target, comment, swept_level_m15, h1_context