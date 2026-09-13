# Machine Loader - 한국어

Aviassembly 게임용 Mod 로더 (Minecraft의 Forge/Fabric와 유사)

## 기능

- **Mod 관리** - 메인 메뉴의 Mod 버튼으로 Mod 관리
- **Mods 폴더** - 게임 디렉토리에 mods 폴더 자동 생성
- **16개의 내장 Mod** - 전투 시스템, 레이더, 미사일, 음성 경보 등
- **자동 업데이트** - SHA-256 + RSA-3072 서명 검증이 포함된 자동 업데이트 시스템
- **멀티플레이** - 온라인/LAN 룸 지원
- **다국어 지원** - 7개 언어 문서

## 설치

1. [Releases](https://github.com/AODOJUST/MachineLoader/releases)에서 `MachineLoader-2.5.0.zip` 다운로드
2. 압축을 풀고 `MachineInstaller.exe` 실행
3. 설치 프로그램이 자동으로 Aviassembly를 감지
4. 설치 경로를 확인하고 설치
5. 게임 실행 - 메인 메뉴 왼쪽 하단에 `Machine v2.5.0`이 표시되면 성공

## 빠른 시작

### Mod 설치
1. Mod의 DLL 파일을 `게임 디렉토리/mods/`에 배치
2. 게임 재시작
3. 메인 메뉴의 Mod 버튼으로 활성화/비활성화 전환

### 자체 Mod 개발
[Mod 개발 가이드 (영어)](../en/mod-development.md)를 참조하세요.

## 내장 Mod 목록

| Mod | 설명 |
|-----|------|
| BattleCore | 전투 정보 허브 (의존성) |
| BattleHold | 전투 창고 관리 |
| CombatBay | 전투 화물칸 |
| FactionSystem | 3개 진영 AI 시스템 |
| FlightTrails | 비행 궤적 표시 |
| GMeter | G력 계산 |
| GVision | 전투 헤드업 디스플레이 |
| KillFeed | 이벤트 킬피드 |
| MachineAAM | 공대공 미사일 + 기관포 |
| MachineShop | 독립 화물 상점 UI |
| OptiMod | CPU 성능 최적화 |
| Radar | 다단계 레이더 + 사격통제 잠금 |
| VoiceAlerts | 중영 양어 음성 경보 |
| ZoomMod | 화면 줌 |

## 문서

- [README (영어)](../en/README.md)
- [Mod 개발 가이드 (영어)](../en/mod-development.md)
- [디버깅 가이드 (영어)](../en/debugging.md)
- [API 레퍼런스](../api/README.md)
- [예제 Mod](../../examples/)

## 기여

[기여 가이드 (영어)](../../CONTRIBUTING.md)를 참조하세요.

## 라이선스

MIT License - [LICENSE](../../LICENSE) 참조
