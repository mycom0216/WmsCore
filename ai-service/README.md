# WMS AI Service — 수요 예측 · 발주점 제안 (Python / FastAPI)

WmsCore가 쌓는 일별 출고 이력으로 **향후 수요를 예측**하고, 리드타임과 목표 서비스 수준을 반영해 **발주점·안전재고·발주량**을 제안하는 마이크로서비스입니다.

스택: Python 3.12+ · FastAPI · pandas · NumPy · scikit-learn · pytest · Docker

## 실행

```bash
python -m venv .venv && .venv\Scripts\activate      # macOS/Linux: source .venv/bin/activate
pip install -r requirements-dev.txt
pytest -q                                           # 테스트 10개
uvicorn app.main:app --reload                       # http://localhost:8000/docs (자동 API 문서)
python evaluate.py                                  # 모델 비교 표 출력
```

```bash
curl -X POST localhost:8000/forecast -H "Content-Type: application/json" -d @request.json
```
요청: `sku`, `history`(날짜·수량 목록, 최소 60일), `horizon`(1~30일), `lead_time_days`, `current_stock`, `service_level`.
응답: 날짜별 예측, 선택된 모델, 백테스트 WAPE, `reorder`(안전재고·발주점·발주 여부·제안 수량).

## 설계 (면접 설명 포인트)

1. **기준선부터**: 어제 값(naive), 7일 전 값(seasonal naive), 28일 평균과 GradientBoosting을 같은 조건에서 비교합니다. "복잡한 모델이 정말 필요한가"를 숫자로 답하려는 구조입니다.
2. **미래 정보 누출 없는 평가**: rolling-origin 백테스트. 각 평가 구간 *이전* 데이터만으로 학습하고 다음 7일을 맞힙니다. 학습 구간이 평가 구간을 넘지 않는다는 것을 테스트로 고정했습니다.
3. **챔피언 선택**: 백테스트 WAPE가 가장 낮은 모델을 실제 예측에 씁니다. ML이 기준선을 못 이기면 기준선이 선택됩니다.
4. **재귀 다단계 예측**: 예측값을 다음 날 입력(시차·이동평균)에 넣어 horizon일을 예측합니다. 먼 날일수록 오차가 쌓이는 한계가 있습니다.
5. **예측을 의사결정으로**: 백테스트 오차의 표준편차 σ로 안전재고 `z·σ·√리드타임`, 발주점 `리드타임 수요 + 안전재고`, 발주량 `(리드타임+점검주기 7일) 수요 + 안전재고 − 현재고`를 계산합니다.
6. **지표 선택**: 수요가 0인 날이 있는 데이터에서 MAPE는 발산하므로 WAPE를 사용했습니다.
7. 입력 검증(pydantic): 음수 수량, 이력 부족은 422. 빠진 날짜는 0으로 채우고, 같은 날짜가 여러 번 오면 합산합니다.

## 결과 (가상 데이터, 8개 구간 × 7일 예측, WAPE — 낮을수록 좋음)

| 데이터(seed) | GradientBoosting | 7일 전 값 | 28일 평균 | 어제 값 |
| --- | --- | --- | --- | --- |
| 7 | **12.8%** | 14.8% | 24.2% | 25.3% |
| 11 | **19.3%** | 19.6% | 26.5% | 35.5% |
| 23 | **13.3%** | 16.2% | 22.9% | 25.3% |

## 솔직한 한계

- **데이터는 직접 만든 가상 수요**(요일 패턴 + 완만한 증가 + 프로모션 + 잡음)입니다. 실제 수요에서의 성능을 보장하지 않습니다.
- GradientBoosting의 개선 폭은 seed에 따라 작습니다(seed 11은 19.3% vs 19.6%로 사실상 비슷). 표본이 적어 우연과 구분되지 않을 수 있습니다.
- 공휴일·가격·재고 부족으로 인한 판매 손실(검열된 수요)은 반영하지 않습니다.
- WmsCore와는 아직 직접 연결하지 않았습니다. 현재는 이력을 요청 본문으로 받습니다. 다음 단계: WmsCore에 날짜 범위 출고 집계 API를 추가하고 이 서비스가 호출하는 연동.
