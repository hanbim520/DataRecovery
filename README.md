<p align="center">
  <img src="src/DataRecovery.App/Assets/datarecovery-logo.svg" width="112" alt="DataRecovery Logo" />
</p>

<h1 align="center">DataRecovery</h1>

<p align="center">
  使用 C#、.NET 9 和 Avalonia 构建的跨平台、只读数据恢复桌面工具。
</p>

## 项目简介

DataRecovery 面向 U 盘、移动硬盘、本地 SSD/HDD 和原始磁盘镜像，提供设备选择、扫描、候选文件筛选以及安全恢复工作流。界面采用三步恢复向导，支持同时选择多个数据源，并明确区分“已删除文件”和“原路径未知的特征扫描候选”。

> DataRecovery 当前处于可运行的 MVP 阶段。重要数据应首先制作整盘镜像，源设备应立即停止写入。对于物理损坏、异响或大量坏扇区的设备，请优先交由专业数据恢复机构处理。

## 主要功能

- 枚举 U 盘、移动硬盘、本地 SSD/HDD 和已挂载卷。
- 同时勾选多个磁盘或镜像，依次执行扫描。
- 打开 `.img`、`.dd`、`.raw`、`.bin`、`.iso` 磁盘镜像。
- 识别 FAT12、FAT16、FAT32、exFAT、NTFS、Ext2 和 Ext3。
- 解析 exFAT 删除目录项，恢复仍保留元数据的连续文件。
- 保留 exFAT 删除文件的原文件名、准确长度和根目录信息。
- 按文件特征查找 JPEG、PNG、GIF、PDF、ZIP/Office 候选数据。
- 使用全部可用逻辑 CPU 进行并行分析。
- 采用单路顺序读盘、8 MB 数据块和 64 KB 边界重叠，兼顾机械硬盘与 SSD。
- 扫描结果实时分批显示，支持进度、取消、分类和搜索。
- 支持照片、文档、压缩包、程序及其他类型筛选。
- 同名恢复文件自动改名，不覆盖目标目录已有文件。
- 多设备结果记录各自源盘，恢复时自动从正确的数据源读取。
- 顶部设置菜单可选择扫描范围，默认使用“已删除文件”。
- 源盘以只读共享方式打开，扫描过程不会主动写入源设备。

## 扫描类型

| 扫描类型 | 说明 | 适用场景 |
| --- | --- | --- |
| 已删除文件（默认） | 读取文件系统中仍保留的删除目录项 | 希望保留原文件名、大小和目录信息 |
| 丢失文件 | 扫描原始扇区中的文件头和文件尾 | 目录元数据已经损坏或丢失 |
| 全部类型 | 组合删除目录项与文件特征扫描 | 希望进行更全面但耗时更长的扫描 |

“丢失文件”并不表示已确认删除。它表示扫描器找到了文件特征，但无法确定原文件名和原始路径；候选内容也可能是缩略图、旧数据或其他文件中的嵌入资源。

## 文件系统支持状态

| 文件系统 | 格式识别 | 删除目录项恢复 | 文件特征扫描 |
| --- | :---: | :---: | :---: |
| FAT12 / FAT16 / FAT32 | ✅ | 计划中 | ✅ |
| exFAT | ✅ | ✅ 连续文件 | ✅ |
| NTFS | ✅ | 计划中 | ✅ |
| Ext2 / Ext3 | ✅ | 计划中 | ✅ |

当前“已删除文件”模式的元数据恢复仅完整支持 exFAT 连续文件。FAT 删除目录项、NTFS MFT 和 Ext inode 解析尚未实现；这些格式目前可识别，并可使用“丢失文件”特征扫描。

## 安全原则

1. 删除文件后立即停止使用源设备。
2. 不要将 DataRecovery 安装到丢失数据所在磁盘。
3. 不要把恢复结果保存回源磁盘。
4. 优先扫描磁盘镜像，而不是反复读取故障设备。
5. Windows 原始卷通常需要管理员权限；Linux/macOS 设备节点通常需要 root 或磁盘读取权限。

## 环境要求

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows、Linux 或 macOS
- 读取物理卷所需的管理员/root 权限

## 运行

```powershell
dotnet restore
dotnet run --project src/DataRecovery.App
```

普通权限下也可以打开并扫描已有的磁盘镜像文件。

## Windows 一键编译

项目根目录提供 [build.bat](build.bat)：

```bat
build.bat
build.bat Debug
build.bat publish
build.bat publish Debug
```

- `build.bat`：恢复依赖、Release 编译并运行测试。
- `build.bat Debug`：执行 Debug 编译和测试。
- `build.bat publish`：额外发布 Windows x64 自包含版本。
- 发布输出：`artifacts\publish\win-x64`。

## 手动测试与发布

```powershell
dotnet test DataRecovery.slnx -c Release

dotnet publish src/DataRecovery.App -c Release -r win-x64 --self-contained true
dotnet publish src/DataRecovery.App -c Release -r linux-x64 --self-contained true
dotnet publish src/DataRecovery.App -c Release -r osx-x64 --self-contained true
```

## 项目结构

```text
DataRecovery/
├─ src/
│  ├─ DataRecovery.App/          Avalonia UI、主题和 MVVM 工作流
│  └─ DataRecovery.Core/         文件系统识别、扫描与恢复引擎
├─ tests/
│  └─ DataRecovery.Core.Tests/   格式识别和恢复行为测试
├─ tools/                        Logo 等开发辅助脚本
├─ build.bat                     Windows 一键编译/发布脚本
├─ LICENSE                       MIT 开源许可证
└─ README.md
```

## 已知限制

- 文件特征扫描无法可靠恢复原文件名、原目录和时间戳。
- 特征候选可能包含误报、缩略图或文件内部嵌入资源。
- 文件碎片化、覆盖、压缩和加密会降低恢复成功率。
- 大于当前扫描块且文件尾不在同一数据块中的候选可能被标记为“部分”。
- SSD 执行 TRIM 后，被删除的数据可能已经清零，通常无法恢复。
- 暂不支持 RAID 重组、坏扇区重试、NTFS 压缩/加密、Ext 日志重放和完整目录树重建。

## 开发路线

- FAT12/16/32 删除目录项和簇链恢复。
- NTFS MFT、父目录和 Data Runs 解析。
- Ext2/3 inode、目录项和块映射恢复。
- 碎片文件重组和文件内容校验。
- 磁盘镜像制作、断点续扫和坏扇区策略。
- 扫描会话保存及结果导出。

## 参与贡献

欢迎提交 Issue、测试镜像构造方案、文件系统解析改进和 Pull Request。涉及恢复算法的修改应同时添加自动化测试，并坚持源设备只读原则。

## 开源许可证

本项目采用 [MIT License](LICENSE) 开源。你可以自由使用、复制、修改、合并、发布和分发本软件，但必须保留原始版权及许可证声明。

本软件按“原样”提供，不附带任何明示或默示担保。数据恢复具有不确定性，使用者应自行承担操作风险。
