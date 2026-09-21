# 贡献指南

感谢你对 Machine Loader 的兴趣！我们欢迎各种形式的贡献，包括代码、文档、翻译、示例 Mod 和 Bug 报告。

[English](CONTRIBUTING_EN.md) | [返回首页](README.md)

## 目录

- [行为准则](#行为准则)
- [如何贡献](#如何贡献)
- [开发环境搭建](#开发环境搭建)
- [代码规范](#代码规范)
- [提交规范](#提交规范)
- [Pull Request 流程](#pull-request-流程)
- [报告 Bug](#报告-bug)
- [功能请求](#功能请求)
- [Mod 开发](#mod-开发)
- [文档贡献](#文档贡献)
- [翻译贡献](#翻译贡献)

---

## 行为准则

参与本项目即表示你同意：

- 尊重所有参与者，保持友善和专业
- 接受建设性的批评
- 关注对社区最有利的事情
- 对其他成员表示同理心

 unacceptable behavior 包括骚扰、侮辱性言论、人身攻击等，将被项目维护者拒绝或删除。

---

## 如何贡献

你可以通过以下方式贡献：

1. **报告 Bug**：提交 Issue，描述问题和复现步骤
2. **功能请求**：提交 Issue，说明你想要的功能和原因
3. **提交代码**：Fork 仓库，修改后提交 Pull Request
4. **完善文档**：修正错别字、补充说明、翻译文档
5. **开发 Mod**：创建有趣的 Mod 并分享给社区
6. **回答问题**：在 Issues 中帮助其他用户

---

## 开发环境搭建

### 前置要求

- Windows 10/11
- Aviassembly 游戏（Steam）
- .NET Framework 4.x（含 csc 编译器）
- Python 3.6+（用于更新脚本和测试）
- Git
- 文本编辑器（VS Code / Visual Studio / Rider）

### 克隆仓库

```bash
git clone https://github.com/AODOJUST/MachineLoader.git
cd MachineLoader
```

### 目录结构

```
MachineLoader/
├── src/                    # 源代码
│   ├── Machine.Core/       # 加载器核心
│   └── Mods/               # 官方 Mod 源码
├── templates/              # Mod 模板
├── examples/               # 示例 Mod
├── docs/                   # 文档
│   ├── zh/                 # 中文文档
│   ├── en/                 # 英文文档
│   └── api/                # API 参考
├── tests/                  # 测试
│   ├── unit/               # 单元测试
│   └── integration/        # 集成测试
├── .github/
│   ├── workflows/          # CI/CD
│   └── ISSUE_TEMPLATE/     # Issue 模板
├── dist_upload/            # 发布脚本
├── build-core.ps1          # 核心编译脚本
├── build-mods.ps1          # Mod 编译脚本
├── build_release.py        # 发布包构建
├── newmod.ps1              # Mod 模板生成器
├── README.md
├── CONTRIBUTING.md
├── SECURITY.md
├── CHANGELOG.md
└── LICENSE
```

### 编译

```powershell
# 编译核心
powershell -ExecutionPolicy Bypass -File build-core.ps1

# 编译所有 Mod
powershell -ExecutionPolicy Bypass -File build-mods.ps1

# 编译单个 Mod
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
    /target:library /out:bin/mods/MyMod.dll `
    /reference:Aviassembly_DEV/Aviassembly_Data/Managed/Assembly-CSharp.dll `
    /reference:Aviassembly_DEV/Aviassembly_Data/Managed/Machine.Core.dll `
    /reference:Aviassembly_DEV/Aviassembly_Data/Managed/UnityEngine.dll `
    src/Mods/MyMod/MyMod.cs
```

### 运行测试

```bash
# 安装依赖
pip install -r requirements.txt

# 运行所有测试
pytest tests/ -v

# 运行单元测试
pytest tests/unit/ -v

# 运行集成测试
pytest tests/integration/ -v

# 代码规范检查
black --check .
ruff check .
```

---

## 代码规范

### C# 规范

- 使用 4 空格缩进，不使用 Tab
- 类名、方法名使用 PascalCase
- 私有字段使用 `_camelCase`
- 公共属性使用 PascalCase
- 常量使用 UPPER_SNAKE_CASE
- 接口名以 `I` 开头
- 每行不超过 120 字符
- 公共 API 必须有 XML 文档注释

```csharp
/// <summary>
/// 示例类，演示代码规范。
/// </summary>
public class ExampleClass
{
    private const int MAX_COUNT = 100;

    private int _currentCount;

    /// <summary>
    /// 当前计数。
    /// </summary>
    public int CurrentCount
    {
        get { return _currentCount; }
    }

    /// <summary>
    /// 增加计数。
    /// </summary>
    /// <param name="amount">增加的数量</param>
    public void Increment(int amount)
    {
        _currentCount += amount;
    }
}
```

### Python 规范

- 遵循 PEP 8
- 使用 black 格式化（行宽 100）
- 使用 ruff 进行 lint
- 类型提示可选但推荐

### 配置文件

- JSON 使用 2 空格缩进
- mod.json 必须包含所有必需字段
- 配置文件使用 UTF-8 编码（无 BOM）

---

## 提交规范

我们使用 [Conventional Commits](https://www.conventionalcommits.org/) 规范：

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Type 类型

| Type | 说明 |
|------|------|
| `feat` | 新功能 |
| `fix` | Bug 修复 |
| `docs` | 文档变更 |
| `style` | 代码格式（不影响功能） |
| `refactor` | 重构（既不修复bug也不添加功能） |
| `perf` | 性能优化 |
| `test` | 添加或修改测试 |
| `build` | 构建系统或外部依赖变更 |
| `ci` | CI/CD 配置变更 |
| `chore` | 杂项（不修改src或test） |
| `revert` | 回退提交 |

### 示例

```
feat(radar): 添加 AESA 五级雷达

- 新增 AESA 五级雷达（无死角水平扫描）
- 探测范围 21000m，重量 50，占用 200
- 实时探测与锁定

Closes #123
```

```
fix(core): 修复 Mod 加载时的空引用异常

- 在 LoadConfig 中添加 null 检查
- 添加单元测试覆盖该场景

Fixes #456
```

---

## Pull Request 流程

1. **Fork 仓库**：点击 GitHub 上的 Fork 按钮
2. **创建分支**：`git checkout -b feature/your-feature-name`
3. **提交修改**：遵循提交规范
4. **运行测试**：确保所有测试通过
5. **推送分支**：`git push origin feature/your-feature-name`
6. **创建 Pull Request**：在 GitHub 上创建 PR
7. **等待审查**：项目维护者会审查你的代码
8. **修改反馈**：根据审查意见进行修改

### Pull Request 要求

- [ ] 代码遵循项目规范
- [ ] 所有测试通过
- [ ] 新功能有对应的测试
- [ ] 文档已更新（如需要）
- [ ] CHANGELOG.md 已更新（如需要）
- [ ] 提交信息遵循 Conventional Commits
- [ ] 一个 PR 只做一件事，避免无关修改

### 审查标准

- **正确性**：代码是否正确实现了功能
- **安全性**：是否引入了安全漏洞
- **性能**：是否有性能问题
- **可维护性**：代码是否清晰、易于维护
- **测试覆盖**：是否有足够的测试
- **文档**：是否有相应的文档

---

## 报告 Bug

### 提交前检查

- [ ] 已搜索现有 Issues，确认不是重复问题
- [ ] 已使用最新版本测试，确认问题仍然存在
- [ ] 已尝试在纯净环境中复现

### Bug 报告应包含

1. **标题**：简洁明了地描述问题
2. **环境信息**：
   - Machine 版本
   - 游戏版本
   - 操作系统
   - 已安装的 Mod 列表
3. **复现步骤**：详细的步骤，让维护者能复现
4. **预期行为**：你期望发生什么
5. **实际行为**：实际发生了什么
6. **日志**：`Machine/logs/Machine.log` 的相关部分
7. **截图/视频**：如适用，附上截图或视频
8. **崩溃报告**：如崩溃，附上 `crash_report_*.txt`

使用 [Bug 报告模板](.github/ISSUE_TEMPLATE/bug_report.md) 提交。

---

## 功能请求

### 提交前检查

- [ ] 已搜索现有 Issues，确认不是重复请求
- [ ] 已确认该功能不在当前开发计划中

### 功能请求应包含

1. **标题**：简洁明了地描述功能
2. **问题描述**：这个功能解决什么问题
3. **解决方案**：你期望的实现方式
4. **替代方案**：你考虑过的其他方案
5. **附加信息**：截图、参考链接等

使用 [功能请求模板](.github/ISSUE_TEMPLATE/feature_request.md) 提交。

---

## Mod 开发

如果你想开发 Mod 并分享给社区：

1. 阅读 [Mod 开发指南](docs/zh/mod-development.md)
2. 参考 [示例 Mod](examples/)
3. 使用 [模板生成器](newmod.ps1) 创建项目骨架
4. 在 Mod 目录中包含：
   - `mod.json`（必需）
   - `README.md`（推荐）
   - `CHANGELOG.md`（推荐）
   - 编译后的 DLL
5. 将 Mod 打包为 ZIP 分享

### Mod 发布检查清单

- [ ] mod.json 字段完整正确
- [ ] 版本号已更新
- [ ] 配置文件有合理默认值
- [ ] README 包含功能说明和使用方法
- [ ] CHANGELOG 记录本次更新
- [ ] 依赖声明正确
- [ ] 在纯净环境中测试通过
- [ ] 无明显性能问题（`/machine diag` 检查）

---

## 文档贡献

我们欢迎文档贡献，包括：

- 修正错别字和语法错误
- 补充缺失的说明
- 改进代码示例
- 添加新的教程
- 更新过时的内容

文档位于 `docs/` 目录，分为：
- `docs/zh/` - 中文文档
- `docs/en/` - 英文文档
- `docs/api/` - API 参考

提交文档 PR 时，请同时更新中英文版本（如适用）。

---

## 翻译贡献

我们希望 Machine 的文档能支持多种语言。如果你想贡献翻译：

1. 选择要翻译的文档
2. 在对应语言目录中创建翻译文件
3. 保持与原文相同的结构和格式
4. 翻译时注意专业术语的一致性
5. 在 PR 中说明翻译的文档和语言

目前支持的语言：
- 中文（简体）
- English

计划支持的语言：
- 日本語
- 한국어
- Русский
- Deutsch
- Français
- Nederlands

---

## 获得帮助

如果你在贡献过程中遇到问题：

1. 搜索现有 Issues
2. 阅读 [文档](docs/)
3. 查看 [示例 Mod](examples/)
4. 在 Discussion 中提问
5. 提交 Issue 寻求帮助

---

## 致谢

感谢所有为 Machine Loader 做出贡献的人！

你的每一个贡献，无论大小，都让这个项目变得更好。

---

**再次感谢你的贡献！** 🎉
