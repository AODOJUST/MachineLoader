# Machine Loader 安全与可靠性说明

本文档说明 v2.4.0 起 Machine 加载器在**更新链路**上的信任模型、已经加固的环节，
以及**明确不防护**的场景。目的：让使用者和二次开发者知道哪些承诺是被代码保证的。

---

## 1. 威胁模型

Machine 会读写两类可执行文件：

- `Aviassembly_Data/Managed/Assembly-CSharp.dll`（注入启动钩子）
- `Aviassembly_Data/Managed/Machine.Core.dll`（加载器本体）

因此"更新通道"天然等价于"远程代码执行通道"。v2.4.0 的设计目标是：
**即使更新仓库、更新清单被完全控制，攻击者也不能让客户端安装一个非官方签名的 DLL。**

### 信任根

发布公钥（RSA-3072）**编译进客户端**，位于：

- `machine_common.py` -> `PUBKEY_MODULUS_HEX / PUBKEY_EXPONENT_HEX`
- `Machine.Core.dll`（`Machine.Core.ReleaseKey`）

两处由 `tools/sign_release.py` 从同一份私钥自动写入，不会漂移。

`Machine/update.json` 和仓库里的 `version.json` **都不是信任根**：它们只能表达
"用哪个仓库/分支/通道"，不能改变"只有持私钥者签名的 DLL 才被接受"这一条。

---

## 2. 更新链路做了什么校验

| 环节 | 位置 | 失败时的行为 |
| --- | --- | --- |
| 更新清单字段 schema | `ConfigGuard.ReadUpdate` | 非法字段丢弃并回落默认值，写日志 |
| 下载地址白名单 | `MachineUpdate.IsAllowedCoreUrl` | 非 `https` 或非 GitHub 域名一律拒绝 |
| 通道过滤 | `MachineUpdate.Check` | `channel=stable` 时不接受 `beta` 发布 |
| 下载内容 SHA-256 | `MachineUpdate.Download` / `machine_update.py` | 不一致 → 删除文件、报错、不落盘 |
| 下载内容 RSA 签名 | `MachineUpdate.Download`（游戏内）+ `machine_update.py`（应用前） | 验签失败 → 拒绝，`Managed/` 不被修改 |
| 写入前再校验一次 | `MachineInstaller.exe --expect-sha256` | 不一致 → 退出码 3，一个字节都不写 |
| 旧 DLL 备份 | `Machine/backup/Machine.Core.dll.<时间戳>.bak` | 保留最近 5 份 |
| 原子替换 | 同目录 `.new` → `File.Replace`/`Move` | 不会出现"覆盖到一半"的半截 DLL |
| 写入后复核 | 重新计算 `Managed/Machine.Core.dll` 的哈希 | 不一致 → 自动回滚到备份 |
| 安装器是否被替换 | `Machine/installed.json` 记录安装时的哈希 | 变化时告警并要求确认才继续 |

**签名与哈希是两次独立检查**，签名证明"来源"，哈希证明"内容完整"，
两者都通过才会写入。

---

## 3. 可靠性做了什么加固

- **返回码一律检查**：安装器/卸载器返回非零时脚本不再打印"完成"。
  退出码语义固定：`1` 参数/环境、`2` 哈希不符、`3` 签名被拒、`4` 安装器失败、
  `5` 已回滚、`6` 用户取消、`7` 游戏在运行、`8` 权限不足。
- **异常全捕获**：`FileNotFoundError` / `PermissionError` / `TimeoutExpired` / `OSError`
  都有对应提示，不会把裸栈甩给用户。任何未预期异常也会写进日志并给出可读摘要。
- **路径探测**：更新器按 6 个候选路径找 `MachineInstaller.exe`，找不到时打印
  试过的全部路径（旧版本的"dist 兜底"用了错误目录，等于没有兜底）。
- **游戏运行检测**：用 `tasklist` + 独占打开测试双重判断；支持 `--wait N` / `--kill`。
- **权限检测**：对目标目录做真实写入探测，不可写时提示需要管理员权限。
- **全量日志**：脚本写 `Machine/logs/machine_update.log`，安装器写
  `Machine/logs/installer.log`。
- **配置文件 schema 校验**：`net.json`（server 必须是合法 IP/主机名、port 1..65535、
  昵称 1-16 位）、`update.json`（repo 形如 `owner/repo`、branch 字符集、channel 白名单）、
  `profile.json`（uid 11 位数字、rid 5 位数字、oid 字符集、playTimeSeconds 有上下界）。
  非法值不会让程序带着脏数据继续跑。
- **不毁玩家数据**：卸载不再递归删除 `mods/`（旧版 `shutil.rmtree(mods)` 会直接
  删掉玩家自己的 Mod）；卸载前把 `profile.json` / 日志等备份到
  `Machine_uninstalled_<时间戳>/`。
- **README 编码**：由程序生成的 `Machine/README.txt` 一律 UTF-8 **无 BOM**，
  且只写相对路径，不再出现开发机绝对路径。

---

## 4. 明确的非目标 / 剩余风险

以下场景**不在**本版本的防护范围内，请知悉：

1. **私钥泄露**。私钥在本机 `Machine_Dev/keys/release_private.pem`（不随发布包分发）。
   一旦泄露，攻击者可以签出"合法"的恶意 DLL。泄露后必须重新生成密钥对，
   并**发一次正常版本**把新公钥推给客户端（换公钥本身无法通过更新通道安全下发，
   这也是设计上把公钥编译进客户端的原因）。
2. **已获得本机管理员权限的攻击者**。可以任意改 `Managed/` 下的 DLL，
   任何客户端校验都拦不住。
3. **首次安装的发布包**。第一次安装时用户是从网页下载的发布包；这一步依赖
   HTTPS 与发布页本身的可信度。安装程序会校验包内 `version.json` 的 sha256 与签名
   （`Machine.Core.dll.sig`），但若用户从钓鱼站点拿到"恶意包（含攻击者自签的 DLL +
   攻击者公钥）"，客户端内置的是官方公钥，验签会失败并拒绝安装。
4. **`MachineInstaller.exe` 自身没有签名校验（自举问题）**。安装器负责"注入 + 替换 DLL"，
   它本身无法在被信任之前验证自己。缓解措施：安装时把安装器哈希写入
   `Machine/installed.json`，之后每次应用更新都比对，一旦变化就告警并需要用户确认；
   真正决定"能不能把 DLL 写进 `Managed/`"的仍然是签名校验，安装器即使被换掉，
   也无法让一个未签名的 DLL 通过 `machine_update.py` 的检查。若需要更严格的自举信任，
   应改用带 Authenticode 签名的安装器并由 Windows 验证（本版本未做）。
5. **`Assembly-CSharp.dll` 被 Steam 更新覆盖**。Steam 校验完整性会还原游戏本体，
   此时注入消失；这是预期行为，重装即可，不是安全问题。
6. **DLL 反向工程**。加载器未做混淆/加壳，DLL 可被反编译。这与"更新通道安全"是
   两个问题，本版本不处理。

---

## 5. 发布流程（维护者）

```text
1. 改代码 -> build-core.ps1 / build-installer.ps1        # 编译
2. python pack_dist.py                                   # 组装 dist/
3. python tools/sign_release.py --set-version X.Y.Z      # 统一版本号 + 签名 + 回写公钥
4. 核对 tools/sign_release.py 输出的公钥指纹与上次一致
5. 把 dist/ 内容同步到发布仓库（Machine.Core.dll / version.json / *.py / *.bat /
   MachineInstaller.exe / Mono.Cecil.dll / Machine/ / mods/）
```

发布前自检清单：

- [ ] `keys/release_private.pem` **没有**出现在发布包里
- [ ] `version.json` 的 `sha256` 与 `Machine.Core.dll` 实际哈希一致
- [ ] `version.json` 的 `version` 与客户端 `LOADER_VERSION` / `MachineLoader.Version` 一致
- [ ] `machine_common.py` 与 `Config.cs` 里的公钥指纹一致
- [ ] 卸载一次 + 重新安装一次，游戏能正常启动

`python tools/sign_release.py --check` 会用第 3、4 条做一致性检查。

### 密钥生成（一次性）

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out keys/release_private.pem
openssl rsa -in keys/release_private.pem -pubout -out keys/release_public.pem
```

---

## 6. 离线校验一个更新包

拿到 `Machine.Core.dll` 与 `version.json` 后，可以独立核对：

```bash
# 1) 哈希
certutil -hashfile Machine.Core.dll SHA256        # 与 version.json 的 sha256 比较

# 2) 签名（把 version.json 里 sig 字段去掉换行后 base64 解码成 sig.bin）
openssl dgst -sha256 -verify release_pubkey.pem -signature sig.bin Machine.Core.dll
# 期望输出: Verified OK
```

签名格式：**RSA-3072 / PKCS#1 v1.5 / SHA-256**，直接对 `Machine.Core.dll` 的字节签名。
