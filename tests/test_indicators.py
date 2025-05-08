# tests/test_indicators.py

import pytest
import pandas as pd
from strategy.indicators import find_liquidity_levels, find_imbalances
from config import LIQUIDITY_LOOKBACK, IMBALANCE_THRESHOLD

# Используем фикстуры из conftest.py

def test_find_liquidity_levels(sample_data_m15_buy_setup):
    """Тест определения Swing High/Low."""
    highs, lows = find_liquidity_levels(sample_data_m15_buy_setup, lookback=LIQUIDITY_LOOKBACK)

    print("\nTest Highs:", highs)
    print("Test Lows:", lows)

    # С LIQUIDITY_LOOKBACK=10, должны найти Swing Low в районе индексов 9-14 (цены ~0.999 - 1.0005)
    # и Swing High где-то раньше.
    # Проверяем, что найден хотя бы один Swing Low в ожидаемом диапазоне цен/индексов.
    # Убедись, что эти индексы и цены соответствуют твоей ФИНАЛЬНОЙ версии sample_data_m15_buy_setup!
    assert any(l[1] < 1.001 and l[1] > 0.998 for l in lows) # Проверяем наличие низких минимумов
    assert len(lows) > 0 # Должен найти хотя бы один лоу

    # Проверяем, что найден хотя бы один хай (если данных достаточно и структура позволяет)
    assert len(highs) > 0 # Должен найти хотя бы один хай


def test_find_imbalances(sample_data_m15_buy_setup):
    """Тест определения имбалансов (FVG)."""
    imbalances = find_imbalances(sample_data_m15_buy_setup, threshold=IMBALANCE_THRESHOLD)

    print("\nTest Imbalances:", imbalances)

    # На основе фикстуры sample_data_m15_buy_setup мы ожидаем бычий FVG около индекса 21
    # между High[22] (1.0038) и Low[20] (1.0040). Индекс FVG = 20. Границы ~1.0038-1.0040
    # Проверяем, что найден бычий имбаланс с ожидаемыми параметрами.
    # Убедись, что эти индексы и границы соответствуют твоей ФИНАЛЬНОЙ версии sample_data_m15_buy_setup!
    expected_fvg_idx = sample_data_m15_buy_setup.index[20]
    expected_fvg_low = 1.0038
    expected_fvg_high = 1.0040

    # Проверяем, что найден имбаланс с нужным индексом, типом и примерными границами
    assert any(
        imb[0] == expected_fvg_idx and
        imb[3] == 'bullish' and
        abs(imb[1] - expected_fvg_low) < 1e-9 and # Сравниваем границы с допуском
        abs(imb[2] - expected_fvg_high) < 1e-9
        for imb in imbalances
    )

    # Проверяем, что найден хотя бы один имбаланс
    assert len(imbalances) > 0