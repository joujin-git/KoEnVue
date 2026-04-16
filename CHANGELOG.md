# Changelog

이 프로젝트의 주요 변경 사항을 기록합니다.
형식은 [Keep a Changelog](https://keepachangelog.com/ko/)를 따릅니다.

## [Unreleased]

### 수정

- Always 모드 투명도 수정 — ActiveOpacity → IdleOpacity 페이드 전이, 트레이 프리셋 반영, 핫리로드 즉시 반영
- 시스템 입력 ESC 해제 시 인디케이터 숨김 (시작 메뉴 + 검색)

## [0.9.0.0] — 2026-04-16

### 추가

- 로그 타임스탬프에 날짜 표시 (`[INFO] 12:40:46.172` → `[INFO] 2026.04.16 12:40:46.172`)

### 수정

- Win11 바탕화면·작업 표시줄 우클릭 컨텍스트 메뉴에서 인디케이터 숨김
- StartMenuExperienceHost 인디케이터 위치 보정 (캐시된 프레임 재사용)

## [0.8.9.0] — 2026-04-14

첫 공개 릴리스.

### 추가

- 한/En/EN 라벨로 IME 상태 실시간 표시
- CAPS LOCK 좌우 세로 막대 동시 표시
- 드래그 가능한 TOPMOST 오버레이 (앱별 위치 기억)
- 자석 스냅 + Shift 축 고정, 멀티 모니터 / DPI / 화면 회전 대응
- 트레이 메뉴: 투명도·크기·시작 프로그램·기본 위치·상세 설정
- 59개 필드 상세 설정 다이얼로그
- 6개 프리셋 테마 (Dracula / Nord / Monokai / Solarized Dark / High Contrast / Default)
- 소수점 인디케이터 배율 (1.0~5.0) 커스텀 입력 다이얼로그
- 저장 위치 없는 앱의 기본 인디케이터 위치 설정
- 미사용 인디케이터 위치 정리 다이얼로그
- 캐럿 이동 감지 (마우스 클릭 재배치 시 인디케이터 표시)
- `Ctrl+Alt+H` 보이기/숨기기 토글
- GitHub Releases 업데이트 알림 (트레이 메뉴, WinHTTP 경량 구현)
- 완전 포터블 config — exe 폴더 우선, delete-safe 핫 리로드
- 포터블 단일 exe (~4.9 MB), .NET 런타임 설치 불필요
- exe 애플리케이션 아이콘
