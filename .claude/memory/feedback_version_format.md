---
name: feedback-version-format
description: KoEnVue 의 버전 문자열은 항상 4-part (major.minor.build.revision). 2~3-part 금지.
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 0e207608-7099-4da3-a65f-7723eecf034d
---

KoEnVue 의 모든 버전 문자열은 **4-part** (`major.minor.build.revision`, 예: `0.9.3.0`) 로 적습니다. csproj `<Version>`, git 태그 (`v0.9.3.0`), CHANGELOG 헤더 (`## [0.9.3.0]`), GitHub Release 제목 모두 4-part. 과거 태그 `v0.8.9.0` ~ `v0.9.2.8` + 현행 `v0.9.3.0` 모두 4-part 일관 유지.

**Why**: 사용자 명시 선호 (2026-05-21 13-PR 작업 종료 시 3-part `0.10.0` 으로 릴리스 만들었다가 취소 요청 받음, 이후 `0.10.0.0` 으로 정정했다가 다시 `0.9.3.0` 로 재정렬). 본 프로젝트는 PE 헤더 4 필드 (`AssemblyVersion` / `FileVersion` / `InformationalVersion`) 가 모두 동일 4-part 로 박혀 Windows 탐색기 "자세히" 탭에서 혼동 없도록 컨벤션화. `System.Version.TryParse` 는 2-part `0.9` 도 받아들이지만 그러면 PE 헤더가 `0.9.0.0` 으로 0-padding 돼 csproj 값과 사용자 가시 값이 어긋남.

**How to apply**:
- csproj `<Version>` bump 시 항상 4-part. 예: `<Version>0.9.3.0</Version>` (`<Version>0.9.3</Version>` 금지).
- git 태그도 4-part. 예: `git tag v0.9.3.0` (`git tag v0.9.3` 금지).
- CHANGELOG 섹션 헤더, GitHub Release 제목, dev-note 등 모든 문서에서 동일 형식.
- [docs/release-procedure.md](../../e:/dev/KoEnVue/docs/release-procedure.md) §2 가 4-part 필수를 명시 (PR-11 D6 산출 + 본 feedback 으로 강화).
- csproj `<Version>` 한 줄이 단일 진실원이라 PE 헤더 + `DefaultConfig.AppVersion` 모두 derive (PR-11). 4-part 만 손대면 모든 경로 정합.

**메이저 vs 마이너 판단**: 13-PR (asInvoker BREAKING 포함) 도 메이저 bump (`0.10.x`) 가 아닌 minor bump (`0.9.3.x`) 로 발행 결정 — 사용자가 0.x 시리즈 안에서 BREAKING 을 minor 로 처리하는 패턴을 선호. 메이저 bump 는 추후 더 큰 마일스톤에 보류.
