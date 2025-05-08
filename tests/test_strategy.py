# tests/test_strategy.py

import pytest
import pandas as pd
from strategy.strategy import generate_signal, calculate_rr, is_london_session_active
from config import MIN_RR # Импортируем нужный конфиг

# Используем фикстуры из conftest.py
# Убедись, что фикстуры sample_data_m15_buy_setup, sample_data_m30_buy_setup,
# sample_data_h1_buy_setup, sample_data_m15_no_setup, sample_data_m15_not_enough_data
# определены в tests/conftest.py

def test_generate_signal_buy_setup(
    sample_data_h1_buy_setup,
    sample_data_m30_buy_setup,
    sample_data_m15_buy_setup # Используем фикстуру с настроенным BUY сигналом
):
    """Тест генерации BUY сигнала при наличии условий."""
    asset = "EUR/USD" # Используем фиктивный актив для теста
    signal, entry, sl, tp, rr_val, comment = generate_signal(
        asset, sample_data_h1_buy_setup, sample_data_m30_buy_setup, sample_data_m15_buy_setup
    )

    print("\n--- BUY Signal Test ---")
    print(f"Signal: {signal}")
    print(f"Entry: {entry}, SL: {sl}, TP: {tp}")
    print(f"RR: {rr_val:.2f}" if rr_val is not None else "RR: N/A")
    print(f"Comment: {comment}")
    print("-" * 25)

    # Проверяем, что сигнал сгенерирован и имеет правильное направление
    assert signal == 'BUY'

    # Проверяем, что определены уровни входа, стопа и цели
    assert entry is not None
    assert sl is not None
    assert tp is not None
    assert rr_val is not None

    # Проверяем основные условия: SL ниже Entry, TP выше Entry, RR >= MIN_RR
    assert sl < entry
    assert tp > entry
    assert rr_val >= MIN_RR

    # Можно добавить более точные проверки на основе вашей фикстуры,
    # например, что SL находится близко к лоу свечи свипа,
    # что вход близко к середине FVG и т.д.
    # assert abs(sl - expected_sweep_low_price) < tolerance
    # assert abs(entry - expected_fvg_midpoint) < tolerance


# Добавь тесты для других сценариев:

# def test_generate_signal_sell_setup(...):
#     """Тест генерации SELL сигнала."""
#     # Создай фикстуру sample_data_m15_sell_setup для этого теста
#     signal, entry, sl, tp, rr_val, comment = generate_signal(...)
#     assert signal == 'SELL'
#     assert entry is not None and sl is not None and tp is not None and rr_val is not None
#     assert sl > entry # SL выше entry для SELL
#     assert tp < entry # TP ниже entry для SELL
#     assert rr_val >= MIN_RR

def test_generate_signal_no_setup(
    sample_data_h1_buy_setup, # Можно использовать любые H1/M30 данные, если они не влияют на M15 setup
    sample_data_m30_buy_setup,
    sample_data_m15_no_setup # Используем фикстуру без условий для сигнала
):
    """Тест отсутствия сигнала при отсутствии условий."""
    asset = "EUR/USD"
    signal, entry, sl, tp, rr_val, comment = generate_signal(
        asset, sample_data_h1_buy_setup, sample_data_m30_buy_setup, sample_data_m15_no_setup
    )

    print("\n--- No Signal Test ---")
    print(f"Signal: {signal}")
    print(f"Comment: {comment}")
    print("-" * 25)

    assert signal == 'NONE'
    assert entry is None
    assert sl is None
    assert tp is None
    assert rr_val is None
    assert "Нет недавнего снятия ликвидности" in comment or "Не все условия" in comment or "Недостаточно данных" in comment # Проверка комментария


def test_generate_signal_not_enough_data(
    sample_data_h1_buy_setup,
    sample_data_m30_buy_setup,
    sample_data_m15_not_enough_data # Используем фикстуру с малым объемом данных
):
    """Тест отсутствия сигнала при недостатке данных."""
    asset = "EUR/USD"
    signal, entry, sl, tp, rr_val, comment = generate_signal(
        asset, sample_data_h1_buy_setup, sample_data_m30_buy_setup, sample_data_m15_not_enough_data
    )

    print("\n--- Not Enough Data Test ---")
    print(f"Signal: {signal}")
    print(f"Comment: {comment}")
    print("-" * 25)

    assert signal == 'NONE'
    assert entry is None
    assert sl is None
    assert tp is None
    assert rr_val is None
    assert "Недостаточно данных M15" in comment


# Добавь тесты для других частей стратегии, например:
# - Тест альтернативного входа на M30
# - Тест условия Лондонской сессии (возможно, как отдельная функция is_london_session_active)

def test_calculate_rr():
    """Тест функции calculate_rr."""
    assert calculate_rr(1.0, 0.9, 1.3) == 3.0 # Risk 0.1, Reward 0.3 -> RR 3.0
    assert calculate_rr(1.0, 1.1, 0.8) == 2.0 # Risk 0.1, Reward 0.2 -> RR 2.0
    assert calculate_rr(1.0, 1.0, 1.1) == 0.0 # Zero risk case
    assert calculate_rr(1.0, 0.9, 1.1) == 1.0 # Risk 0.1, Reward 0.1 -> RR 1.0

# Тест для функции is_london_session_active
# Нужно создать datetime объекты с разными часами и таймзонами
# import pytz
# from datetime import datetime
# def test_is_london_session_active():
#     london_tz = pytz.timezone('Europe/London')
#     utc_tz = pytz.utc
#
#     # Часы Лондона в UTC (например, 8:00 UTC до 17:00 UTC, зависит от DST)
#     # Лучше использовать константы из config или параметризовать
#     london_open_utc = 8 # Из config.py
#     london_close_utc = 17 # Из config.py
#
#     # Время внутри Лондонской сессии (UTC)
#     dt_in_london = datetime(2023, 10, 27, 12, 30, tzinfo=utc_tz)
#     assert is_london_session_active(dt_in_london) is True
#
#     # Время вне Лондонской сессии (UTC)
#     dt_before_london = datetime(2023, 10, 27, 6, 30, tzinfo=utc_tz)
#     assert is_london_session_active(dt_before_london) is False
#
#     dt_after_london = datetime(2023, 10, 27, 18, 30, tzinfo=utc_tz)
#     assert is_london_session_active(dt_after_london) is False
#
#     # Время ровно на границе (зависит от включения/исключения границы)
#     # dt_at_open = datetime(2023, 10, 27, london_open_utc, 0, tzinfo=utc_tz)
#     # assert is_london_session_active(dt_at_open) is True # Или False, как определено в функции
#
#     # dt_at_close = datetime(2023, 10, 27, london_close_utc, 0, tzinfo=utc_tz)
#     # assert is_london_session_active(dt_at_close) is False # Или True