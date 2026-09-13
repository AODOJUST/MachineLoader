# Machine Loader - 日本語

Aviassembly ゲーム用の Mod ローダー（Minecraft の Forge/Fabric と同様）

## 機能

- **Mod 管理** - メインメニューの Mod ボタンから Mod を管理
- **Mods フォルダ** - ゲームディレクトリに mods フォルダを自動作成
- **16 の内蔵 Mod** - 戦闘システム、レーダー、ミサイル、音声アラートなど
- **自動更新** - SHA-256 + RSA-3072 署名検証付きの自動更新システム
- **マルチプレイ** - オンライン/LAN ルーム対応
- **多言語対応** - 7 言語のドキュメント

## インストール

1. [Releases](https://github.com/AODOJUST/MachineLoader/releases) から `MachineLoader-2.5.0.zip` をダウンロード
2. 解凍して `MachineInstaller.exe` を実行
3. インストーラーが自動的に Aviassembly を検出
4. インストールパスを確認してインストール
5. ゲームを起動 - メインメニュー左下に `Machine v2.5.0` が表示されれば成功

## クイックスタート

### Mod のインストール
1. Mod の DLL ファイルを `ゲームディレクトリ/mods/` に配置
2. ゲームを再起動
3. メインメニューの Mod ボタンから有効/無効を切り替え

### 独自 Mod の開発
[Mod 開発ガイド（英語）](../en/mod-development.md) を参照してください。

## 内蔵 Mod 一覧

| Mod | 説明 |
|-----|------|
| BattleCore | 戦闘情報ハブ（依存関係） |
| BattleHold | 戦闘倉庫管理 |
| CombatBay | 戦闘カーゴベイ |
| FactionSystem | 3 陣営 AI システム |
| FlightTrails | 飛行軌跡表示 |
| GMeter | G 力計算 |
| GVision | 戦闘ヘッドアップディスプレイ |
| KillFeed | イベントキルフィード |
| MachineAAM | 空対空ミサイル + 機関砲 |
| MachineShop | 独立カーゴショップ UI |
| OptiMod | CPU パフォーマンス最適化 |
| Radar | 多段階レーダー + 射撃管制ロック |
| VoiceAlerts | 中日バイリンガル音声アラート |
| ZoomMod | 画面ズーム |

## ドキュメント

- [README（英語）](../en/README.md)
- [Mod 開発ガイド（英語）](../en/mod-development.md)
- [デバッグガイド（英語）](../en/debugging.md)
- [API リファレンス](../api/README.md)
- [サンプル Mod](../../examples/)

## 貢献

[貢献ガイド（英語）](../../CONTRIBUTING.md) を参照してください。

## ライセンス

MIT License - [LICENSE](../../LICENSE) を参照
