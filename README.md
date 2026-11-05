# WmsCore — 창고 재고·주문 관리 API (ASP.NET Core / .NET 10)

물류·제조 현장에서 쓰는 WMS(창고관리시스템)의 핵심인 **입고 → 재고 → 주문 출고(할당) → 취소/출하** 흐름을 REST API로 만든 프로젝트입니다.
"여러 사람이 동시에 같은 재고를 주문해도 재고가 틀어지지 않는가?"를 코드와 테스트로 증명하는 데 초점을 맞췄습니다.

## 기술 스택

C# 14 · .NET 10 · ASP.NET Core Web API · EF Core 10 · SQLite(개발) / SQL Server(운영) · JWT 인증·역할 권한 · xUnit + WebApplicationFactory 통합 테스트 · Docker / docker-compose · GitHub Actions

## 핵심 설계 (면접에서 설명할 포인트)

| 문제 | 해결 | 위치 |
| --- | --- | --- |
| 동시 주문으로 재고가 음수가 되는 초과 출고 | "읽고 검사하고 쓰기" 대신 **조건부 UPDATE 한 문장** (`... WHERE Quantity >= @q`). 영향 행이 0이면 재고 부족. DB가 원자적으로 판단 | `StockService.CreateOrderAsync` |
| 여러 줄 주문 중 일부만 실패 | 한 **트랜잭션**으로 묶어 전체 롤백 | 동일 |
| 같은 취소 요청이 동시에 두 번 → 재고 이중 복구 | 상태 전이도 조건부 UPDATE (`WHERE Status = Created`) | `TransitionAsync` |
| 네트워크 재시도로 입고가 두 번 반영 | 입고 전표번호 **유일 인덱스**로 멱등 처리 | `ReceiveAsync` |
| 첫 입고가 동시에 들어와 INSERT 충돌 | UPDATE 먼저 → 0행이면 INSERT → 충돌 시 재시도 | `AddStockAsync` |
| 여러 행을 갱신할 때 데드락 | 항상 (상품, 위치) 순으로 정렬해 같은 순서로 잠금 | `ResolveAsync` |
| 재고가 왜 이 값인지 추적 | 모든 증감을 **수불 원장**(`StockMovement`)에 기록 | `/api/movements` |
| 최후의 안전망 | DB `CHECK (Quantity >= 0)` 제약 | `WmsDbContext` |
| 계정 보안 | PBKDF2-SHA256 60만 회 + 솔트, 상수 시간 비교, 없는 계정도 같은 비용의 해시 검증(계정 존재 여부 노출 방지) | `Auth.cs` |
| 오류 응답 | 업무 예외 → RFC 7807 `ProblemDetails`, 서버 오류는 내부 정보 숨김 | `Errors.cs` |

설계 선택의 이유: 낙관적 동시성(RowVersion + 재시도)도 검토했지만, 재고 차감처럼 충돌이 잦은 경로는 재시도 폭주가 생길 수 있어 조건부 UPDATE를 택했습니다. 이 방식은 SQLite와 SQL Server에서 같은 코드로 동작합니다.

## Python AI 서비스

[`ai-service/`](ai-service/README.md): 일별 출고 이력으로 수요를 예측하고 발주점·발주량을 제안하는 FastAPI 서비스(pandas, scikit-learn). 기준선 대비 백테스트로 모델을 고릅니다. 현재 WmsCore와는 API로 직접 연결돼 있지 않고 이력을 요청 본문으로 받습니다.

## 실행

```bash
# 1) 설치 없이 바로 (SQLite)
dotnet run --project src/WmsCore.Api        # 개발 모드에서 /openapi/v1.json 제공

# 2) SQL Server까지 Docker로
docker compose up --build                   # http://localhost:8080/health
```

데모 계정(첫 실행 시 생성): `admin / Admin123!` (입고·마스터 등록), `operator / Operator123!` (주문). **운영에서는 `Seed__AdminPassword`, `Jwt__Key` 환경 변수로 반드시 바꿉니다.**

```bash
# 로그인 → 토큰
curl -X POST localhost:5000/api/auth/login -H "Content-Type: application/json" -d "{\"userName\":\"admin\",\"password\":\"Admin123!\"}"
```

| 메서드 | 경로 | 권한 | 설명 |
| --- | --- | --- | --- |
| POST | /api/auth/login | 공개 | JWT 발급 |
| GET/POST | /api/products | 로그인 / Admin | 상품 조회(검색·페이징) / 등록 |
| GET/POST | /api/locations | 로그인 / Admin | 위치 조회 / 등록 |
| POST | /api/inbound | Admin | 입고 (전표번호 멱등) |
| GET | /api/stocks?sku= | 로그인 | 현재고 |
| POST | /api/orders | 로그인 | 주문 생성 = 재고 할당 (부족 시 409, 전체 롤백) |
| POST | /api/orders/{id}/ship · cancel | 로그인 | 출하 / 취소(재고 복구) |
| GET | /api/movements?sku= | 로그인 | 수불 원장 |
| GET | /health | 공개 | DB 연결 포함 상태 확인 |

## 테스트

```bash
dotnet test
```

실제 서버 파이프라인(인증, 검증, 예외 처리, DB 트랜잭션)을 통과하는 통합 테스트 10개. 대표 시나리오:

- 재고 10개에 **동시 주문 50건** → 정확히 10건 성공, 40건 409, 최종 재고 0
- 같은 주문을 **동시에 10번 취소** → 1번만 성공, 재고는 한 번만 복구
- 2줄 주문에서 둘째 줄 재고 부족 → 첫 줄도 롤백
- 같은 입고 전표 2회 전송 → 재고 1회만 증가
- 권한(401/403), 입력 검증(400), 없는 상품(404), 출하 후 취소 불가, 수불 원장 기록

## 한계와 다음 단계 (솔직하게)

- 스키마는 `EnsureCreated`로 만들었습니다. 운영 배포에는 EF Core **마이그레이션**으로 전환해야 합니다.
- 동시성 테스트는 SQLite로 검증했습니다. SQL Server용 `docker-compose.yml`은 작성했지만 이 저장소의 테스트는 SQL Server에서 돌리지 않았습니다. 같은 SQL 문이라 동작이 같을 것으로 보지만 실측은 아닙니다.
- 출하는 상태 변경만 합니다(송장·배송 연동 없음). 로그인 시도 횟수 제한, 리프레시 토큰, 캐시(Redis)는 아직 없습니다.
- 다음 확장 후보: Outbox 패턴으로 ERP 연동 이벤트 발행, 재고 실사(조정) API, Serilog 구조화 로그 + OpenTelemetry.
