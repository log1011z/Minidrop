# MiniDrop v1.1

个人跨设备投递的使用体验更新。

- Windows 支持直接粘贴截图。
- 两端支持附件与文字一起发送，发送前可增删附件。
- 图片缩略图与应用内预览；Android 支持双指缩放、拖动、复位。远端图片按需下载。
- 正文支持选择局部文字、点击链接打开，无额外复制按钮。
- 文件支持下载并打开；Windows 增加复制文件和打开所在文件夹，Android 增加分享文件。
- Android 上传失败和等待重试时可手动重试，不显示失败原因。
- 刷新期间仍可发送；下载目录等本地偏好可离线保存。
- 改善长文字展开/收起、滚动、阅读位置保留、草稿保留、分享接收与上传恢复。

## 安装包

- `MiniDrop-1.1-windows-x64.exe`：Windows 免安装自包含版，无需额外安装 .NET。
- `MiniDrop-1.1-windows-x64-framework-dependent.exe`：Windows 精简版，需要 .NET Desktop Runtime 10。
- `MiniDrop-1.1-release.apk`：Android 正式签名 Release 版（versionCode 3），使用原签名以便覆盖旧正式版。

Windows 请先从托盘退出旧版，再替换程序。Android 正式版可覆盖相同签名的旧正式版；先前的 Debug 测试版不属于同一签名。

验证：Windows 101 项单元测试、Windows 界面与工作流检查、Android 52 项单元测试。Android 未进行真机验收。
