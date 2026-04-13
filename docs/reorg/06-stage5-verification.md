# 06 — Stage 5: 전체 빌드 & 런타임 검증

← Previous: [05 — Stage 4](05-stage4-core-extraction.md) | → Next: [07 — Stage 6](07-stage6-docs-sync.md)

## 목표

Stage 4까지의 작업(12 커밋)이 모두 한 번에 얹혀도 빌드/런타임에서 회귀가 없는지 확인. 문서 변경 전이므로 **커밋 없음** — 빌드와 스모크만 수행.

## 에이전트 구성

- **구성**: serial, 1x general-purpose
- **커밋 없음** — 검증 전용 Stage

## 작업 내역

1. **클린 빌드 사이클**
   - `dotnet clean`
   - `dotnet build` (debug)
   - `dotnet publish -r win-x64 -c Release`

2. **기준선과 비교** — `docs/reorg/baseline.md` 읽어 델타 검증
   - 경고 델타 0
   - exe 크기 델타 ≤ +100KB

3. **풀 스모크 매트릭스** (10개 항목)

| # | 항목 | 검증 방법 |
|---|---|---|
| a | 최초 실행 시 `config.json` 생성 | 파일 삭제 후 exe 실행 → 새 `config.json` 생성 확인 |
| b | 한/En/EN 상태 전환 반영 | Alt+Space (또는 Right Alt) → 라벨 변경 확인 |
| c | 오버레이 드래그 + 에지 스냅 + Shift 축 고정 + DPI 전환 | 드래그, Shift 누른 채 드래그, 다른 DPI 모니터로 이동 |
| d | 트레이 메뉴 전 항목 동작 | 투명도, 크기, 스냅, 애니메이션, 하이라이트, 설정 다이얼로그 진입 |
| e | CleanupDialog 열기/Tab/ESC/저장 | 메뉴 → 초기화 → Tab 순환 → ESC → 저장 |
| f | ScaleInputDialog 열기/입력/Enter/ESC | 메뉴 → 크기 → 직접 지정 → 값 입력 |
| g | SettingsDialog 스크롤 + 헥스 검증 + 저장 | 설정 다이얼로그 스크롤, `XYZ123` 입력해 검증 실패 확인, 저장 후 `config.json` 반영 |
| h | 외부에서 `config.json` 수정 → 5초 내 핫-리로드 | 파일 에디터로 Opacity 변경 후 저장 |
| i | 손상된 `config.json` 투입 → 1회 경고 후 스팸 없음 | JSON 일부러 깨뜨린 후 `koenvue.log` 경고 횟수 확인 |
| j | 시스템 입력 프로세스 포커스 전환 | Win 키, Win+S로 시작 메뉴/Search 전환 → 인디케이터가 위쪽에 나타나는지 확인 |

## 검증 게이트

- 위 10개 항목 모두 통과
- 경고 증가 0
- 크기 델타 허용 범위 내 (≤ +100KB)

## 실패 시 롤백 전략

- 스모크 실패 원인에 따라 부분 revert:
  - Stage 4 특정 추출에서 회귀 → C9/C10/C11/C12 중 해당 커밋만 revert (서로 독립)
  - Stage 2 네임스페이스 재배치에서 회귀 → C6 revert (단일 원자 커밋이므로 깨끗)
  - Stage 1 핫스팟 수정에서 회귀 → C1~C5 중 해당 커밋만 revert
- 각 커밋이 완전성을 만족하므로 아래부터 차례로 벗겨도 빌드가 유지됨

## 커밋 출력

**없음** — Stage 5는 검증 전용.

---

← Back to [README](README.md)
