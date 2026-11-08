# 发版：GitHub Release + .cipx

1. 改版本号：`manifest.yml` 的 `version` 与 `XiaoChengLink.csproj` 的 `<Version>`（例如 `1.0.1.0`）。
2. 提交并推送：`git add -A && git commit -m "release: v1.0.1" && git push`。
3. 打 tag 并推送（触发发版）：`git tag v1.0.1 && git push origin v1.0.1`。
4. `.github/workflows/build.yml` 会在 `windows-latest` + .NET 8 上构建，生成 `cipx/XiaoChengLink.cipx`，并在 Actions 通过后自动创建同名 GitHub Release（附 `.cipx`，自动生成 release notes）。
5. 不发版只想拿包：Actions → Build & Release → Run workflow，从该次运行的 Artifacts 下载 `XiaoChengLink-cipx`（需登录 GitHub）。
6. 安装：ClassIsland → 设置 → 插件 → 从本地安装（或拖入 `.cipx`）→ 重启 ClassIsland。
7. 首次 Actions 构建尚未在本机验证（本机无 .NET SDK / NuGet）；若失败，先看 `Build (Release)` 日志和 `Post-build diagnostics` 步骤打印的 cipx/bin 目录。
