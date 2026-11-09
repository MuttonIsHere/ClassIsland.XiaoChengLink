// ─── 插件入口 ───────────────────────────────────────────────────────────────
// [PluginEntrance] + PluginBase.Initialize(HostBuilderContext, IServiceCollection)
// 均已由研究文档（research/classisland-api/classisland-2.1-plugin-api.md §4）用 Mono.Cecil
// 核对过 2.1.0.1 二进制签名；注意 2.1.0.1 的 PluginBase **没有** OnShutdown，不要照抄旧文档。
//
// 初始化时机：宿主构建主机的 ConfigureServices 阶段（IHost.StartAsync 之前、UI 起来之前）。
// 因此这里只做「注册服务 + 读配置 + 写日志」，不做阻塞/弹窗/长耗时操作。
//
// 发提醒路径（Lead 修正 + 研究文档 §5.3 实测结论）：
//   注册自己的 NotificationProviderBase 子类 → 监控服务通过 ClassIslandNotifier 调用它的
//   ShowNotification/ShowNotificationAsync。不能从插件调用 INotificationHostService.ShowNotification
//   （该重载不可访问，csc 实测 CS0122），也没有“全局 INotificationSender”可注入。
//
// 异常安全：PluginService 会 try/catch 插件 Initialize 的异常（研究文档 §8.3），但这里仍然
// 自己 try/catch 一次并记日志，保证即使配置/日志初始化失败也不会把异常交给宿主。
//
// 【需实测】
//   · services.AddHostedService<WindowTextMonitor>() 注册的 hosted service 是否在宿主启动时
//     被执行。研究文档 §4.3 的结论是“能，随 IHost.StartAsync 一起启动”（标准 .NET DI 行为），
//     但本机尚未启动 ClassIsland 真机验证。
//   · 若真机上 hosted service 未被启动，备用方案（不采用，仅注释）：
//       ClassIsland.Core.AppBase.Current.AppStarted += (_, _) => Task.Run(监控循环);
//     即订阅宿主的 AppStarted 事件后自己起后台 Task（需要 using ClassIsland.Core + System.Threading.Tasks）。

using System;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XiaoChengLink.Logging;
using XiaoChengLink.Monitoring;
using XiaoChengLink.Notification;
using XiaoChengLink.Settings;

namespace XiaoChengLink;

/// <summary>小橙看班联动插件入口。</summary>
[PluginEntrance]
public sealed class XiaoChengLinkPlugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        try
        {
            // 1) 日志（写入 PluginConfigFolder/logs/yyyyMMdd.log）
            PluginLog.Init(PluginConfigFolder);
            // 供不依赖 PluginBase 实例的组件（如 LocalMessageReader）获取插件目录/配置目录。
            PluginPaths.PluginConfigFolder = PluginConfigFolder;
            try
            {
                PluginPaths.PluginFolder = Info?.PluginFolderPath;
            }
            catch
            {
                PluginPaths.PluginFolder = null;
            }

            // 2) 配置：<PluginConfigFolder>/settings.json，缺失时写默认值
            var options = MonitorOptions.Load(PluginConfigFolder);

            // 3) 注册插件自身实例，便于其它服务注入
            services.AddSingleton(this);

            // 4) 配置与监控组件
            services.AddSingleton(options);
            services.AddSingleton<WindowScanner>();
            services.AddSingleton<UiaTextReader>();
            services.AddSingleton<KeywordMatcher>();

            // 本地数据库正文读取器（调用 32 位 PowerShell 辅助脚本读取 Data.idb）
            services.AddSingleton<LocalMessageReader>();

            // 5) 提醒提供方：注册元数据 + 可注入单例 + IHostedService（宿主启动时构造/启动）
            AddSharedNotificationProvider<XiaoChengNotificationProvider>(services);

            // 6) 提醒封装（内部使用上面的提供方实例）
            services.AddSingleton<ClassIslandNotifier>();

            // 7) 后台监控服务（随 IHost.StartAsync 启动；内部全部 try/catch）
            services.AddHostedService<WindowTextMonitor>();

            // 8) 设置页：设置 → 插件 → 「小橙看班联动」
            //    AddSettingsPage<T>() 读取 T 上的 [SettingsPageInfo]（id/name/分类），
            //    把页面注册为 keyed transient；页面构造函数参数由 DI 解析。
            //    设置页与监控共用上面注册的同一个 MonitorOptions / WindowScanner 单例：
            //      · 保存配置 → 写回 options → TrySave(settings.json) → scanner.ReloadTitlePatterns()
            //      · 因此标题正则/进程名/轮询间隔等无需重启宿主即可生效（关键词除外，见设置页注释）。
            //    单独 try/catch：设置页注册失败时其它功能（提醒/监控）仍应正常工作。
            try
            {
                services.AddSettingsPage<XiaoChengLinkSettingsPage>();
            }
            catch (Exception ex)
            {
                PluginLog.Error("注册设置页失败（插件其它功能继续工作）", ex);
            }

            PluginLog.Info(
                "小橙看班联动插件初始化完成：" +
                $"进程=[{string.Join(", ", options.ProcessNames)}] " +
                $"标题正则=[{string.Join(", ", options.WindowTitlePatterns)}] " +
                $"关键词={options.Keywords.Length}个 模式={options.KeywordMatchMode}");
        }
        catch (Exception ex)
        {
            // 绝不让插件初始化异常炸宿主：记录下来，宿主会继续启动（插件功能不可用）。
            try
            {
                PluginLog.Error("插件初始化异常（已捕获，宿主继续启动）", ex);
            }
            catch
            {
                // 日志都失败时只能放弃。
            }
        }
    }

    /// <summary>
    /// 注册提醒提供方，并让“宿主启动的实例”与“可注入单例”是同一个对象。
    ///
    /// 背景（实测 2.1.0.1 IL）：AddNotificationProvider&lt;T&gt;() 的实现是
    ///     Register(services, typeof(T), null); services.AddHostedService&lt;T&gt;();
    /// 而 AddHostedService&lt;T&gt;() 只登记 IHostedService -&gt; T（实现类型），**不会**把 T 本身
    /// 登记为单例，因此其它服务无法通过构造函数注入 T（DI 会报找不到服务）。
    /// 这里移除 AddHostedService 添加的描述符，改成“T 单例 + IHostedService 工厂解析同一实例”，
    /// 既保留宿主启动时构造/StartAsync 提供方的行为，又让监控服务能注入到同一个实例。
    /// </summary>
    private static void AddSharedNotificationProvider<TProvider>(IServiceCollection services)
        where TProvider : NotificationProviderBase
    {
        services.AddNotificationProvider<TProvider>();

        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(TProvider))
            {
                services.RemoveAt(index);
            }
        }

        services.AddSingleton<TProvider>();
        services.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredService<TProvider>());
    }
}
