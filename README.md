# AsioBridge

把不支持 ASIO 的音乐 / 播放软件的声音捕获出来，转发到 ASIO 声卡播放。

适用于 DAW 监听、专业声卡低延迟回放、需要 ASIO 但播放器只有 WASAPI 的场景。

## 功能

- **按进程捕获**（推荐）：只抓取指定软件（及其子进程）的声音，系统提示音等不会混入
- **系统全局 Loopback**：捕获默认输出设备上的所有声音
- **ASIO 输出**：自动匹配采样率（48 / 44.1 / 96 kHz…），环形缓冲 + 自适应延迟裁剪
- **低延迟**：实测约 30 ms 缓冲窗口，可调 10–200 ms
- **深色紧凑 UI** + 系统托盘常驻

## 环境要求

| 项目 | 要求 |
|------|------|
| 系统 | Windows 10 2004+ / Windows 11（按进程捕获） |
| 运行时 | .NET 8 Desktop Runtime（或使用自包含单文件） |
| 驱动 | 任意 ASIO 驱动（专业声卡驱动 / ASIO4ALL / FL Studio ASIO 等） |

## 快速开始

1. 打开 `AsioBridge.exe`
2. 选择 **ASIO 设备**
3. 捕获模式选 **按进程**，下拉中选择目标软件  
   （列表为空时先让软件出声，再点「↻」刷新）
4. 点 **▶ 开始**

### 命令行

```text
AsioBridge.exe --list
AsioBridge.exe --start --driver "FL Studio ASIO" --pid 1234 --buffer 40
```

| 参数 | 说明 |
|------|------|
| `--list` | 列出 ASIO 驱动和当前出声进程 |
| `--start` / `-s` | 无界面启动 |
| `--driver` / `-d` | ASIO 驱动名 |
| `--mode` | `process`（默认）或 `system` |
| `--pid` / `-p` | 目标进程 PID（按进程模式） |
| `--buffer` | 缓冲毫秒，默认 50 |
| `--children` | `false` 时不包含子进程 |

## 构建

```powershell
dotnet build AsioBridge/AsioBridge.csproj -c Release

# 单文件 exe
dotnet publish AsioBridge/AsioBridge.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -o publish
```

## 架构

```
目标软件
   │  WASAPI Process Loopback (VAD\Process_Loopback)
   │  或 WASAPI System Loopback
   ▼
环形缓冲 (CircularSampleBuffer)
   │  自适应延迟裁剪
   ▼
采样率转换 (WDL Resampler，按需)
   ▼
ASIO 输出 (NAudio AsioOut)
```

## 常见问题

**按进程列表是空的？**  
先让目标软件播放一段声音，再点「↻」。进程只有在有音频会话时才会出现。

**有爆音 / 欠载？**  
把缓冲调大到 80–100 ms。ASIO 驱动的 buffer size 也会影响。

**和其他播放器抢声卡？**  
ASIO 通常是独占的。桥接期间默认声卡上的其他播放可能被中断，这是驱动行为。

**按进程捕获启动失败？**  
需要 Windows 10 2004+。失败时会自动回退到系统全局 Loopback。

## License

[MIT](LICENSE)
