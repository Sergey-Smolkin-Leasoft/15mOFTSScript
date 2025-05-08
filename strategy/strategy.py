# strategy/strategy.py

import pandas as pd
import numpy as np
from datetime import datetime, time
import pytz
from .indicators import find_liquidity_levels, find_imbalances
from config import (
    LIQUIDITY_LOOKBACK, MIN_RR, IMBALANCE_THRESHOLD,
    # Оставляем LONDON_... в импортах, если is_london_session_active их использует
    LONDON_OPEN_HOUR, LONDON_CLOSE_HOUR
)

def calculate_rr(entry, sl, target):
    """Расчет Reward/Risk."""
    risk = abs(entry - sl)
    reward = abs(target - entry)
    if risk == 0 or risk is None or reward is None:
        return 0.0
    # Используем явное преобразование для надежности
    return float(reward) / float(risk) if risk != 0 else 0.0 # Добавим проверку деления на ноль

def is_london_session_active(dt_index):
    """Проверяет, приходится ли время свечи на активную Лондонскую сессию (UTC)."""
    # Предполагаем, что dt_index уже в UTC или наивный и сравниваем часы напрямую
    # Если yfinance/twelvedata дает данные в другой TZ, нужна конвертация
    if dt_index is None: return False # Добавим проверку на None
    try:
        # Попробуем получить час, если индекс является временной меткой
        dt_utc = dt_index
        if hasattr(dt_utc, 'hour'):
            if dt_utc.hour >= LONDON_OPEN_HOUR and dt_utc.hour < LONDON_CLOSE_HOUR:
                return True
            return False
        # Если это не временная метка с часом, или другой формат
        # Можно попытаться преобразовать, если нужно, но пока оставим просто False
        return False
    except Exception:
        return False


# check_order_flow остается без изменений, так как работает с переданными данными
def check_order_flow(data: pd.DataFrame, direction: str, recent_sweep_index):
    """
    Базовая проверка на формирование Order Flow после снятия ликвидности.
    Очень упрощенная реализация!
    Логика: после свипа, ищем пробой локального фракатал в направлении OF
    и наличие/тест имбаланса в этом же направлении.
    Возвращает True, если OF подтвержден, иначе False.
    """
    if recent_sweep_index is None or data is None or data.empty:
        return False

    # Берем данные после свипа
    # Убедимся, что recent_sweep_index существует в индексе данных
    if recent_sweep_index not in data.index:
        # Это может случиться, если recent_sweep_idx находится вне num_candles
        # которое передается в generate_signal, если num_candles меньше, чем глубина свипа.
        # В текущей версии num_candles берется из TD_OUTPUT_SIZES['M15'],
        # которая должна быть достаточно большой (500). Но добавим проверку.
        comment_check_of = f"recent_sweep_index {recent_sweep_idx} не найден в индексе данных."
        # print(f"DEBUG check_order_flow: {comment_check_of}") # Для отладки
        return False # OF не может быть подтвержден, если свечи свипа нет в данных


    data_after_sweep = data.loc[recent_sweep_index:].copy()

    if len(data_after_sweep) < 3: # Нужно хотя бы несколько свечей после свипа
        # print(f"DEBUG check_order_flow: Недостаточно свечей после свипа ({len(data_after_sweep)}).") # Для отладки
        return False

    # Ищем локальные фракталы после свипа
    # Lookback для OF может быть меньше, чем для глобальной ликвидности
    of_lookback = min(LIQUIDITY_LOOKBACK, len(data_after_sweep) // 2) # Используем половину доступных свечей, минимум 1
    of_lookback = max(1, of_lookback) # Минимальный lookback = 1
    highs_after, lows_after = find_liquidity_levels(data_after_sweep, lookback=of_lookback)

    # Ищем имбалансы после свипа (имбалансы будут искаться по всем данным after_sweep)
    imbalances_after = find_imbalances(data_after_sweep, threshold=IMBALANCE_THRESHOLD)

    # --- Упрощенная логика проверки OF ---
    # Ищем пробой локального фракатал В НАПРАВЛЕНИИ свипа/OF
    # Ищем подходящий имбаланс ПОСЛЕ пробоя, который может служить зоной входа

    of_confirmed = False

    # Проверка на пробой структуры (CHoCH)
    structure_broken = False
    if direction == 'bullish': # Ищем бычий OF после снятия SSL (движение вверх) -> пробой локального хая
        # Ищем пробой локального максимума после свипа
        recent_highs_after_sweep_levels = [level for idx, level in highs_after] # Хаи после свипа

        # Ищем любую свечу после свипа, которая закрылась ВЫШЕ любого локального хая после свипа
        if recent_highs_after_sweep_levels:
            highest_high_after_sweep_levels = max(recent_highs_after_sweep_levels)
            # Проверяем, было ли закрытие выше самого высокого локального хая после свипа
            if data_after_sweep['Close'].max() > highest_high_after_sweep_levels:
                 structure_broken = True
                 # print(f"DEBUG check_order_flow: Бычий пробой структуры выше {highest_high_after_sweep_levels:.5f}.") # Для отладки


    elif direction == 'bearish': # Ищем медвежий OF после снятия BSL (движение вниз) -> пробой локального лоя
        # Ищем пробой локального минимума после свипа
        recent_lows_after_sweep_levels = [level for idx, level in lows_after] # Лоу после свипа
        if recent_lows_after_sweep_levels:
             lowest_low_after_sweep_levels = min(recent_lows_after_sweep_levels)
             # Проверяем, было ли закрытие ниже самого низкого локального лоя после свипа
             if data_after_sweep['Close'].min() < lowest_low_after_sweep_levels:
                  structure_broken = True
                  # print(f"DEBUG check_order_flow: Медвежий пробой структуры ниже {lowest_low_after_sweep_levels:.5f}.") # Для отладки

    # Если структура сломана в нужном направлении, ищем имбаланс ПОСЛЕ пробоя
    # (Очень упрощено: просто ищем имбаланс после свипа в нужном направлении)
    # Более строго: нужно найти имбаланс, сформированный ПОСЛЕ свечи, сделавшей пробой структуры
    if structure_broken:
         relevant_imbalances_after_sweep = []
         if direction == 'bullish':
              relevant_imbalances_after_sweep = [imb for imb in imbalances_after if imb[3] == 'bullish']
         elif direction == 'bearish':
              relevant_imbalances_after_sweep = [imb for imb in imbalances_after if imb[3] == 'bearish']

         # Если есть пробой структуры и подходящий имбаланс после свипа, считаем OF подтвержденным
         if relevant_imbalances_after_sweep:
              of_confirmed = True
              # print(f"DEBUG check_order_flow: OF подтвержден (пробой структуры + имбаланс).") # Для отладки
         # else:
              # print(f"DEBUG check_order_flow: Пробой структуры был, но нет подходящего имбаланса после свипа.") # Для отладки

    # else:
        # print(f"DEBUG check_order_flow: Пробой структуры не обнаружен.") # Для отладки


    return of_confirmed


# --- ИЗМЕНЕНА ПОДПИСЬ ФУНКЦИИ: принимает только data_m15 ---
def generate_signal(asset: str, data_m15: pd.DataFrame | None):
# ----------------------------------------------------------
    """
    Генерирует торговый сигнал на основе стратегии 15mOF, используя только M15 данные.
    Возвращает: tuple (signal, entry, stop_loss, take_profit, rr, status_sweep, status_of, status_target, comment)
    signal: 'BUY', 'SELL', 'NONE'
    status_X: bool (True/False)
    """
    # Инициализируем статусы условий как False
    status_sweep = False
    # Статус OF теперь означает: OF подтвержден И вход по M15 имбалансу найден
    status_of = False
    status_target = False # Цель с RR >= 1.5 найдена

    potential_signal = 'NONE'
    entry_price = None
    stop_loss = None
    take_profit = None
    rr = None
    comment = ""
    recent_sweep_idx = None
    sweep_direction = None

    # Проверка на наличие данных M15
    if data_m15 is None or data_m15.empty or len(data_m15) < LIQUIDITY_LOOKBACK * 2:
         comment = f"Недостаточно данных M15 для анализа ликвидности ({len(data_m15) if data_m15 is not None else 0} свечей, нужно >={LIQUIDITY_LOOKBACK*2})."
         # Всегда возвращаем текущие статусы (все False на этом этапе)
         return 'NONE', None, None, None, None, status_sweep, status_of, status_target, comment

    # latest_price = data_m15['Close'].iloc[-1] # Переменная не используется дальше в этом блоке, можно удалить

    # Находим ликвидность и имбалансы на M15 по всем доступным данным
    highs_m15, lows_m15 = find_liquidity_levels(data_m15, lookback=LIQUIDITY_LOOKBACK)
    imbalances_m15 = find_imbalances(data_m15, threshold=IMBALANCE_THRESHOLD)


    # 1. Проверка на снятие SSL/BSL на M15
    # Находим последний хай/лоу для проверки свипа
    last_high = highs_m15[-1] if highs_m15 else None
    last_low = lows_m15[-1] if lows_m15 else None

    # Проверяем последние свечи на свип, ищем свечу, которая пробила последний фрактал
    # Смотрим только на свечи после последнего локального фракатал для эффективности
    start_idx_check_sweep = None
    if last_high: start_idx_check_sweep = last_high[0]
    if last_low:
        if start_idx_check_sweep is None or last_low[0] < start_idx_check_sweep:
            start_idx_check_sweep = last_low[0]

    # Если нет ни хаев, ни лоев, свипа быть не может
    if start_idx_check_sweep is None:
        comment += "Нет значимых Swing High/Low на M15 для определения свипа."
        return 'NONE', None, None, None, None, status_sweep, status_of, status_target, comment

    # Берем только недавние данные после последнего фракатал
    recent_candles_for_sweep_check = data_m15.loc[start_idx_check_sweep:].copy()

    sweep_candle_info = None # (Series свечи свипа, direction, swept_level)

    # Проверяем наличие пробоя последнего хая в недавних свечах
    if last_high:
         sweeping_candles_bearish = recent_candles_for_sweep_check[recent_candles_for_sweep_check['High'] > last_high[1]]
         if not sweeping_candles_bearish.empty:
              # Нашли свечу(и), которая пробила последний хай. Берем последнюю из них.
              sweep_candle_info = (sweeping_candles_bearish.iloc[-1], 'bearish', last_high[1])

    # Проверяем наличие пробоя последнего лоя в недавних свечах
    if sweep_candle_info is None and last_low: # Проверяем лой, только если не нашли свип хая
         sweeping_candles_bullish = recent_candles_for_sweep_check[recent_candles_for_sweep_check['Low'] < last_low[1]]
         if not sweeping_candles_bullish.empty:
              # Нашли свечу(и), которая пробила последний лой. Берем последнюю из них.
              sweep_candle_info = (sweeping_candles_bullish.iloc[-1], 'bullish', last_low[1])


    # Если нашли свечу свипа
    if sweep_candle_info:
        sweeping_candle_series, sweep_direction, swept_level = sweep_candle_info
        recent_sweep_idx = sweeping_candle_series.name # Индекс свечи, сделавшей свип
        status_sweep = True # Свип обнаружен

        if sweep_direction == 'bearish':
             potential_signal = 'SELL'
             comment += f"Снятие BSL ({swept_level:.5f}) на M15 свечой {recent_sweep_idx.strftime('%Y-%m-%d %H:%M')}. "
             stop_loss = sweeping_candle_series['High'] * 1.0005 # SL за хай свечи свипа
        elif sweep_direction == 'bullish':
             potential_signal = 'BUY'
             comment += f"Снятие SSL ({swept_level:.5f}) на M15 свечой {recent_sweep_idx.strftime('%Y-%m-%d %H:%M')}. "
             stop_loss = sweeping_candle_series['Low'] * 0.9995 # SL за лоу свечи свипа
        else: # Не должно произойти при текущей логике
            status_sweep = False # На всякий случай, если направление не определилось
            comment += "Обнаружен свип, но направление не определено. "


    if not status_sweep:
        comment += "Нет недавнего снятия ликвидности на M15. "
        return 'NONE', None, None, None, None, status_sweep, status_of, status_target, comment


    # --- Снятие ликвидности было. Продолжаем анализ только на M15 ---

    # 2. Проверка на формирование Order Flow на M15 и поиск входа по M15 имбалансу
    of_confirmed_m15 = False
    entry_found_via_of_imbalance = False # Флаг, что найдена точка входа по имбалансу

    # Проверяем M15 OF только если есть свип и направление
    if recent_sweep_idx is not None and sweep_direction is not None:
         # check_order_flow работает с полными данными и индексом свипа
         of_confirmed_m15 = check_order_flow(data_m15, sweep_direction, recent_sweep_idx)


    if of_confirmed_m15:
        comment += "M15 Order Flow подтвержден. "
        # OF подтвержден, теперь ищем точку входа по имбалансу M15
        # Ищем имбалансы, которые появились ПОСЛЕ свечи свипа
        imbalances_after_sweep = [imb for imb in imbalances_m15 if imb[0] >= recent_sweep_idx]


        relevant_imbalances_m15 = []
        if sweep_direction == 'bullish': # Ищем бычий имбаланс на M15 после свипа
             relevant_imbalances_m15 = [imb for imb in imbalances_after_sweep if imb[3] == 'bullish']
             # Ищем имбаланс, который находится ниже текущей цены (для LONG)
             # (Цена должна откатить в имбаланс)
             relevant_imbalances_m15 = [imb for imb in relevant_imbalances_m15 if imb[1] < data_m15.iloc[-1]['Close']] # imb_low < latest_price
        elif sweep_direction == 'bearish': # Ищем медвежий имбаланс на M15 после свипа
             relevant_imbalances_m15 = [imb for imb in imbalances_after_sweep if imb[3] == 'bearish']
             # Ищем имбаланс, который находится выше текущей цены (для SHORT)
             relevant_imbalances_m15 = [imb for imb in relevant_imbalances_m15 if imb[2] > data_m15.iloc[-1]['Close']] # imb_high > latest_price


        if relevant_imbalances_m15:
             # Берем самый ранний (первый после свипа) подходящий M15 имбаланс
             relevant_imbalances_m15.sort(key=lambda x: x[0])
             imb_idx, imb_low, imb_high, imb_type = relevant_imbalances_m15[0]
             # Точка входа - середина имбаланса
             entry_price = (imb_low + imb_high) / 2.0
             comment += f"Потенциальный вход на тесте M15 {imb_type} имбаланса ({imb_low:.5f}-{imb_high:.5f}) на {imb_idx.strftime('%Y-%m-%d %H:%M')}. "
             entry_found_via_of_imbalance = True # Флаг, что вход определен

        else:
             comment += "Не найден подходящий M15 имбаланс после свипа для входа при подтвержденном OF. "
             # entry_price остается None
             # entry_found_via_of_imbalance остается False


    # Обновляем статус_OF: он TRUE только если OF подтвержден И точка входа по имбалансу M15 найдена
    status_of = of_confirmed_m15 and entry_found_via_of_imbalance


    # Если статус OF не True ИЛИ stop_loss не определен (он определяется при свипе), дальше нет смысла
    if not status_of or stop_loss is None:
        if status_sweep: # Только если был свип
             comment += "Пропуск анализа цели: не выполнен OF на M15 или не найдена точка входа по M15 имбалансу."
        # Всегда возвращаем текущие статусы
        return 'NONE', entry_price, stop_loss, take_profit, rr, status_sweep, status_of, status_target, comment


    # --- Вход и SL определены по M15 OF/имбалансу. Продолжаем анализ. ---

    # 3. Определение цели и проверка RR
    # Находим все swing highs/lows на M15, которые произошли ПОСЛЕ свечи свипа
    recent_highs_after_sweep = [(idx, lvl) for idx, lvl in highs_m15 if recent_sweep_idx is not None and idx > recent_sweep_idx]
    recent_lows_after_sweep = [(idx, lvl) for idx, lvl in lows_m15 if recent_sweep_idx is not None and idx > recent_sweep_idx]

    target_found = False
    if potential_signal == 'BUY':
        # Ищем ближайший Swing High после свипа, который может служить целью для LONG
        recent_highs_after_sweep.sort(key=lambda x: x[1]) # Сортируем по цене от меньшей к большей
        for target_idx, potential_target in recent_highs_after_sweep:
             if potential_target > entry_price: # Цель должна быть ВЫШЕ входа
                 calculated_rr = calculate_rr(entry_price, stop_loss, potential_target)
                 if calculated_rr >= MIN_RR:
                     take_profit = potential_target
                     rr = calculated_rr
                     target_found = True
                     status_target = True # Цель найдена
                     comment += f"Цель найдена (High на {target_idx.strftime('%Y-%m-%d %H:%M')}) с ценой {take_profit:.5f} (RR = {rr:.2f} >= {MIN_RR}). "
                     break # Нашли первую подходящую цель

    elif potential_signal == 'SELL':
        # Ищем ближайший Swing Low после свипа, который может служить целью для SHORT
        recent_lows_after_sweep.sort(key=lambda x: x[1], reverse=True) # Сортируем по цене от большей к меньшей
        for target_idx, potential_target in recent_lows_after_sweep:
            if potential_target < entry_price: # Цель должна быть НИЖЕ входа
                 calculated_rr = calculate_rr(entry_price, stop_loss, potential_target)
                 if calculated_rr >= MIN_RR:
                     take_profit = potential_target
                     rr = calculated_rr
                     target_found = True
                     status_target = True # Цель найдена
                     comment += f"Цель найдена (Low на {target_idx.strftime('%Y-%m-%d %H:%M')}) с ценой {take_profit:.5f} (RR = {rr:.2f} >= {MIN_RR}). "
                     break # Нашли первую подходящую цель

    # Если цель не найдена, возвращаем NONE с соответствующим статусом и комментарием
    if not target_found:
        comment += f"Не удалось найти цель после свипа с RR >= {MIN_RR}. "
        # status_target остается False
        return 'NONE', entry_price, stop_loss, take_profit, rr, status_sweep, status_of, status_target, comment


    # --- Все основные условия соблюдены. Генерируем сигнал. ---

    # 4. Проверка условия Лондонской сессии (для комментария)
    latest_candle_time = data_m15.index[-1]
    if is_london_session_active(latest_candle_time):
         comment += f"Активна Лондонская сессия ({latest_candle_time.hour}:00 UTC). "
    else:
         comment += f"Лондонская сессия не активна ({latest_candle_time.hour}:00 UTC). "


    # Если мы дошли сюда, значит status_sweep, status_of, status_target = True
    # и entry, sl, tp, rr определены и rr >= MIN_RR.
    # Возвращаем финальный сигнал и все статусы.
    return potential_signal, entry_price, stop_loss, take_profit, rr, status_sweep, status_of, status_target, comment