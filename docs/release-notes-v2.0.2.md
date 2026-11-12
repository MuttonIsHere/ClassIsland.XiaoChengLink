# 小橙看班联动 v2.0.2 —— 调课联动

> 老师在小橙看班发「全屏播报 / 班级通知」，内容写「下午第三节语文改数学」，大屏也能自动改课表了。

## 🆕 新增

- **调课联动**：读到小橙看班数据库正文后，转发给「QQ 调课同步」（QqClassSync）插件，
  由对方统一解析（正则 / AI）并修改 ClassIsland 临时层课表、弹出提醒。
- **两种触发**：
  1. 窗口提醒 / 画面变化触发时**立即转发**；
  2. **兜底轮询**：`tiaokeLinkPollSeconds`（默认 30 秒，0 = 关闭）主动读库转发，
     避免不弹窗的通知被漏掉。
- **解耦方式**：进程内反射调用 `QqClassSync.Bridge.TiaokeBridge.Submit(text, source)`，
  不编译期引用对方程序集；对方未安装时静默跳过（每分钟最多记一条日志），不影响本插件其它功能。
- **去重**：按「时间 + 发送人 + 正文」10 分钟内只转发一次，避免监控重复读到同一行记录。
- **新鲜度放宽**：兜底轮询按「3 个轮询周期」取消息，避免刚好卡在两个轮询之间错过。

## 🔧 配置

```json
{
  "tiaokeLinkPollSeconds": 30,
  "readMessageBodyFromDatabase": true,
  "messageBodyFreshSeconds": 30
}
```

- `tiaokeLinkPollSeconds`：兜底轮询间隔（秒），0 = 关闭（只保留弹窗触发路径）
- `readMessageBodyFromDatabase`：总开关，关掉后不再读数据库正文，联动也随之停止

## ✅ 验证

- 与 QQ 调课同步 1.3.0.0 同时加载：`xiaocheng.link 2.0.2.0` + `qq.classsync 1.3.0.0`
- 真机实测：在小橙看班发消息 → 转发日志出现 → 大屏弹「【来源：小橙看班】」待确认 → `#确认` 后课表翻转

## 🚀 升级

1. 下载 `XiaoChengLink.cipx` 覆盖安装
2. 重启 ClassIsland（与 QQ 调课同步一起升级时只需重启一次）

## ⚠️ 说明

- 兜底轮询会复制 `Data.idb` 并运行读取脚本，课堂机觉得开销大时把 `tiaokeLinkPollSeconds` 调大或设 0
- 两个插件独立发布，请保持 `qq.classsync 1.3.0+` 与 `xiaocheng.link 2.0.2+` 配套
