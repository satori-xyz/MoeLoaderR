# Windows 安装包

安装 Inno Setup 6.5 或更新版本，运行：

```powershell
.\packaging\Build-Installer.ps1
# 编译器不在默认位置时：
.\packaging\Build-Installer.ps1 -IsccPath 'C:\Tools\Inno Setup\ISCC.exe'
```

脚本先发布自包含单 EXE，再生成 `packaging/bin/MoeLoaderR-<版本>-Setup.exe`。
版本读取 `Directory.Build.props`。构建不会提交代码或发布 GitHub Release。

- 当前用户安装，安装向导始终显示目录选择，支持其他盘符和中文目录。
- 默认 `%LOCALAPPDATA%\Programs\MoeLoaderR`；请选择当前用户有写入权限的目录，供现有程序内更新使用。
- 同目录覆盖安装保留配置；阻止旧安装包覆盖较新的程序。重复安装默认沿用上次目录；改目录不是自动迁移，原目录中的文件不会被删除。
- 创建开始菜单快捷方式，可选桌面快捷方式，提供 Windows 卸载入口。
- 配置继续保存在 `%APPDATA%\MoeLoaderR`；卸载只移除安装器管理的文件，保留配置、下载图片和其他用户文件。
- 安装包只包含发布的 EXE 和许可证，不包含 tests、日志、本机配置或构建工具。
- GitHub 发布时仍需同时保留 `MoeLoaderR.exe` 供现有更新客户端使用；安装包是额外附件，不取代该文件。

`Languages/ChineseSimplified.isl` 来源于 Inno Setup 官方仓库的简体中文翻译，原作者信息保留在文件头：
https://github.com/jrsoftware/issrc/blob/main/Files/Languages/ChineseSimplified.isl
