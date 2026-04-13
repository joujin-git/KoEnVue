# 01 — Stage 0: 기준선 고정

← Previous: [00 — Context & Target Structure](00-context-and-structure.md) | → Next: [02 — Stage 1](02-stage1-dedupe.md)

## 목표

리팩터링 전후의 델타를 **객관적으로** 비교할 수 있는 baseline 파일을 `docs/reorg/baseline.md`로 고정하고 커밋으로 승격한다. Stage 5와 Stage 7이 이 파일을 읽어 경고 증가량과 exe 크기 델타를 계산하므로 누락 시 후속 검증이 불가능하다.

## 에이전트 구성

- **구성**: serial, 1x general-purpose
- **역할**: 빌드/퍼블리시 실행 → 메트릭 캡처 → baseline.md 작성 → C0 커밋

## 작업 내역

1. **브랜치 생성**: `git checkout -b refactor/feature-reorg` (이 자체는 커밋 생성 안 함)

2. **기준 빌드 캡처** — `docs/reorg/baseline.md`에 **파일로 저장** (메모 금지)
   - `dotnet build` — 전체 출력 저장, 경고 개수를 헤더에 기록
   - `dotnet publish -r win-x64 -c Release` — 생성된 `KoEnVue.exe`의 바이트 크기와 SHA256 해시 기록

3. **파일별 라인 수 표 작성** — `baseline.md`의 "Line counts" 섹션
   - 대상: `Utils/*.cs`, `Native/*.cs`, `Models/*.cs`, `UI/*.cs`, `Detector/*.cs`, `Config/*.cs`, `Program.cs`
   - 형식: `| file | lines |` 테이블

4. **실행 스모크** — publish된 exe를 띄워 10초 내 확인
   - 인디케이터가 화면에 나오는지
   - IME 한/영 토글이 반영되는지
   - 결과를 `baseline.md`에 `[PASS]`/`[FAIL]`로 기록

5. **C0 커밋**
   - `git add docs/reorg/baseline.md`
   - 커밋 제목: `chore: capture refactor baseline metrics`
   - 본 Stage의 유일한 커밋

## baseline.md 섹션 구조 (예시)

```markdown
# Refactor Baseline — <YYYY-MM-DD>

## Build
- dotnet build: warnings = N
- dotnet publish: exe size = N bytes, SHA256 = ...

## Line counts
| file | lines |
|---|---|
| UI/Overlay.cs | 825 |
| UI/Tray.cs    | 1169 |
| ...           | ... |

## Smoke
- Indicator visible: [PASS]
- IME toggle: [PASS]
```

## 검증 게이트

- Debug + Release 빌드 모두 경고 증가 없이 성공
- 스모크 `[PASS]` 2/2
- `docs/reorg/baseline.md`가 존재하고 내용이 채워져 있음
- `git log --oneline -1`이 baseline 커밋을 가리킴

---

← Back to [README](README.md)
