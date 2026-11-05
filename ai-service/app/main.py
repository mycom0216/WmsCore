from __future__ import annotations

from datetime import date as Date

import pandas as pd
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field, field_validator

from . import forecast as fc

app = FastAPI(title="WMS AI Service", version="1.0.0",
              description="일별 출고 이력으로 수요를 예측하고 발주점·발주량을 제안합니다.")


class HistoryPoint(BaseModel):
    date: Date
    quantity: float = Field(ge=0)


class ForecastRequest(BaseModel):
    sku: str = Field(min_length=1, max_length=40)
    history: list[HistoryPoint] = Field(min_length=1, max_length=5000)
    horizon: int = Field(default=14, ge=1, le=30, description="예측할 일수")
    lead_time_days: int = Field(default=5, ge=1, le=60, description="발주 후 입고까지 걸리는 일수")
    current_stock: int = Field(ge=0)
    service_level: float = Field(default=0.95, gt=0.5, lt=0.999, description="품절을 피하고 싶은 확률")

    @field_validator("history")
    @classmethod
    def unique_dates_not_required(cls, v):
        return v  # 같은 날짜가 여러 번 오면 합산한다(출고 건별 이력을 그대로 보낼 수 있게)


class DayForecast(BaseModel):
    date: Date
    quantity: float


class ForecastResponse(BaseModel):
    sku: str
    model: str
    forecast: list[DayForecast]
    backtest_wape: dict[str, float]
    residual_std: float
    reorder: dict


@app.get("/health")
def health() -> dict:
    return {"status": "ok"}


@app.post("/forecast", response_model=ForecastResponse)
def forecast(req: ForecastRequest) -> ForecastResponse:
    df = pd.DataFrame([{"date": p.date, "qty": p.quantity} for p in req.history])
    series = fc.to_daily_series(df)
    try:
        values, champion, bt = fc.forecast(series, req.horizon)
    except ValueError as e:
        raise HTTPException(status_code=422, detail=str(e)) from e

    start = series.index[-1] + pd.Timedelta(days=1)
    days = [DayForecast(date=(start + pd.Timedelta(days=i)).date(), quantity=round(float(v), 1)) for i, v in enumerate(values)]
    plan = fc.reorder_plan(values, bt.residual_std, req.current_stock, req.lead_time_days, req.service_level)
    return ForecastResponse(
        sku=req.sku, model=champion, forecast=days,
        backtest_wape={k: round(v, 4) for k, v in bt.wape.items()},
        residual_std=round(bt.residual_std, 2), reorder=plan,
    )
