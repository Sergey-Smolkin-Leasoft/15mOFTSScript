# backtester.py

import pandas as pd
import numpy as np
from datetime import datetime, timedelta, time as dt_time
import time
from utils.data_fetcher import fetch_historical_data_range
from strategy.strategy import generate_signal, calculate_rr, is_london_session_active
from config import (
    BACKTESTING_ASSET, BACKTESTING_START_DATE, BACKTESTING_END_DATE,
    INITIAL_CAPITAL, RISK_PER_TRADE_PERCENT, RISK_PER_TRADE_FIXED,
    TIMEFRAMES, LIQUIDITY_LOOKBACK, ASSUMED_PIP_VALUE_PER_LOT,
    DAILY_CHECK_TIME_UTC, LOOKBACK_WINDOW_FOR_SCAN,
    TIMEFRAME_DURATIONS_MINUTES
)

# Символы для вывода (дублируются для удобства)
CHECK = "✓"
CROSS = "✗"
ARROW = "→"

# Helper function to calculate position size based on risk
def calculate_position_size(account_balance, risk_percent, risk_fixed, stop_loss_pips, asset, assumed_pip_value_per_lot):
    """
    Рассчитывает размер позиции в лотах на основе заданного риска.
    """
    if stop_loss_pips is None or stop_loss_pips <= 0 or account_balance is None or account_balance <= 0 or assumed_pip_value_per_lot is None or assumed_pip_value_per_lot <= 0:
        return 0

    if risk_percent is not None:
        risk_amount = account_balance * (risk_percent / 100.0)
    elif risk_fixed is not None:
        risk_amount = risk_fixed
    else:
        return 0

    cost_per_pip_per_lot = assumed_pip_value_per_lot
    stop_loss_cost_per_lot = stop_loss_pips * cost_per_pip_per_lot

    if stop_loss_cost_per_lot is None or stop_loss_cost_per_lot <= 0:
        return 0

    volume_in_lots = risk_amount / stop_loss_cost_per_lot

    min_lot_size = 0.01
    volume_in_lots = max(0.0, round(volume_in_lots / min_lot_size) * min_lot_size)

    return volume_in_lots

# Helper to calculate pips from price difference
def calculate_pips(price1, price2, asset):
     """Calculates the difference between two prices in pips."""
     if price1 is None or price2 is None:
         return 0.0
     pip_decimal_places = 4
     if "JPY" in asset:
         pip_decimal_places = 2

     pip_factor = 10 ** (-pip_decimal_places)

     return abs(price1 - price2) / pip_factor


class Backtester:
    def __init__(self, asset, start_date, end_date, initial_capital, risk_percent, risk_fixed, assumed_pip_value_per_lot, daily_check_time_str, lookback_window_for_scan: timedelta):
        self.asset = asset
        self.start_date = pd.to_datetime(start_date)
        self.end_date = pd.to_datetime(end_date)
        self.initial_capital = initial_capital
        self.current_balance = initial_capital
        self.risk_percent = risk_percent
        self.risk_fixed = risk_fixed
        self.assumed_pip_value_per_lot = assumed_pip_value_per_lot

        self.trades = []
        self.open_position = None

        self.balance_history = [initial_capital]
        self.peak_balance = initial_capital
        self.max_drawdown = 0

        try:
            check_hour, check_minute, check_second = map(int, daily_check_time_str.split(':'))
            self.daily_check_time_obj = dt_time(check_hour, check_minute, check_second)
        except ValueError as e:
             raise ValueError(f"Неверный формат DAILY_CHECK_TIME_UTC в config.py: {e}")

        self.lookback_window_for_scan = lookback_window_for_scan

        m15_interval_minutes = TIMEFRAME_DURATIONS_MINUTES.get('M15')
        if m15_interval_minutes is None or m15_interval_minutes <= 0:
             raise ValueError("Неверно задана длительность M15 таймфрейма в минутах в TIMEFRAME_DURATIONS_MINUTES config.py")
        self.m15_timedelta = timedelta(minutes=m15_interval_minutes)

        # Минимальное количество свечей H1 и M15, необходимых для работы стратегии (на основе LIQUIDITY_LOOKBACK)
        self.min_h1_for_strategy_logic = LIQUIDITY_LOOKBACK * 2
        # Минимальное количество свечей M15 для поиска ликвидности M15 и Order Flow
        self.min_m15_for_strategy_logic = (LIQUIDITY_LOOKBACK // 2) * 2 + 5 # Примерно


    def run(self):
        print(f"Запуск бэктеста для {self.asset} с {self.start_date} по {self.end_date}")
        print(f"Ежедневная проверка стратегии в {self.daily_check_time_obj.strftime('%H:%M:%S')} UTC с окном lookback {self.lookback_window_for_scan}")

        # Загрузка ВСЕХ исторических данных за период + запас для lookback
        # Увеличим запас еще больше, чтобы точно хватило данных в начале для lookback окна
        fetch_start_date = self.start_date - self.lookback_window_for_scan - timedelta(days=14) # Увеличенный запас
        fetch_end_date = self.end_date + self.m15_timedelta

        data_h1_full = fetch_historical_data_range(self.asset, "H1", fetch_start_date.strftime('%Y-%m-%d %H:%M:%S'), fetch_end_date.strftime('%Y-%m-%d %H:%M:%S'))
        data_m15_full = fetch_historical_data_range(self.asset, "M15", fetch_start_date.strftime('%Y-%m-%d %H:%M:%S'), fetch_end_date.strftime('%Y-%m-%d %H:%M:%S'))


        if data_h1_full is None or data_m15_full is None or data_m15_full.empty or data_h1_full.empty:
            print("Не удалось загрузить все необходимые исторические данные. Бэктест отменен.")
            return

        actual_data_start = data_m15_full.index.min()
        actual_data_end = data_m15_full.index.max()
        print(f"Фактически загруженный диапазон данных M15: {actual_data_start} - {actual_data_end}")


        trading_days_in_data = data_m15_full.index.normalize().unique()
        trading_days = trading_days_in_data.sort_values()

        if trading_days.empty:
             print("Нет загруженных данных M15. Бэктест отменен.")
             return

        print(f"Найдено {len(trading_days)} дней в загруженных данных для потенциальной проверки.")

        last_check_time = actual_data_start # Время последней проверки SL/TP или начала данных


        for trading_day in trading_days:
            current_check_time = trading_day + timedelta(hours=self.daily_check_time_obj.hour,
                                                        minutes=self.daily_check_time_obj.minute,
                                                        seconds=self.daily_check_time_obj.second)

            # Проверяем, находится ли время проверки в пределах фактического диапазона бэктеста (self.start_date - self.end_date)
            # И убедимся, что есть достаточно данных для lookback перед check_time в полной загруженной истории
            required_data_start_for_scan = current_check_time - self.lookback_window_for_scan
            if current_check_time < self.start_date or current_check_time > self.end_date or required_data_start_for_scan < actual_data_start:
                # print(f"\nВремя проверки {current_check_time} вне диапазона бэктеста или недостаточно данных для lookback в полной истории. Пропускаем день.")
                last_check_time = current_check_time # Обновляем last_check_time даже если пропускаем день
                continue

            # --- Разделитель дня ---
            print(f"\n{'-'*40}")
            print(f"--- Проверка дня: {trading_day.strftime('%Y-%m-%d')} ({current_check_time.strftime('%H:%M:%S')} UTC) ---")
            print(f"{'-'*40}")


            # --- Обработка открытой позиции (проверка SL/TP с last_check_time до current_check_time) ---
            if self.open_position:
                print(f"\n  [{ARROW}] Проверка открытой позиции для {self.open_position['asset']} ({self.open_position['direction']})...")
                pos = self.open_position
                check_sl_tp_start_time = last_check_time

                data_for_sl_tp_check = data_m15_full.loc[(data_m15_full.index > check_sl_tp_start_time) & (data_m15_full.index <= current_check_time)].copy()

                if not data_for_sl_tp_check.empty:
                    print(f"    [{ARROW}] Проверка SL/TP на свечах с {data_for_sl_tp_check.index.min()} по {data_for_sl_tp_check.index.max()}...")
                    for candle_time, candle_data in data_for_sl_tp_check.iterrows():
                        current_high = candle_data['High']
                        current_low = candle_data['Low']

                        hit_sl = False
                        hit_tp = False
                        exit_price = None
                        exit_reason = None

                        if pos['direction'] == 'BUY':
                            if current_low <= pos['sl']:
                                hit_sl = True
                                exit_price = pos['sl']
                                exit_reason = 'SL'
                            elif current_high >= pos['tp']:
                                hit_tp = True
                                exit_price = pos['tp']
                                exit_reason = 'TP'
                        elif pos['direction'] == 'SELL':
                            if current_high >= pos['sl']:
                                hit_sl = True
                                exit_price = pos['sl']
                                exit_reason = 'SL'
                            elif current_low <= pos['tp']:
                                hit_tp = True
                                exit_price = pos['tp']
                                exit_reason = 'TP'

                        if hit_sl or hit_tp:
                            pips_profit = calculate_pips(exit_price, pos['entry_price'], self.asset)
                            if pos['direction'] == 'SELL':
                                pips_profit *= -1

                            pnl_account_currency = pips_profit * self.assumed_pip_value_per_lot * pos['volume_lots']

                            self.current_balance += pnl_account_currency
                            self.balance_history.append(self.current_balance)

                            if self.current_balance > self.peak_balance:
                                 self.peak_balance = self.current_balance
                            drawdown = self.peak_balance - self.current_balance
                            if drawdown > self.max_drawdown:
                                 self.max_drawdown = drawdown

                            trade_result = {
                                'asset': self.asset,
                                'direction': pos['direction'],
                                'entry_time': pos['entry_time'],
                                'entry_price': pos['entry_price'],
                                'exit_time': candle_time,
                                'exit_price': exit_price,
                                'sl': pos['sl'],
                                'tp': pos['tp'],
                                'volume_lots': pos['volume_lots'],
                                'pips': pips_profit,
                                'pnl_account_currency': pnl_account_currency,
                                'exit_reason': exit_reason,
                                'balance_after': self.current_balance
                            }
                            self.trades.append(trade_result)
                            self.open_position = None
                            print(f"    [{CHECK if pnl_account_currency > 0 else CROSS}] Позиция закрыта ({trade_result['exit_reason']}) на {trade_result['exit_time']}. PnL: {trade_result['pnl_account_currency']:.2f}. Баланс: {self.current_balance:.2f}")
                            break
                    if self.open_position:
                         print(f"    [{ARROW}] Позиция остается открытой.")


            if self.open_position is None:
                print(f"\n  [{ARROW}] Поиск нового сигнала на {current_check_time}...")
                scan_window_start = current_check_time - self.lookback_window_for_scan
                scan_window_end = current_check_time

                data_m15_scan_window = data_m15_full.loc[(data_m15_full.index > scan_window_start) & (data_m15_full.index <= scan_window_end)].copy()
                data_h1_scan_window = data_h1_full.loc[(data_h1_full.index > scan_window_start) & (data_h1_full.index <= scan_window_end)].copy()

                print(f"    [{ARROW}] Сканируемое окно:")
                print(f"      [{ARROW}] Время: {scan_window_start} - {scan_window_end}")
                print(f"      [{ARROW}] Кол-во свечей: M15={len(data_m15_scan_window)}, H1={len(data_h1_scan_window)}")
                if not data_m15_scan_window.empty:
                    print(f"      [{ARROW}] Срез M15: {data_m15_scan_window.index.min()} - {data_m15_scan_window.index.max()}")
                if not data_h1_scan_window.empty:
                     print(f"      [{ARROW}] Срез H1: {data_h1_scan_window.index.min()} - {data_h1_scan_window.index.max()}")


                is_data_sufficient = len(data_h1_scan_window) >= self.min_h1_for_strategy_logic and len(data_m15_scan_window) >= self.min_m15_for_strategy_logic

                if not is_data_sufficient:
                    comment = f"Недостаточно данных в окне сканирования для стратегии (Требуется H1>={self.min_h1_for_strategy_logic}, M15>={self.min_m15_for_strategy_logic}). Пропускаем сигнал для этого дня."
                    print(f"    [{CROSS}] {comment}")
                    signal = 'NONE'
                    entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, swept_level_m15, h1_context = None, None, None, None, False, 'NONE', False, None, 'NEUTRAL'
                else:
                    print(f"    [{CHECK}] Данных в окне сканирования достаточно.")
                    signal, entry, sl, tp, rr_val, status_sweep_m15, status_of_m15, status_target_m15, comment, swept_level_m15, h1_context = generate_signal(
                        self.asset, data_h1_scan_window, data_m15_scan_window
                    )

                    if signal != 'NONE' and entry is not None and sl is not None and tp is not None:
                        print(f"\n  [{ARROW}] Попытка открытия позиции...")
                        entry_candle_time = data_m15_full.index[data_m15_full.index > current_check_time].min()

                        if pd.isna(entry_candle_time) or entry_candle_time > self.end_date:
                             print(f"    [{CROSS}] Нет данных M15 после {current_check_time} или за пределами бэктеста для имитации входа. Сигнал сгенерирован, но вход не выполнен.")
                             signal = 'NONE'
                             entry, sl, tp, rr_val = None, None, None, None

                        else:
                            entry_price_actual = data_m15_full.loc[entry_candle_time]['Open']
                            print(f"    [{ARROW}] Имитация входа по цене открытия {entry_price_actual:.5f} на свече {entry_candle_time}")

                            calculated_stop_loss_pips = calculate_pips(entry, sl, self.asset)

                            if calculated_stop_loss_pips is None or calculated_stop_loss_pips <= 0:
                                 print(f"    [{CROSS}] Рассчитанный размер стопа в пипсах некорректен ({calculated_stop_loss_pips}). Вход не выполнен.")
                                 signal = 'NONE'
                                 entry, sl, tp, rr_val = None, None, None, None

                            else:
                                pip_value_unit = (0.0001 if "JPY" not in self.asset else 0.01)
                                if signal == 'BUY':
                                    sl_actual = entry_price_actual - calculated_stop_loss_pips * pip_value_unit
                                    calculated_take_profit_pips = calculate_pips(entry, tp, self.asset) * (1 if tp > entry else -1)
                                    tp_actual = entry_price_actual + calculated_take_profit_pips * pip_value_unit
                                elif signal == 'SELL':
                                    sl_actual = entry_price_actual + calculated_stop_loss_pips * pip_value_unit
                                    calculated_take_profit_pips = calculate_pips(entry, tp, self.asset) * (1 if tp < entry else -1)
                                    tp_actual = entry_price_actual + calculated_take_profit_pips * pip_value_unit


                                if (signal == 'BUY' and (sl_actual is None or entry_price_actual is None or tp_actual is None or sl_actual >= entry_price_actual or tp_actual <= entry_price_actual)) or \
                                   (signal == 'SELL' and (sl_actual is None or entry_price_actual is None or tp_actual is None or sl_actual <= entry_price_actual or tp_actual >= entry_price_actual)):
                                     print(f"    [{CROSS}] Некорректные фактические уровни SL/TP после имитации входа по {entry_price_actual:.5f}. Сигнал отменен.")
                                     signal = 'NONE'
                                     entry, sl, tp, rr_val = None, None, None, None

                                else:
                                    rr_actual = calculate_rr(entry_price_actual, sl_actual, tp_actual, signal, self.asset)
                                    if rr_actual is None or rr_actual < MIN_RR:
                                         print(f"    [{CROSS}] Фактический RR ({rr_actual:.2f}) после имитации входа ниже MIN_RR ({MIN_RR}). Сигнал отменен.")
                                         signal = 'NONE'
                                         entry, sl, tp, rr_val = None, None, None, None
                                    else:
                                        stop_loss_pips_actual = calculate_pips(entry_price_actual, sl_actual, self.asset)
                                        volume_lots = calculate_position_size(
                                            self.current_balance,
                                            self.risk_percent,
                                            self.risk_fixed,
                                            stop_loss_pips_actual,
                                            self.asset,
                                            self.assumed_pip_value_per_lot
                                        )

                                        if volume_lots is None or volume_lots <= 0:
                                             print(f"    [{CROSS}] Рассчитан нулевой или отрицательный объем лотов ({volume_lots:.2f}). Вход не выполнен.")
                                             signal = 'NONE'
                                             entry, sl, tp, rr_val = None, None, None, None
                                        else:
                                            self.open_position = {
                                                'asset': self.asset,
                                                'direction': signal,
                                                'entry_time': entry_candle_time,
                                                'entry_price': entry_price_actual,
                                                'sl': sl_actual,
                                                'tp': tp_actual,
                                                'volume_lots': volume_lots,
                                                'last_check_time': current_check_time
                                            }
                                            print(f"    [{CHECK}] Позиция открыта: {signal} {volume_lots:.2f} лотов по {entry_price_actual:.5f} на {entry_candle_time}. SL: {sl_actual:.5f}, TP: {tp_actual:.5f}. Фактический RR: {rr_actual:.2f}")

            if signal == 'NONE':
                 print("  [{CROSS}] Сигнал для открытия позиции НЕ СГЕНЕРИРОВАН на этом дне.")


            last_check_time = current_check_time

            if self.current_balance > self.peak_balance:
                 self.peak_balance = self.current_balance
            drawdown = self.peak_balance - self.current_balance
            if drawdown > self.max_drawdown:
                 self.max_drawdown = drawdown


        print(f"\n{'-'*40}")


        print(f"\n{'-'*40}")
        print(f"--- Завершение бэктеста. Проверка последней позиции... ---")
        print(f"{'-'*40}")
        if self.open_position:
             print(f"  [{ARROW}] Проверка открытой позиции для {self.open_position['asset']} ({self.open_position['direction']}) до конца данных...")
             pos = self.open_position
             check_sl_tp_start_time = last_check_time

             data_for_sl_tp_check = data_m15_full.loc[(data_m15_full.index > check_sl_tp_start_time)].copy()

             position_closed_at_end = False
             if not data_for_sl_tp_check.empty:
                 print(f"    [{ARROW}] Проверка SL/TP на свечах с {data_for_sl_tp_check.index.min()} по {data_for_sl_tp_check.index.max()}...")
             for candle_time, candle_data in data_for_sl_tp_check.iterrows():
                 current_high = candle_data['High']
                 current_low = candle_data['Low']

                 hit_sl = False
                 hit_tp = False
                 exit_price = None
                 exit_reason = None

                 if pos['direction'] == 'BUY':
                     if current_low <= pos['sl']:
                         hit_sl = True
                         exit_price = pos['sl']
                         exit_reason = 'SL'
                     elif current_high >= pos['tp']:
                         hit_tp = True
                         exit_price = pos['tp']
                         exit_reason = 'TP'
                 elif pos['direction'] == 'SELL':
                     if current_high >= pos['sl']:
                         hit_sl = True
                         exit_price = pos['sl']
                         exit_reason = 'SL'
                     elif current_low <= pos['tp']:
                         hit_tp = True
                         exit_price = pos['tp']
                         exit_reason = 'TP'

                 if hit_sl or hit_tp:
                     pips_profit = calculate_pips(exit_price, pos['entry_price'], self.asset)
                     if pos['direction'] == 'SELL':
                         pips_profit *= -1

                     pnl_account_currency = pips_profit * self.assumed_pip_value_per_lot * pos['volume_lots']

                     self.current_balance += pnl_account_currency
                     self.balance_history.append(self.current_balance)

                     if self.current_balance > self.peak_balance:
                          self.peak_balance = self.current_balance
                     drawdown = self.peak_balance - self.current_balance
                     if drawdown > self.max_drawdown:
                          self.max_drawdown = drawdown

                     trade_result = {
                         'asset': self.asset,
                         'direction': pos['direction'],
                         'entry_time': pos['entry_time'],
                         'entry_price': pos['entry_price'],
                         'exit_time': candle_time,
                         'exit_price': exit_price,
                         'sl': pos['sl'],
                         'tp': pos['tp'],
                         'volume_lots': pos['volume_lots'],
                         'pips': pips_profit,
                         'pnl_account_currency': pnl_account_currency,
                         'exit_reason': exit_reason,
                         'balance_after': self.current_balance
                     }
                     self.trades.append(trade_result)
                     self.open_position = None
                     position_closed_at_end = True
                     print(f"    [{CHECK if pnl_account_currency > 0 else CROSS}] Позиция закрыта ({trade_result['exit_reason']}) на {trade_result['exit_time']}. PnL: {trade_result['pnl_account_currency']:.2f}. Баланс: {self.current_balance:.2f}")
                     break

             if self.open_position and not position_closed_at_end:
                 print(f"  [{ARROW}] Позиция осталась открытой до конца бэктеста.")
                 if not data_m15_full.empty:
                     last_candle = data_m15_full.iloc[-1]
                     exit_price = last_candle['Close']
                     self.open_position = None
                     print(f"    [{CROSS}] Позиция закрыта по концу бэктеста на {last_candle.index}. PnL: {exit_price - self.open_position['entry_price']:.2f}. Баланс: {self.current_balance:.2f}")
             else:
                  print(f"    [{ARROW}] Нет свечей M15 для проверки SL/TP после последней проверки. Позиция остается открытой (будет закрыта по концу бэктеста).")

        self.report_results()


    def report_results(self):
        """Выводит отчет о результатах бэктеста."""
        total_trades = len(self.trades)
        print("\n" + "="*50)
        print("              --- Финальные Результаты Бэктеста ---")
        print("="*50)
        print(f"Актив: {self.asset}")
        print(f"Период: {self.start_date.strftime('%Y-%m-%d %H:%M:%S')} - {self.end_date.strftime('%Y-%m-%d %H:%M:%S')}")
        print(f"Ежедневная проверка в: {self.daily_check_time_obj.strftime('%H:%M:%S')} UTC")
        print(f"Окно Lookback для сканирования: {self.lookback_window_for_scan}")
        print("-" * 50)
        print(f"Начальный капитал: {self.initial_capital:.2f}")
        print(f"Конечный капитал: {self.current_balance:.2f}")
        net_profit = self.current_balance - self.initial_capital
        print(f"Чистая прибыль (в валюте счета): {net_profit:.2f} ({'+' if net_profit >= 0 else ''}{net_profit/self.initial_capital*100:.2f}%)")
        print("-" * 50)
        print(f"Всего сделок: {total_trades}")

        if total_trades > 0:
            winning_trades = [t for t in self.trades if t['pnl_account_currency'] > 0]
            losing_trades = [t for t in self.trades if t['pnl_account_currency'] <= 0]

            total_profit_pips = sum(t['pips'] for t in self.trades)
            total_profit_currency = sum(t['pnl_account_currency'] for t in self.trades)

            win_rate = len(winning_trades) / total_trades * 100

            total_wins_currency = sum(t['pnl_account_currency'] for t in winning_trades)
            total_losses_currency = abs(sum(t['pnl_account_currency'] for t in losing_trades))

            profit_factor = total_wins_currency / total_losses_currency if total_losses_currency > 0 else float('inf')

            print(f"Прибыльных сделок: {len(winning_trades)}")
            print(f"Убыточных сделок: {len(losing_trades)}")
            print(f"Процент выигрышей: {win_rate:.2f}%")
            print("-" * 50)
            print(f"Общая прибыль (в валюте счета): {total_profit_currency:.2f}")
            print(f"Общая прибыль (Pips): {total_profit_pips:.2f}")
            print(f"Профит-фактор (в валюте счета): {profit_factor:.2f}" if profit_factor != float('inf') else "Профит-фактор: Inf (нет убыточных сделок)")
            print("-" * 50)
            print(f"Максимальная просадка (в валюте счета): {self.max_drawdown:.2f} ({self.max_drawdown/self.initial_capital*100:.2f}%)" if self.initial_capital > 0 else f"Максимальная просадка (в валюте счета): {self.max_drawdown:.2f}")

            print("\nСписок завершенных сделок:")
            trades_df = pd.DataFrame(self.trades)
            display_cols = ['entry_time', 'exit_time', 'direction', 'entry_price', 'exit_price', 'sl', 'tp', 'volume_lots', 'pips', 'pnl_account_currency', 'exit_reason', 'balance_after']
            trades_df = trades_df.sort_values(by='entry_time')
            print(trades_df[display_cols].to_string(index=False))
        else:
             print("Нет завершенных сделок за период бэктеста.")

        print("="*50)


if __name__ == "__main__":
    print("Запуск скрипта бэктестинга...")

    if RISK_PER_TRADE_PERCENT is None and RISK_PER_TRADE_FIXED is None:
         print("Ошибка: В config.py должен быть задан либо RISK_PER_TRADE_PERCENT, либо RISK_PER_TRADE_FIXED.")
    elif RISK_PER_TRADE_PERCENT is not None and RISK_PER_TRADE_FIXED is not None:
         print("Предупреждение: В config.py заданы оба типа риска. Будет использоваться RISK_PER_TRADE_PERCENT.")
         RISK_PER_TRADE_FIXED = None

    if "H1" not in TIMEFRAME_DURATIONS_MINUTES or "M15" not in TIMEFRAME_DURATIONS_MINUTES:
         print("Ошибка конфигурации: В config.py не заданы длительности таймфреймов 'H1' или 'M15' в словаре TIMEFRAME_DURATIONS_MINUTES.")
    elif TIMEFRAME_DURATIONS_MINUTES['M15'] is None or TIMEFRAME_DURATIONS_MINUTES['M15'] <= 0 or TIMEFRAME_DURATIONS_MINUTES['H1'] is None or TIMEFRAME_DURATIONS_MINUTES['H1'] <= 0:
         print("Ошибка конфигурации: Длительности таймфреймов в TIMEFRAME_DURATIONS_MINUTES должны быть положительными числами.")
    elif not isinstance(LOOKBACK_WINDOW_FOR_SCAN, timedelta):
         print("Ошибка конфигурации: LOOKBACK_WINDOW_FOR_SCAN в config.py должен быть типа timedelta.")
    else:
        backtester = Backtester(
            asset=BACKTESTING_ASSET,
            start_date=BACKTESTING_START_DATE,
            end_date=BACKTESTING_END_DATE,
            initial_capital=INITIAL_CAPITAL,
            risk_percent=RISK_PER_TRADE_PERCENT,
            risk_fixed=RISK_PER_TRADE_FIXED,
            assumed_pip_value_per_lot=ASSUMED_PIP_VALUE_PER_LOT,
            daily_check_time_str=DAILY_CHECK_TIME_UTC,
            lookback_window_for_scan=LOOKBACK_WINDOW_FOR_SCAN
        )
        backtester.run()
