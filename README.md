# ITVCoffin（驱入虚空棺材）

一个**本地抓包工具包**：通过本地中间人代理，捕获《驱入虚空》官服的网络流量，把你自己账号的数据保存到本地，便于个人备份/分析。

> 本工具包**不包含任何游戏本体文件**（无 DLL、无 bundle、无贴图/音频等游戏资源），只包含代码、脚本和文档。所有默认路径均为相对路径。

---

## 目录结构

```
ITVCoffin/
├─ README.md                 本文件
├─ .gitignore
├─ config.example.json       代理配置示例（复制为 config.json 使用）
├─ requirements.txt          Python 依赖（UnityPy）
├─ run_capture.bat           一键启动代理（相对路径）
├─ proxy/                    抓包代理（.NET 9 控制台程序）
│   ├─ ITVCoffin.Proxy.csproj
│   ├─ Program.cs            参数/配置解析、启动横幅、并行运行 HTTP+TCP
│   ├─ CaptureProxy.cs       TCP + HTTP 中间人代理、逐帧日志
│   └─ Framing.cs            帧/契约编解码（与客户端一致）
├─ patcher/                  客户端补丁器（dnlib，修改 Assembly-CSharp.dll）
│   ├─ ITVCoffin.Patcher.csproj
│   └─ Program.cs            IP/端口/HTTP 重定向 + RSA 校验绕过 + Lua 覆盖
└─ scripts/                  Python 脚本
    ├─ patch_client.py       一条龙：定位 → 提取 → 补丁 → 重打包 → 安装
    ├─ extract_bundles.py    扫描 CacheFiles，自动发现并导出 hotupdate bundle
    ├─ repack_hotupdate.py   把补丁后的 DLL 重新打包回 bundle
    ├─ apply_bundle.py       安装 bundle 到游戏缓存（备份 + 重建 __info）
    └─ parse_capture.py      解析抓包日志，导出服务端下行数据
```

---

## 原理简述

1. **补丁器**修改客户端的 `Assembly-CSharp.dll`：把所有官服 IP 改成 `127.0.0.1`、端口改成 `25313`、HTTPS 域名改成 `http://127.0.0.1:18080`，并跳过登录签名校验。
2. **代理**在本机监听 TCP `25313` 与 HTTP `18080`：
   - 客户端的 TCP 流量被转发到真正的官服；
   - HTTP 请求（含登录）被转发到真正的官方 HTTPS 域名；
   - 对 `/server` 返回 `{"ips":[]}`，强制客户端继续使用本地代理。
3. 代理把每一帧流量写入 `captures/tcp_<时间戳>.log`，`parse_capture.py` 可解析并导出。

补丁后的客户端**只能连本地代理**（因为 IP 已写死为 127.0.0.1）。

---

## 环境要求

| 组件 | 版本 | 说明 |
|---|---|---|
| Windows | 10/11 x64 | 脚本与补丁器面向 Windows |
| .NET SDK | **9.0**（可选） | 仅**从源码构建**时需要；发布版压缩包已内置自包含 exe，无需安装 .NET |
| Python | **3.9+** | 运行 scripts/ |
| UnityPy | `pip install -r requirements.txt` | 读写 Unity AssetBundle |
| 游戏客户端 | 官方 CN 版 | 已安装并能正常启动 |

> **发布版（Release 压缩包）**：已包含 `proxy\publish\ITVCoffin.Proxy.exe` 与
> `patcher\publish\ITVCoffin.Patcher.exe`（Windows x64 自包含，无需安装 .NET）。
> `run_capture.bat` 与 `patch_client.py` 会自动优先使用它们——你只需要安装 Python 依赖：
>
> ```bat
> pip install -r requirements.txt
> ```

首次使用前，先安装依赖：

```bat
cd ITVCoffin
pip install -r requirements.txt
```

---

## 完整使用步骤

### 0. 准备

- 找到**游戏目录**：包含 `IntoTheVoid_Data` 文件夹的那一级，例如
  `...\IntoTheVoid\Game\IntoTheVoid`。
- 关闭游戏。**不要**同时开着"检查完整性/修复游戏"。

### 1. 打补丁（一条龙）

在 `ITVCoffin` 目录下：

```bat
python scripts\patch_client.py --game "<游戏目录>"
```

脚本会依次执行：

```
[1/5] 扫描 CacheFiles，定位 hot-update bundle
[2/5] 复制原始 bundle 并导出 Assembly-CSharp.dll
[3/5] 用 ITVCoffin.Patcher 打补丁（IP/端口/HTTP/RSA）
[4/5] 把补丁后的 DLL 重新打包回 bundle（并回读校验）
[5/5] 安装到游戏缓存（自动备份 __data / __info 为 .bak，重建 __info）
```

> 想先看看会发生什么、但不改动游戏？加 `--dry-run`：
> ```bat
> python scripts\patch_client.py --game "<游戏目录>" --dry-run
> ```
> 它会跑完 1~4 步并停在安装前。

**分步执行**（等价于上面，适合排错）：

```bat
python scripts\extract_bundles.py --game "<游戏目录>" --out work
python patcher\...  # 见"分步补丁"一节
python scripts\repack_hotupdate.py work\hotupdate_original.bundle work\Assembly-CSharp.patched.dll work\hotupdate_patched.bundle
python scripts\apply_bundle.py work\hotupdate_patched.bundle "<游戏目录>"
```

### 2. 启动代理

**方式 A（推荐）**：双击 `run_capture.bat`。它会自动优先使用已发布/已编译的 exe，找不到才 `dotnet run`。

**方式 B（手动）**：在 `ITVCoffin` 目录下任选其一：

```bat
REM B1. 用 dotnet run（无需预先编译）
dotnet run --project proxy\ITVCoffin.Proxy.csproj -c Release

REM B2. 先编译，再运行生成的 exe
dotnet build proxy\ITVCoffin.Proxy.csproj -c Release
proxy\bin\Release\net9.0\ITVCoffin.Proxy.exe

REM B3. 发布为独立目录后运行（可拷贝给别人）
dotnet publish proxy\ITVCoffin.Proxy.csproj -c Release -o proxy\publish
proxy\publish\ITVCoffin.Proxy.exe
```

首次运行 `dotnet run` 会自动还原并构建。启动后应看到类似横幅：

```
=========================================================
  ITVCoffin (驱入虚空棺材) — capture proxy
=========================================================
  TCP listen    : 0.0.0.0:25313
  TCP upstream  : 1.13.127.58:30531
  HTTP listen   : 127.0.0.1:18080
  HTTP upstream : cweb.jinzhangshu.com / official.jinzhangshu.com
  capture dir   : <ITVCoffin>\captures
  Press Ctrl+C to stop.
```

**保持这个窗口开着**，然后启动游戏。

### 3. 启动游戏并正常游玩

- 启动游戏 → 登录 → 进入主界面 → 打开背包/角色/商城/任务等各界面 → 做你想记录的操作。
- 代理窗口会实时打印 `[C->S]` / `[S->C]` 帧日志。

### 4. 捕获文件位置

```
ITVCoffin\captures\tcp_<yyyyMMdd_HHmmss>.log
```

每行格式（解析入口，勿改）：

```
<HH:mm:ss.fff> [C->S|S->C] Data type=<Type> id=<id> route='<route>' dataLen=<n> data=<HEX>
```

### 5. 解析

```bat
python scripts\parse_capture.py
```

- 默认读取 `captures\` 下**最新**的 `tcp_*.log`；
- 打印所有帧的表格；
- 把每条非空的服务端下行 payload 导出到 `captures\parsed\*.bin`。

指定文件/目录：

```bat
python scripts\parse_capture.py --captures captures --log captures\tcp_20260101_120000.log
```

---

## 分步补丁（可选，用于排错）

```bat
REM 1) 定位并导出原始 bundle（会同时复制 hotupdate_original.bundle）
python scripts\extract_bundles.py --game "<游戏目录>" --out work

REM 2) 从 bundle 里取出 Assembly-CSharp.dll（work\Assembly-CSharp.dll）

REM 3) 打补丁
dotnet patcher\bin\Release\net9.0\ITVCoffin.Patcher.dll work\Assembly-CSharp.dll work\Assembly-CSharp.patched.dll
REM    发布版：patcher\publish\ITVCoffin.Patcher.exe work\Assembly-CSharp.dll work\Assembly-CSharp.patched.dll
REM    也可用：dotnet run --project patcher\ITVCoffin.Patcher.csproj -c Release -- work\Assembly-CSharp.dll work\Assembly-CSharp.patched.dll

REM 4) 重打包
python scripts\repack_hotupdate.py work\hotupdate_original.bundle work\Assembly-CSharp.patched.dll work\hotupdate_patched.bundle

REM 5) 安装（自动备份 + 重建 __info）
python scripts\apply_bundle.py work\hotupdate_patched.bundle "<游戏目录>"
```

---

## 配置

复制 `config.example.json` 为 `config.json` 后按需修改（也可用命令行参数覆盖）：

```json
{
  "tcpPort": 25313,
  "httpPort": 18080,
  "officialTcpHost": "1.13.127.58",
  "officialTcpPort": 30531,
  "httpHost": "cweb.jinzhangshu.com",
  "officialHost": "official.jinzhangshu.com",
  "captureDir": "captures"
}
```

命令行覆盖示例：

```bat
run_capture.bat --tcp-port 35313 --http-port 28080 --capture-dir my_captures
```

> 注意：如果你改了 `tcpPort`/`httpPort`，补丁器写入客户端的端口是**固定的 25313/18080**。
> 除非你同时改补丁器，否则请保持默认端口。

---

## 注意事项（重要）

1. **不要点"检查完整性 / 修复游戏 / 更新"**：启动器会重新下载原始 bundle，把补丁覆盖掉，需要重新执行第 1 步。
2. **原文件已备份**：`apply_bundle.py` 会把原始文件备份为 `__data.bak` / `__info.bak`（只备份一次，不会覆盖）。
   想还原：把 `.bak` 复制回 `__data` / `__info` 即可。
3. **补丁后的客户端只能连本地代理**：因为 IP 已写死为 `127.0.0.1`。**必须先启动代理**，否则游戏无法登录。
4. **端口**：代理占用 TCP `25313` 与 HTTP `18080`。若被占用，代理会启动失败并打印 `BIND FAILED`。
5. **只抓自己的流量**：代理只是转发，不修改游戏逻辑；请勿用于任何破坏游戏公平性的用途。
6. **杀毒软件**可能拦截 dnlib 补丁行为或代理监听，必要时添加信任。

---

## 故障排查

| 现象 | 原因 / 处理 |
|---|---|
| 代理打印 `BIND FAILED ... 25313/18080` | 端口被占用。关掉占用进程，或改用备用端口（需同步改补丁器）。 |
| 游戏卡在登录 / 连不上 | 代理没开，或补丁没生效。先启动代理，再确认第 1 步成功。 |
| `patch_client.py` 报 "could not locate CacheFiles" | `--game` 指错了。应指向**包含 `IntoTheVoid_Data` 的目录**。 |
| `ERROR: UnityPy is not installed` | 运行 `pip install -r requirements.txt`。 |
| 补丁后启动器又更新了 | 启动器覆盖了补丁。重新执行第 1 步。 |
| 找不到 `Assembly-CSharp.dll` TextAsset | 游戏版本差异或缓存不完整；先让游戏正常启动一次以下载完整缓存。 |
| 想恢复原版 | 把 `__data.bak` / `__info.bak` 复制回 `__data` / `__info`。 |

---

## 免责声明

- 本工具仅供**个人数据备份与学习研究**使用，用于捕获**你自己账号**的流量。
- 本仓库**不包含任何游戏资源/游戏本体文件**，也不提供任何破解、作弊或修改游戏数据的功能。
- 使用本工具可能违反游戏用户协议，请自行评估风险并遵守相关条款。因使用本工具产生的一切后果由使用者自行承担。
- 请勿将捕获到的数据用于商业用途或侵犯他人隐私。
