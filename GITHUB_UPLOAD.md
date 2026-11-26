# GitHub 仓库说明

仓库地址：https://github.com/ygao1981/BWG-Top-Bar

仓库已创建。要在 Windows 本地继续开发，请克隆仓库：

```powershell
git clone https://github.com/ygao1981/BWG-Top-Bar.git
cd BWG-Top-Bar
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-win.ps1
```

修改后使用 `git add`、`git commit`、`git push` 提交，并在暂存前检查文件。仓库中的 `publish-github.ps1` 仅适用于同名仓库尚不存在时的首次创建；**现有仓库不要再运行此脚本**。

## 自动构建

推送 `main` 后，GitHub Actions 的 `Windows build` 工作流尝试生成 Windows x64 独立 EXE。成功后在工作流 Artifacts 下载 `BWG-Top-Bar-win-x64`。

构建成功不代表已在真实 Windows 和 KiwiVM 账号上完成运行验证。

## 安全

仓库目前是公开的。请勿提交 VEID、API Key、SSH 私钥、Windows 凭据导出或本地用户配置。程序使用 Windows 凭据管理器保存 API Key。`.gitignore` 不能清除历史提交中的秘密；如果误提交，请立即撤销相应密钥。
