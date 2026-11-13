# 小橙看班联动 v2.1.0 —— 考试模式全局免打扰

> 考试进行中，ClassIsland 的上课/下课铃、自动化提醒全部静默；**地震预警永远放行**。

## 🆕 功能

- **考试模式自动识别**：沿用 `ExamMonitor` 读取的 ExamClock，每 20 秒轮询一次，
  当前时间落在某场考试的 `[开始, 结束)` 内即视为「考试中」。
- **高优先级提醒拦截**：注册一个优先级 `-100000` 的 `INotificationConsumer`，
  考试中吞掉除白名单外的所有提醒（上课铃、下课铃、自动化「请清理黑板」等）。
- **白名单放行**：默认放行「地震预警」（`B27C0AF3-C917-44DE-A61D-8010C3F3FB92`）。
- **来源识别**：反射读取 `NotificationRequest.NotificationSourceGuid` 与 `NotificationSource.Name`，
  日志会写明「来源=地震预警（GUID）」。

## 🔧 实现要点（ClassIsland 2.1.0.1 源码结论）

| 环节 | 做法 |
|---|---|
| 抢到提醒 | 消费者优先级 `-100000`（越小越优先），且仅在考试中「接受提醒」 |
| 识别来源 | `NotificationSourceGuid` / `NotificationSource` 是 internal，用反射读取 |
| 白名单放行 | `ticket.Cancel()` 会把提醒退回主机，移交给下一个消费者（MainWindowLine）播放 |
| 静默拦截 | 取消提醒的 `CompletedTokenSource` → 标记已播放完成且不再派发 |

## ⚙️ 配置项（settings.json）

| 配置 | 默认 | 说明 |
|---|---|---|
| `examDndEnabled` | `true` | 考试中是否拦截提醒 |
| `examDndWhitelistProviderGuids` | `["B27C0AF3-C917-44DE-A61D-8010C3F3FB92"]` | 白名单（默认地震预警） |
| `examDndDryRun` | `false` | 干跑：只记录来源、不拦截（联调用） |
| `examDndForceActive` | `false` | 强制视为考试中（联调用，正式请关） |
| `examDndSelfTest` | `false` | 开机自检：8 秒后自动投递两条测试提醒 |

## ✅ 验证（本机实测）

开启 `examDndForceActive=true` + `examDndSelfTest=true` 后的日志：

```
[调课中枢] 考试静默已注册（优先级 -100000，开关=True，干跑=False，白名单=[B27C0AF3-…]）
[调课中枢] 考试静默：来源=未知提供方（11111111-2222-3333-4444-555555555555）→ 拦截
[调课中枢] 考试静默：来源=地震预警（b27c0af3-c917-44de-a61d-8010c3f3fb92）→ 放行（白名单）
```

- 普通来源被拦截 ✅
- 地震预警被放行 ✅（来源 ID 与名称都正确解析）

## ⚠️ 注意事项

- 来源字段是 **internal**，本功能依赖反射；ClassIsland 升级大版本后需要复测（日志会暴露问题）
- 考试识别依赖 `ExamClock` 数据（与考试提醒同源）；没有考试数据时不会误拦截
- 白名单可加多个 GUID（例如学校自己的紧急通知插件）
