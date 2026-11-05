import math

import numpy as np
import pandas as pd
import pytest
from fastapi.testclient import TestClient

from app import forecast as fc
from app.main import app

client = TestClient(app)


def history_payload(days=120, seed=7):
    df = fc.make_synthetic_demand(days=days, seed=seed)
    return [{"date": d.date().isoformat(), "quantity": q} for d, q in zip(df["date"], df["qty"])]


def test_wape_계산():
    assert fc.wape(np.array([10, 10]), np.array([9, 12])) == pytest.approx(3 / 20)


def test_백테스트는_미래_정보를_쓰지_않는다():
    """마지막 구간의 실제값을 완전히 바꿔도, 그 구간보다 앞선 예측(앞 fold)의 입력은 바뀌지 않아야 한다."""
    s = fc.to_daily_series(fc.make_synthetic_demand(days=200))
    seen = []

    def spy(train, horizon):
        seen.append(len(train))
        return fc.predict_seasonal_naive(train, horizon)

    fc.backtest(s, horizon=7, folds=4, models={"spy": spy})
    # fold마다 학습 데이터는 7일씩 늘어나야 하고, 항상 평가 구간 이전까지만 포함
    assert seen == [len(s) - 28, len(s) - 21, len(s) - 14, len(s) - 7]


def test_기준선보다_ML이_나쁘지_않다_합성데이터():
    s = fc.to_daily_series(fc.make_synthetic_demand(days=400, seed=7))
    r = fc.backtest(s, horizon=7, folds=8)
    assert r.wape["gbr"] < r.wape["seasonal_naive"] < r.wape["naive"]


def test_빠진_날짜는_0으로_채운다():
    df = pd.DataFrame({"date": ["2025-01-01", "2025-01-04"], "qty": [5, 7]})
    s = fc.to_daily_series(df)
    assert list(s.values) == [5, 0, 0, 7]


def test_발주점_공식():
    plan = fc.reorder_plan(np.full(30, 10.0), residual_std=4.0, current_stock=50, lead_time_days=4, service_level=0.95)
    z = 1.6449
    safety = z * 4.0 * math.sqrt(4)
    assert plan["safety_stock"] == pytest.approx(safety, abs=0.1)
    assert plan["reorder_point"] == pytest.approx(40 + safety, abs=0.1)
    assert plan["should_reorder"] is True  # 50 <= 53.2
    assert plan["suggested_order_qty"] == math.ceil(110 + safety - 50)


def test_재고가_충분하면_발주량_0():
    plan = fc.reorder_plan(np.full(30, 10.0), 2.0, current_stock=10_000, lead_time_days=5, service_level=0.9)
    assert plan["suggested_order_qty"] == 0 and plan["should_reorder"] is False


def test_api_예측과_발주_제안():
    res = client.post("/forecast", json={"sku": "SKU-1", "history": history_payload(), "horizon": 7,
                                          "lead_time_days": 5, "current_stock": 100})
    assert res.status_code == 200
    body = res.json()
    assert body["model"] in {"gbr", "seasonal_naive", "moving_average", "naive"}
    assert len(body["forecast"]) == 7
    assert all(d["quantity"] >= 0 for d in body["forecast"])
    assert body["reorder"]["suggested_order_qty"] >= 0


def test_api_이력이_짧으면_422():
    res = client.post("/forecast", json={"sku": "S", "history": history_payload(days=30), "current_stock": 10})
    assert res.status_code == 422


def test_api_음수_수량_거절():
    bad = [{"date": "2025-01-01", "quantity": -5}]
    res = client.post("/forecast", json={"sku": "S", "history": bad, "current_stock": 10})
    assert res.status_code == 422


def test_health():
    assert client.get("/health").json() == {"status": "ok"}
