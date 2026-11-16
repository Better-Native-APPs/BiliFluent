# BiliFluent.Mpv.RelayCheck

`BiliFluent.Mpv` 里回环中转（`Relay/LoopbackMediaRelay`）的命令行自检。它**不在解决方案里**，不参与任何构建、打包和发布，也不引入新的包；只在需要时单独运行。

## 运行

在仓库根目录，用 `global.json` 指定的 SDK：

```powershell
$out = Join-Path $env:TEMP 'bf-relaycheck\'
dotnet build src\Libs\BiliFluent.Mpv.RelayCheck\BiliFluent.Mpv.RelayCheck.csproj -c Debug "-p:BuildOutDir=$out"
dotnet "$out\BiliFluent.Mpv.RelayCheck\bin\BiliFluent.Mpv.RelayCheck.dll" all
```

`BuildOutDir` 把输出放到仓库外面，不指定时输出会落在项目目录下的 `_build`。

最后一行是 `RESULT mode=<模式> passed=<数> failed=<数>`，有失败时退出码为 1。

## 模式

| 模式 | 内容 | 需要 |
|---|---|---|
| `unit` | Range 换算、令牌校验、候选轮换、速率统计、请求头解析 | 无 |
| `relay` | 中转对着本机假节点：Range 与 seek、请求头、响应中途换节点、连不上/错误状态/读到一半断开/连上不出数据时自行轮换、总长度不一致、整体换地址表、内存预置、背压、注销与释放 | 回环网络 |
| `all` | `unit` + `relay` | 回环网络 |
| `smoke` | 无窗口无声音的 libmpv 经中转播放一条匿名公开流：起播、seek、视频与音频换节点、坏节点、换地址表，全程采样 `time-pos` | 外网、`libmpv-2.dll` |

`smoke` 的参数：

```powershell
dotnet "$out\BiliFluent.Mpv.RelayCheck\bin\BiliFluent.Mpv.RelayCheck.dll" smoke dll=src\Desktop\BiliCopilot.UI\Assets\libmpv\x64\libmpv-2.dll
```

- `dll=`（必填）：libmpv 的路径。
- `bvid=`、`cid=`：换一个视频。
- `tls=off`：不校验上游证书。

`smoke` 不使用任何账号，播放地址是未登录访客拿到的那一份。它依赖真实的 CDN 节点，个别节点临时不可用时会看到中转自行轮换的输出，这不算失败。
