"""백테스트 결과를 표로 출력한다.  실행: python evaluate.py"""
from app import forecast as fc

for seed in (7, 11, 23):
    s = fc.to_daily_series(fc.make_synthetic_demand(days=400, seed=seed))
    r = fc.backtest(s, horizon=7, folds=8)
    print(f"\n[seed={seed}] 8개 구간 x 7일 예측, WAPE(낮을수록 좋음)")
    for name, w in sorted(r.wape.items(), key=lambda kv: kv[1]):
        print(f"  {name:15s} WAPE {w:6.1%}   MAE {r.mae[name]:5.2f}")
