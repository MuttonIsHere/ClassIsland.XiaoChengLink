# ClassIsland × 小橙看班：完美实现无感消息同步与全屏播报联动

> 让 ClassIsland 长出一双“看得到小橙看班”的眼睛：消息提醒、全屏播报、作业通知，一出现就同步弹出——**不用接口、不搞逆向、不装 OCR**。

## 它解决什么问题

教室大屏上，小橙看班负责消息/播报；ClassIsland 负责课表/提醒。两边各干各的，老师和学生总得抬头看两个屏幕。
这个插件把两边接了起来：**小橙看班一有动静，ClassIsland 立刻弹出提醒，并带上真实标题/正文**。

## 三个硬核点

1. **无接口联动**：小橙看班没有公开 API，插件只读 Win32 窗口信息（标题/类名）和它自己的本地 Jet 缓存库，不注入、不修改进程。
2. **无 OCR 看画面**：小橙看班是 Delphi 自绘、UIA 读不到文字；插件用 `PrintWindow` 把窗口缩成 40×40 亮度网格做哈希，超过阈值就触发——鼠标划过不会误报。
3. **全屏播报瞬时抓取**：实测「全屏播报」会往 `WelcomeWord` 表写一行，**约 14 秒后删除**。插件在触发后等 1–2 秒、重试 4 次并重新复制实时库，再用“窗口类名→表映射 + 30 秒新鲜度”精准取正文，不会把旧消息串进来。

## 安装（2 分钟）

1. 打开 Release 下载 `XiaoChengLink.cipx`：
   <https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink/releases>
2. ClassIsland → 设置 → 插件 → 从本地安装 / 拖入 `.cipx`；
3. 重启 ClassIsland；
4. 启动小橙看班（`D:\inArtBoard\inArt_PC.exe`）。

> ⚠️ 请让 **ClassIsland 主界面保持可见**：隐藏主界面时提醒会排队不显示（ClassIsland 自身行为），重新显示时会补弹。

## 可调参数（设置 → 插件 →「小橙看班联动」）

| 你的感受 | 调什么 |
|---|---|
| 太灵敏（鼠标划过也弹） | 画面变化阈值 0.4 → 1.0 / 2.0 |
| 太迟钝（新消息不弹） | 画面变化阈值 0.4 → 0.2 |
| 太吵（连续弹） | 全局提醒节流 12 → 30 / 60 秒 |
| 正文偶尔为空 | 正文新鲜度 30 → 60 秒 |
| 只想看通用提示 | 关闭「从本地数据库读取消息正文」 |

## 技术流水线

```
Win32 窗口监控 → 画面哈希比对（40×40）→ 触发（冷却+节流）
→ 等 1–2 秒重试 → 32 位 PowerShell 只读 Jet 库
→ 窗口类名→表映射（FMTfrmText2Voice → WelcomeWord）
→ 30 秒新鲜度过滤 → ClassIsland 提醒（遮罩 + 真实正文）
```

## 链接

- 仓库：<https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink>
- Release：<https://github.com/MuttonIsHere/ClassIsland.XiaoChengLink/releases>
- 问题反馈：欢迎提 Issue，带上小橙看班版本 + 插件日志（`data\Config\Plugins\xiaocheng.link\logs\`）。

> 本项目为社区原型，仅做只读联动；小橙看班与 ClassIsland 的版权归各自作者所有。
