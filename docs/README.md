# Machine 文档中心

> Machine Mod Loader 完整文档。

[返回项目首页](../README.md)

## 文档导航 / Documentation Navigation

### 🌍 多语言文档 / Multilingual Documentation

| 语言 / Language | 项目介绍 / Intro | 开发指南 / Dev Guide | 调试指南 / Debug |
|-----------------|-------------------|----------------------|-------------------|
| 🇨🇳 中文 / Chinese | [README](zh/README.md) | [Mod 开发指南](zh/mod-development.md) | [调试指南](zh/debugging.md) |
| 🇺🇸 English | [README](en/README.md) | [Mod Development](en/mod-development.md) | [Debugging Guide](en/debugging.md) |
| 🇯🇵 日本語 / Japanese | [README](ja/README.md) | - | - |
| 🇰🇷 한국어 / Korean | [README](ko/README.md) | - | - |
| 🇷🇺 Русский / Russian | [README](ru/README.md) | - | - |
| 🇩🇪 Deutsch / German | [README](de/README.md) | - | - |
| 🇫🇷 Français / French | [README](fr/README.md) | - | - |
| 🇳🇱 Nederlands / Dutch | [README](nl/README.md) | - | - |

> 注：开发指南和调试指南目前提供完整的中英文版本，其他语言版本正在翻译中。开发时请参考英文版。
> Note: Development and debugging guides are currently available in full Chinese and English. Other language versions are being translated. Please refer to the English version for development.

### 中文文档

| 文档 | 说明 | 适合人群 |
|------|------|---------|
| [README](zh/README.md) | 项目介绍、安装、目录结构、常见问题 | 所有人 |
| [Mod 开发指南](zh/mod-development.md) | 从零到发布的完整开发教程 | 开发者 |
| [调试工具指南](zh/debugging.md) | 日志、诊断、性能分析、崩溃报告 | 开发者/玩家 |

### English Documentation

| Document | Description | Audience |
|----------|-------------|----------|
| [README](en/README.md) | Project intro, installation, structure, FAQ | Everyone |
| [Mod Development Guide](en/mod-development.md) | Complete tutorial from zero to publish | Developers |
| [Debugging Guide](en/debugging.md) | Logs, diagnostics, profiler, crash reports | Developers/Players |

### API 参考 / API Reference

| 文档 | 说明 |
|------|------|
| [API Reference](api/README.md) | 完整 API 文档（核心接口、事件系统、内容定义、日志、性能分析、对象池等） |

### 示例 / Examples

| 示例 | 说明 | 难度 |
|------|------|------|
| [HelloWorld](../examples/HelloWorld/) | 最小示例：基本结构和日志输出 | ⭐ |
| [AddButton](../examples/AddButton/) | 在主菜单添加自定义按钮和窗口 | ⭐⭐ |
| [AddCargo](../examples/AddCargo/) | 注册自定义货物 | ⭐⭐ |
| [ListenEvents](../examples/ListenEvents/) | 订阅事件总线的各种事件 | ⭐⭐ |
| [ReadConfig](../examples/ReadConfig/) | 读取和保存自定义配置文件 | ⭐⭐ |
| [综合模板](../templates/ExampleMod/) | 完整示例：事件+UI+配置+命令 | ⭐⭐⭐ |

## 快速链接

### 玩家

- [安装 Machine](zh/README.md#玩家安装-machine)
- [安装 Mod](zh/README.md#玩家安装-mod)
- [常见问题](zh/README.md#常见问题)
- [性能优化](zh/debugging.md#63-调试-onupdate-性能问题)

### 开发者

- [环境准备](zh/mod-development.md#1-环境准备)
- [创建第一个 Mod](zh/mod-development.md#2-创建第一个-mod)
- [mod.json 字段说明](zh/mod-development.md#4-modjson-字段说明)
- [生命周期](zh/mod-development.md#5-生命周期)
- [事件系统](zh/mod-development.md#6-事件系统)
- [UI 开发](zh/mod-development.md#7-ui-开发)
- [配置系统](zh/mod-development.md#8-配置系统)
- [内容注册](zh/mod-development.md#9-内容注册)
- [调试技巧](zh/debugging.md)
- [打包发布](zh/mod-development.md#11-打包发布)
- [最佳实践](zh/mod-development.md#12-最佳实践)

### 高级

- [API 参考](api/README.md)
- [性能分析器](api/README.md#性能分析)
- [对象池系统](api/README.md#对象池)
- [依赖与冲突管理](zh/mod-development.md#依赖与加载顺序)
- [版本范围语法](zh/mod-development.md#版本范围语法)

## 核心特性一览

### 加载器核心
- 事件总线（12 种事件类型）
- Mod 生命周期（OnLoad → OnEnable → OnUpdate → OnDisable → OnUnload）
- 依赖管理（必需/可选依赖、版本范围、循环依赖检测）
- 冲突检测（冲突 Mod 自动禁用）
- 加载顺序（拓扑排序）

### 性能与稳定性
- 异常隔离（单个 Mod 异常不影响其他 Mod）
- 自动禁用（连续异常超过 10 次自动禁用）
- 帧预算（每个 Mod 每帧最多 2ms）
- 性能分析器（初始化时间、每帧耗时、峰值）
- 内存泄漏检测（事件订阅、对象池、托管内存）
- 日志轮转（5MB 切分，保留 5 个）
- 崩溃报告（自动生成，包含最后 200 行日志）

### 联机功能
- 局域网房间（IP:端口 直连）
- 网络房间（房间号加入，大厅浏览）
- Mod 校验（自动检测游戏版本和 Mod 列表一致性）
- 玩家同步（飞机位置、模型、状态实时同步）
- 玩家系统（UID/RID/OID 权限分级）

## 版本信息

- **当前版本**：2.3.0
- **API 版本**：1.0
- **最低加载器版本**：2.0.0
- **更新日志**：[CHANGELOG.md](../CHANGELOG.md)

## 贡献

欢迎贡献代码、文档、翻译或示例 Mod！

- 提交 Bug：[GitHub Issues](https://github.com/AODOJUST/MachineLoader/issues)
- 贡献代码：提交 Pull Request
- 完善文档：直接修改 docs/ 目录下的文件
- 翻译文档：添加新的语言版本

## 联系

- GitHub：[https://github.com/AODOJUST/MachineLoader](https://github.com/AODOJUST/MachineLoader)
- 问题反馈：[GitHub Issues](https://github.com/AODOJUST/MachineLoader/issues)
