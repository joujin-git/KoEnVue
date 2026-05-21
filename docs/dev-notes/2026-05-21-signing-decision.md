# 2026-05-21 — 코드 사인 보류 + SHA256 게시 결정

**Context**: PR-11 G4. v0.9.x 시절 KoEnVue.exe 가 무서명 + `requireAdministrator` 라 Windows SmartScreen 이 "확인되지 않은 게시자" 경고 + UAC 프롬프트를 동시에 띄웠다. PR-03 (asInvoker 전환) 으로 UAC 는 사라졌지만 SmartScreen 경고는 남는다.

## 결정

- **EV/OV 코드 사인 인증서 도입 보류.**
- **GitHub Releases body 에 SHA256 hash 게시 + 사용자 검증 가이드 ([README.md](../../README.md) 다운로드 검증 섹션).**
- **publish 산출물 옆에 `KoEnVue.exe.sha256.txt` 자동 생성 ([Directory.Build.targets](../../Directory.Build.targets) `EmitSha256` Target).**

## ROI 분석 — 왜 코드 사인 안 하나

| 항목 | OV (Organization Validation) | EV (Extended Validation) |
|------|-----------------------------|---------------------------|
| 연간 비용 (DigiCert, 2026 기준) | $200~400 | $400~700 |
| SmartScreen 즉시 신뢰 | 아니오 — 평판 누적 후 | 예 (구매 시점부터) |
| HSM / USB 토큰 요구 | 아니오 | 예 (분실 시 재발급 비용) |
| 발급 절차 | 사업자 등록증 등 서류 검증 (1~2주) | 더 까다로움 (2~4주) |
| 키 관리 책임 | 평문 PFX 보관 위험 | HSM 강제라 비교적 안전 |

**현재 사용자 베이스**: 한국어 IME 사용자 / 개인 / 다운로드 수 미공개. 무료 / 오픈소스 / 개인 프로젝트 규모라 OV $200/년 도 손익분기 불명. SmartScreen 경고는 "추가 정보 → 그래도 실행" 한 단계 클릭으로 우회 가능 — 신뢰는 SHA256 검증 가이드 + 평판 누적으로 대체.

**무서명 단점**: (1) SmartScreen 1단계 경고 — 첫 사용자가 클릭 한 번 더. (2) AV 가 휴리스틱으로 false positive 분류 가능 — 다행히 NativeAOT + KoEnVue 의 정직한 P/Invoke 만 쓰는 코드는 AV 의 의심 패턴에 잘 안 걸린다 (UPX 같은 packer 미사용, 키로깅 의심 패턴 미보유).

## SHA256 게시의 역할

코드 사인 미도입 상태에서 **변조 검증** 만이라도 보장:

1. publish 시점에 `Directory.Build.targets` 의 `EmitSha256` Target 가 PowerShell `Get-FileHash` 로 hash 계산 → `KoEnVue.exe.sha256.txt` (Algorithm + Hash 두 줄) 생성.
2. 유지보수자가 GitHub Releases body 에 hash 값 인용 + `.sha256.txt` 첨부.
3. 다운로드 후 사용자가 `Get-FileHash -Algorithm SHA256 .\KoEnVue.exe` 한 줄로 비교 (PowerShell 은 모든 Windows 10/11 에 기본 탑재).

`<Deterministic>true</Deterministic>` 가 활성이라 같은 SDK + 같은 코드 입력이면 SHA256 이 항상 동일 — 재현 가능한 빌드. 다른 환경 (다른 머신 / 다른 SDK 마이너 버전) 에서 빌드한 hash 가 GitHub 정본과 다르면 의심.

## 재검토 트리거

다음 중 하나라도 충족하면 코드 사인을 재검토:

1. **사용자 베이스 확장**: GitHub Releases 다운로드 수 1,000+ / 년. 평판 학습이 일어나기 전 첫 사용자들의 SmartScreen 마찰 비용이 OV 인증서 비용을 넘어선다.
2. **AV false positive 보고**: 잘 알려진 AV (Defender, Avast, Kaspersky, BitDefender 등) 중 하나라도 명확한 false positive 분류. 사인된 exe 는 AV 평판에서 우대받음.
3. **기업 / 학교 환경 배포 요청**: AppLocker / SmartScreen 정책이 강한 환경. 무서명은 사실상 차단되므로 OV 최소 필요.
4. **유료화**: 어떤 형태로든 결제 / 라이선스 도입. 신뢰 표현이 사업 요건이 됨.

## 대안 — 무료 사인 옵션

- **Sigstore / cosign**: OSS 기반 transparency-log 사인. SmartScreen 은 인식 안 함 (Windows 가 신뢰하는 root CA 가 아님). 별도 검증 단계라 사용자 학습 비용.
- **Microsoft Store**: Store 게시 시 자동 사인 + SmartScreen 통과. 그러나 NativeAOT win-x64 단일 exe 모델과 호환 안 됨 (Store 는 .msix 패키지 필요). 본 프로젝트의 "포터블 단일 exe" 정체성과 충돌.
- **Azure Code Signing (Trusted Signing)**: Microsoft 의 SaaS 사인. 월 $9.99 부터 + Azure 구독. 키 관리 부담은 줄지만 여전히 사업자 등록 검증이 필요해 개인 프로젝트는 진입 장벽.

→ 결국 현 상태 (무서명 + SHA256) 가 trade-off 최적.

## 함의 — NativeAOT 비결정성 (PR-11 Tier-3 발견)

`<Deterministic>true</Deterministic>` 는 **C# 컴파일러 출력** (IL + PE 헤더 timestamp) 만 결정성을 보장한다. NativeAOT ILC 의 codegen 단계는 별개로 비결정성을 가져 같은 머신 + 같은 SDK + 같은 코드 입력에서도 `dotnet publish` 를 반복하면 SHA256 이 매번 달라진다.

PR-11 Tier-3 실측 — 같은 코드 + 같은 SDK + 같은 머신에서 `rm -rf bin/Release obj/Release && dotnet publish -r win-x64 -c Release` 를 3 회 실행한 결과:

```
ed6b2acee83b64c5dcb0afd1e639a029eb88f288364ff969e0e9b083a2da908f   # 1차
41d245fcb31ea4d6a439681d1e37ad22aa8855e8beb862e7d64dce236a829187   # 2차
a711de796d28523024681762d116a59ef39b349c5af6039ccaca3fe4e8a34d8e   # 3차
```

3 hash 모두 다름. PE 헤더의 timestamp 는 결정적이지만 ILC 가 생성하는 native code 안에 GUID / 내부 metadata slot 의 무작위 요소가 잔존하는 것으로 추정 (정확한 원인은 ILC 내부 — 본 노트의 스코프 초과).

**함의**:
- README "다운로드 검증" 섹션은 "정본 hash" 와 "본인이 빌드한 hash" 를 구분 명시. 사용자가 직접 빌드한 exe 와 GitHub Releases 첨부 exe 의 hash 는 다를 수 있다.
- [docs/release-procedure.md](../release-procedure.md) §3 + §4 의 강조: **publish 결과물을 재빌드 없이** GitHub Releases 에 그대로 첨부. 재빌드하면 첨부된 SHA256 과 새 binary 가 어긋난다.
- 본 비결정성이 해결되면 (.NET 11+ 또는 ILC 의 deterministic 모드 노출) 본 노트 갱신 + README/release-procedure 의 "정본 hash" 표현을 "재현 가능한 hash" 로 강화 가능.

## 함의 — UpdateChecker 와의 관계

[App/Update/UpdateChecker.cs](../../App/Update/UpdateChecker.cs) 는 GitHub API 의 `releases/latest` 만 조회하고 다운로드 단계는 사용자 브라우저에 위임한다 — SHA256 자동 검증을 앱 안에서 구현해 두 단계 (보고 → 검증 → 교체) 로 늘리진 않는다. 본 PR 의 사용자 책임 패러다임 그대로 유지.

향후 만약 자동 다운로드 + 자가 교체 기능을 도입하면 그 시점에서 SHA256 자동 비교는 필수 (네트워크 전송 중 MITM / 캐시 변조 방어). 그러나 그 기능 자체가 "포터블 단일 exe" 정체성과 충돌하므로 도입 검토 시 자가 교체 + 사인 + SHA256 자동 검증을 한 묶음으로 평가해야 한다.
