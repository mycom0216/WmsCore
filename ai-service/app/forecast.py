"""수요 예측: 일별 출고량 시계열 → 향후 N일 예측.

모델은 두 부류를 같은 조건으로 겨루게 한다.
  - 단순 기준선: 어제 값(naive), 7일 전 값(seasonal naive), 최근 28일 평균(moving average)
  - 머신러닝: 시차·요일·이동평균 특징을 쓰는 GradientBoosting (재귀 방식 다단계 예측)
rolling-origin 백테스트(과거 시점마다 "그때까지의 데이터로만" 학습해 다음 7일을 맞혀 봄)로 WAPE를 재고,
가장 낮은 모델을 고른다. ML이 기준선을 못 이기면 기준선을 쓴다(복잡한 모델이 항상 낫지는 않다).
"""
from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np
import pandas as pd
from sklearn.ensemble import GradientBoostingRegressor

SEASON = 7
MIN_HISTORY = 60


def make_synthetic_demand(days: int = 400, seed: int = 7) -> pd.DataFrame:
    """주간 패턴 + 완만한 증가 추세 + 가끔 프로모션 급증 + 잡음을 가진 가상 수요. (실제 데이터 아님)"""
    rng = np.random.default_rng(seed)
    dates = pd.date_range("2025-01-01", periods=days, freq="D")
    weekday_factor = np.array([0.8, 1.0, 1.05, 1.0, 1.2, 1.5, 0.6])  # 월~일
    base = 40 + 0.05 * np.arange(days)
    promo = (rng.random(days) < 0.03) * rng.uniform(1.6, 2.2, days)
    mean = base * weekday_factor[dates.day_of_week] * np.where(promo > 0, promo, 1.0)
    qty = rng.poisson(mean).astype(float)
    return pd.DataFrame({"date": dates, "qty": qty})


def to_daily_series(df: pd.DataFrame) -> pd.Series:
    """날짜별 합계로 만들고 빠진 날은 0(출고 없음)으로 채운다."""
    s = df.groupby(pd.to_datetime(df["date"]).dt.normalize())["qty"].sum().sort_index()
    return s.asfreq("D", fill_value=0.0)


def _features(history: list[float], date: pd.Timestamp) -> list[float]:
    h = np.asarray(history, dtype=float)
    return [
        h[-1], h[-7], h[-14],
        h[-7:].mean(), h[-28:].mean(),
        date.day_of_week,
    ]


def _training_matrix(s: pd.Series) -> tuple[np.ndarray, np.ndarray]:
    values = s.to_numpy(dtype=float)
    X, y = [], []
    for i in range(28, len(values)):
        X.append(_features(list(values[:i]), s.index[i]))
        y.append(values[i])
    return np.asarray(X), np.asarray(y)


def _new_gbr() -> GradientBoostingRegressor:
    return GradientBoostingRegressor(n_estimators=150, max_depth=3, learning_rate=0.05, subsample=0.8, random_state=0)


def predict_gbr(train: pd.Series, horizon: int) -> np.ndarray:
    X, y = _training_matrix(train)
    model = _new_gbr().fit(X, y)
    history = list(train.to_numpy(dtype=float))
    date = train.index[-1]
    out = []
    for _ in range(horizon):
        date = date + pd.Timedelta(days=1)
        pred = max(0.0, float(model.predict(np.asarray([_features(history, date)]))[0]))
        out.append(pred)
        history.append(pred)  # 재귀: 예측값을 다음 날의 입력으로 사용
    return np.asarray(out)


def predict_naive(train: pd.Series, horizon: int) -> np.ndarray:
    return np.full(horizon, float(train.iloc[-1]))


def predict_seasonal_naive(train: pd.Series, horizon: int) -> np.ndarray:
    last_week = train.iloc[-SEASON:].to_numpy(dtype=float)
    return np.array([last_week[i % SEASON] for i in range(horizon)])


def predict_moving_average(train: pd.Series, horizon: int) -> np.ndarray:
    return np.full(horizon, float(train.iloc[-28:].mean()))


MODELS = {
    "gbr": predict_gbr,
    "seasonal_naive": predict_seasonal_naive,
    "moving_average": predict_moving_average,
    "naive": predict_naive,
}


def wape(actual: np.ndarray, pred: np.ndarray) -> float:
    """Weighted Absolute Percentage Error = 오차 절대값 합 / 실제 합. 수요가 0인 날이 있어도 안전한 지표."""
    total = float(np.abs(actual).sum())
    return float(np.abs(actual - pred).sum() / total) if total > 0 else float("nan")


@dataclass
class BacktestResult:
    wape: dict[str, float]
    mae: dict[str, float]
    residual_std: float  # 선택된 모델의 일별 예측 오차 표준편차 (안전재고 계산에 사용)
    folds: int
    horizon: int


def backtest(s: pd.Series, horizon: int = 7, folds: int = 4, models: dict | None = None, select_by: str | None = None) -> BacktestResult:
    """뒤쪽 folds개 구간을 차례로 숨기고, 그 앞 데이터만으로 학습·예측해 실제와 비교한다(미래 정보 누출 없음)."""
    models = models or MODELS
    if len(s) < MIN_HISTORY + horizon * folds:
        raise ValueError(f"백테스트에는 최소 {MIN_HISTORY + horizon * folds}일의 이력이 필요합니다.")
    actuals, preds = [], {name: [] for name in models}
    for k in range(folds, 0, -1):
        cut = len(s) - k * horizon
        train, test = s.iloc[:cut], s.iloc[cut:cut + horizon]
        actuals.append(test.to_numpy(dtype=float))
        for name, fn in models.items():
            preds[name].append(fn(train, horizon))
    actual = np.concatenate(actuals)
    wapes, maes = {}, {}
    for name, p in preds.items():
        p = np.concatenate(p)
        wapes[name] = wape(actual, p)
        maes[name] = float(np.abs(actual - p).mean())
    best = select_by or min(wapes, key=wapes.get)
    resid = actual - np.concatenate(preds[best])
    return BacktestResult(wapes, maes, float(resid.std(ddof=1)), folds, horizon)


def forecast(s: pd.Series, horizon: int, folds: int = 4) -> tuple[np.ndarray, str, BacktestResult]:
    """백테스트에서 가장 좋은 모델로 전체 이력을 학습해 horizon일을 예측한다."""
    if len(s) < MIN_HISTORY:
        raise ValueError(f"이력이 너무 짧습니다. 최소 {MIN_HISTORY}일이 필요합니다.")
    bt_horizon = min(horizon, 14)
    result = backtest(s, horizon=bt_horizon, folds=folds)
    champion = min(result.wape, key=result.wape.get)
    return MODELS[champion](s, horizon), champion, result


def reorder_plan(daily_forecast: np.ndarray, residual_std: float, current_stock: int,
                 lead_time_days: int, service_level: float, review_days: int = 7) -> dict:
    """발주점·안전재고 계산.
    안전재고 = z × σ(일 오차) × √리드타임,  발주점 = 리드타임 동안 예상 수요 + 안전재고,
    발주량 = (리드타임 + 점검주기) 동안 예상 수요 + 안전재고 − 현재고  (음수면 0)
    """
    from statistics import NormalDist

    z = NormalDist().inv_cdf(service_level)
    safety = z * residual_std * math.sqrt(lead_time_days)
    lead_demand = float(daily_forecast[:lead_time_days].sum()) if len(daily_forecast) >= lead_time_days \
        else float(daily_forecast.mean() * lead_time_days)
    cover_days = lead_time_days + review_days
    cover_demand = float(daily_forecast[:cover_days].sum()) if len(daily_forecast) >= cover_days \
        else float(daily_forecast.mean() * cover_days)
    reorder_point = lead_demand + safety
    order_qty = max(0, math.ceil(cover_demand + safety - current_stock))
    return {
        "safety_stock": round(safety, 1),
        "reorder_point": round(reorder_point, 1),
        "should_reorder": current_stock <= reorder_point,
        "suggested_order_qty": order_qty,
    }
