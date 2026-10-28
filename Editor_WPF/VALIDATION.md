# 实现与验证记录

验证日期：2026-10-26。

- 目标：`G:/SuperMain/RemoteInspector`；WPF + 纯 .NET 核心。
- 依据：已完整阅读原模块 `G:/SuperMain/Client/Assets/ThirdPart/RemoteInspector/README.md`，并以实际协议及 Runtime setter 代码核对兼容行为。
- 修改范围：独立工程及已授权的 RemoteInspector Runtime / Shared 增量。旧 Editor、Prefab、Scene 和业务脚本没有写入。
- Unity MCP、编译和 Console：2026-10-26 双端扩展通过项目脚本写入工具及 Unity 编译验证，详情见末节；Prefab 验证不适用。
- CodeGraph：没有修改 `Assets/Game/Script`，不涉及业务符号迁移或兼容入口变更；不适用。
- 原模块已有未版本化 `.codex-workreport` 目录保持不变；本次修改和新增的 Runtime/Shared 源文件及 Unity 自动生成的 .meta 见末节。

## 构建与发布

SDK 10.0.301；Release solution 构建零警告、零错误。

`artifacts/portable-win-x64` 是 Windows x64 自包含发布，包含 Microsoft.NETCore.App 和 Microsoft.WindowsDesktop.App 10.0.9；运行无需安装 Unity 或额外的 .NET 运行时。分发整个文件夹，当前约 131 MiB。

核心程序集仅依赖 .NET；桌面工程只引用核心与 WPF 框架。没有 Unity 程序集或外部 NuGet 库依赖。框架安装包仍由 .NET SDK 的正常还原机制提供。

## 自动验证结果

186 项断言全部通过。日志见 `artifacts/verification/smoke-tests.log`，构建/发布输出见 `artifacts/verification/build-publish.log`。

- 报文固定头、小端序、拆包、粘包、截断和超长拒绝。
- Unity public 字段 JSON、Unicode、嵌套列表。
- 独立构造的二进制节点树 v1、v2 格式，以及 65536 节点计数。
- 独立构造的旧 RISNP v1 快照、17 字节文件头、导出和截断拒绝。
- 两个 TCP 客户端具有相同 InstanceID 时的缓存与请求隔离。
- 属性写入 ACK 后回读、RPC、超时、取消、断线清理、异常报文不会停止监听。
- Runtime 固定序列号 0 的纹理响应、两种图片分块方式。
- 多块和空文件上传；下载乱序拒绝、目标文件保留、部分文件清理。
- WPF 实际按钮经 TCP 执行属性写入、RPC 列表、目录浏览、截图。
- 三种激活状态、按 activeInHierarchy 计数/筛选、保留父级路径、未激活节点选中、刷新保留展开状态。
- WPF 修改 activeSelf 后重新获取整树，确认状态更新和选中位置保留。
- 属性按组件和材质槽分组，初始折叠材质、刷新保留折叠状态、属性筛选、全部展开及折叠。
- 元数据解析非连续及负数枚举常量，拒绝错误映射；布尔勾选/枚举下拉的实际 TCP 写回及回读。
- 数值、颜色弹出菜单不自动发送；材质颜色选值保留材质槽、字段名和协议类型。
- 附带 5148 个枚举定义的 JSON 字典；生成工具仅读取程序集元数据，发布程序只读取 JSON。
- 窗口退出覆盖：未监听、停止后关闭、监听中关闭、有连接与未完成请求、停止过程中立即关闭、重复关闭、配置保存失败。
- 并发停止与释放共享同一次清理，可重新监听；关闭释放端口，未完成请求立即结束。
- 两个独立 WPF 子进程使用正常 Application.Run 消息循环，分别在停止监听后和监听期间关闭，均以退出码 0 正常退出。

### 停止监听后关闭卡死修复

原代码在 Closing 回调中取消关闭，随后等待清理；已停止监听时 await 同步完成，直接调用 Close 导致 WPF 关闭重入异常。修复前已通过窗口事件测试复现，记录见 `artifacts/verification/shutdown-before-fix.log`。

现在最终关闭通过 Dispatcher 排队，在原 Closing 回调返回后执行；清理期间的重复关闭请求继续取消，避免绕过清理。停止按钮与窗口退出共享同一个停止任务，避免重复释放监听器和 CancellationTokenSource。清理或配置写入异常不会留下永久禁止关闭的窗口。测试窗口使用隔离的配置文件路径。
- 页面离屏渲染并检查截图：属性、未激活筛选、节点禁用、日志、FPS、RPC、文件和图片。

节点状态与视觉样例见：

- `artifacts/verification/desktop-inspector.png`
- `artifacts/verification/desktop-inactive-filter.png`
- `artifacts/verification/desktop-node-disabled.png`
- `artifacts/verification/desktop-property-groups.png`
- `artifacts/verification/desktop-material-properties.png`

## 本次布局与交互优化

- 搜索清除恢复滚动位置；选过搜索结果后清除仍定位该节点；根与子节点顺序严格保留远端顺序。
- Camera LayerMask 读取工程 TagManager 导出的层名，多选写入及回读；保留空槽和第 31 位。
- 向量单轴编辑保留其他分量，布尔勾选通过真实 TCP 写回。
- 目录双击、相对路径输入、面包屑父路径与根路径跳转；右键菜单以指针所在行为目标。
- Inspector、日志和文件区同时显示，实际 WPF 控件矩形验证无重叠；选中日志显示详情。
- 停止监听不再被正在执行的目录或属性请求阻挡。
- 已检查 desktop-workspace.png、desktop-camera-layers.png、desktop-search-restored.png 等离屏截图。

## 尚未覆盖

测试使用模拟 Runtime，未启动真实游戏或 Unity Editor。真实游戏包/手机、实际旧工具产生的快照、真实超大场景及具体业务 RPC 仍需按 README 的验收步骤联调。本次不据此宣称真实设备兼容性已经验收完成。

## 调试工作流优化验证

- 新增 DebugTools.cs；修改 MainWindow.xaml、MainWindow.xaml.cs、ViewModels.cs 和定向交互测试，未修改 Unity 工程或协议。
- 已读取并遵循独立工程 README；布局、字段编辑和快照仍使用已有 WPF / Core 架构。Unity MCP、CodeGraph 和序列化事件迁移不适用。
- 实际 WPF + 模拟 TCP 验证：节点收藏、历史前进后退、身份信息、编辑标记、基准差异、还原和远端刷新。
- 监视固定、读取和移除保持 Inspector 目标；布局预设改变分区；RPC 模板恢复准确参数且不自动执行。
- 自动刷新确实读取远端新详情，存在未应用编辑时暂停；日志跟随可暂停与恢复。
- 现场包包含快照、日志、帧数据和采集信息；解压快照可重新读取目标节点详情。
- 视觉检查 desktop-debug-tools.png：较少使用的工具收进折叠区，节点、属性、日志与文件区互不重叠。
- 收藏、基准、监视与历史为本次运行内数据；RPC 模板和分隔布局写入本机配置。真实设备联调仍未进行。

## 2026-10-26 双端扩展验证

- 读取独立模块 README、原 RemoteInspector README、unity-mcp-orchestrator 技能和工程内 MCP 技能的脚本/事件安全说明。
- Unity 写入通过 unity.script.write；原有 script_apply_edits 路由返回实例不可用后，确认实例和最小状态，改用项目专用写入工具。未通过直接文件编辑绕过 Unity 写入要求。
- Runtime 修改：ComponentCollector.cs（新增只增不减的可读纹理元数据）、MessageHandler.cs（新增命令分发和详情标记）；新增 InspectionExtensions.cs、Shared/Protocol/InspectionProtocol.cs，.meta 由 Unity 生成。没有修改旧 MonoBehaviour 回调、签名、程序集、Prefab 或 Scene。
- 影响范围在 ThirdPart/RemoteInspector，未修改 Assets/Game/Script，CodeGraph 索引不覆盖该模块；以目标源码和调用链检查替代。旧消息入口和图像替换命令均保留。
- Unity 2022.3.24f1 编译与 Domain Reload 完成；通过 execute_code 在隔离 PreviewScene 验证 3D 拾取和 NGUI UI_3D 命中。临时对象和 PreviewScene 均在 finally 销毁；未进入业务 Play Mode。
- Unity 内可读纹理标记测试：Texture2D 初始可读时返回 ID；Apply(false,true) 后不再返回；非法坐标被拒绝。
- Console 游标在 Domain Reload 后重置，回退读取当前日志；仅有无关 ImportTools 提示，未发现本次源码错误。
- .NET 回归覆盖 9555 默认端口、图标区分、Uniform 留白拒绝/坐标翻转、灰色禁用纹理按钮、弹窗解码、原尺寸传输、真实 BqLog 样本解码与取消、远端压缩日志双击预览、拾取结果路径和保持远端树序。
- 新截图：desktop-texture-popup.png、desktop-compressed-log.png、desktop-pick-results.png。旧实时协议及关闭生命周期回归一并通过。
- 未进行真实手机游戏包联调；需要更新游戏包后验收实际屏幕、业务 UI、压缩纹理格式和持续增长日志。uGUI、2D 物理分支已通过编译，尚未完成真实 UI 场景验证。

## 截图拾取主线程错误修复（2026-10-26）

- 根因：新增 0x0092 截图拾取和 0x0094 原尺寸纹理导出漏入 RemoteInspectorClient.RequiresMainThread，网络接收线程直接执行 Unity API。
- 修复：在现有私有分发判断中补齐两个命令，复用现有主线程队列，不修改 MonoBehaviour 生命周期或任何旧接口。Runtime 数据采集与处理源码的中文编码损坏也已按原始补丁恢复，恢复前确认与编码转换后的补丁完全一致，没有后续修改。
- Unity 编译和 Domain Reload 完成，ready_for_tools=true。未改桌面端，未修改 Prefab 或 Scene 资产，无需重新发布桌面程序。
- 回归使用隔离 PreviewScene、未激活的临时客户端和本机 TCP：从 Task.Run 后台线程调用 HandlePacket，断言两个请求都进入队列且没有提前写响应；主线程 ProcessMainThreadTasks 执行后收到 success=true 的拾取响应和有效 PNG 纹理响应。临时对象、连接与 PreviewScene 在 finally 清理。
- 回归源码保存在 artifacts/verification/runtime-thread-regression.cs.txt，当前 Console 未发现 RemoteInspector 相关错误。无需重复运行未变更的 WPF 全套测试。
- 修复文件：Client/Assets/ThirdPart/RemoteInspector/Runtime/Core/RemoteInspectorClient.cs；编码恢复文件为 Runtime/Core/MessageHandler.cs 与 Runtime/Collectors/ComponentCollector.cs。CodeGraph 不覆盖该 ThirdPart 模块；旧入口完整保留。
- 已打包设备需要重新构建包含修复 Runtime 的游戏包；Unity Editor 需重新进入运行并连接后验证实际截图。

## 节点自动刷新与日志 Tab 调整

- 依据独立工程 README，修改 MainWindow.xaml、MainWindow.xaml.cs、MediaTools.cs，新增 CompressedLogView.cs，更新定向 WPF 测试。未修改游戏端或 Unity 资产，Unity MCP / CodeGraph 不适用。
- 刷新节点树按钮移到场景层级收藏按钮旁；CLIENT_HELLO 完成后每个会话自动请求一次树，不抢占全局操作，非当前会话仅更新自己的缓存。
- 远端文件与压缩日志改为同一区域的 Tab；双击日志自动切页，切回后保留目录。日志按会话隔离，替换日志、切换会话或退出时取消任务并清理缓存。
- 关键字上一处/下一处、无匹配、刷新、完整文本导出及 Tab 切换都通过实际 WPF + 模拟 TCP 回归。额外断言自动刷新请求次数为 1，无需手动刷新。
- desktop-compressed-log.png 已检查：日志位于右下 Tab，与左侧实时日志分开，无独立弹窗或布局重叠。

## 图片缩放与拾取坐标验证

- 依据独立模块 README，修改 MainWindow.xaml、MainWindow.xaml.cs，新增 ImageZoom.cs，更新 WPF 定向测试；未修改 Runtime、协议或 Unity 资产，Unity MCP/CodeGraph 不适用。
- 图片加入 5%—400% 滑条、适应窗口、原始大小与 Ctrl+滚轮缩放，新图默认适应窗口，缩放保持宽高比与视图中心。
- WPF 使用 2400×1200 图片验证 50% 显示为 1200×600；实际滚动后从视口反变换至图像坐标，确认归一化坐标和 Unity Y 翻转正确。
- 验证适应窗口包含整个大图，100% 恢复原始尺寸；desktop-image-zoom.png 已检查，滑条和按钮可见且无重叠。图片导出和游戏端拾取协议保持不变。

## 简体中文发布资源

- 桌面工程设置 SatelliteResourceLanguages=zh-Hans，后续构建只发布简体中文卫星资源。
- Build.ps1 清理旧发布目录内其他已识别语言的 .resources.dll，再移除空语言目录；路径限定 artifacts 下，不递归删除其他内容。
- 已重新发布，Release 构建零警告、零错误。portable-win-x64 根目录仅剩 tools 与 zh-Hans 两个子目录，zh-Hans 中保留 12 个资源程序集。
- 本次仅配置和发布内容调整，未改变应用逻辑，未重复执行 WPF 功能回归或 Unity 验证。

## 组件显示与节点动态监听

- 依据独立工程 README 和原 RemoteInspector 模块 README，调整 WPF Inspector 与动态监视。新增 NodeWatches.cs；修改 MainWindow.xaml、MainWindow.xaml.cs、ViewModels.cs、InspectorConverters.cs，保留原固定属性能力和内部组件索引。
- Inspector 去除“图片与纹理”摘要分组、隐藏组件数组式后缀，重复组件使用人类可读序号；标题与参数明确显示远端启用/激活状态，禁用参数仍可编辑。
- 定向 WPF + 模拟 TCP 验证：右键加入/移除监听、首次基准不误报、定时发现节点改名、组件新增、启用变化、参数前后值、组件移除、反向定位、销毁取消监听、断线不误判。生命周期与已有功能一并回归，共 186 项断言通过。
- Runtime 只在 MessageHandler.HandleGetNodeTree 私有方法体内增加 MarkDirty，再重新采集。通过 unity.script.write 写入，保留旧命令和方法签名，不修改 MonoBehaviour 入口、Prefab 或 Scene。CodeGraph 不覆盖 ThirdPart 模块，使用定向源码调用检查。
- Unity Play Mode 编译持续未完成后，经用户明确允许停止运行；随后确认 compiling=false、scriptCompilationFailed=false。隔离 PreviewScene 测试向 NodeTreeCollector 注入旧缓存并保持场景计数相同，通过真实 MessageHandler.Handle(GET_NODE_TREE) 确认旧缓存被替换；临时对象和 PreviewScene 均在 finally 清理。
- Console 游标在 Domain Reload 后重置，按本次文件过滤当前错误，未见 MessageHandler 编译错误；先前运行产生的连接拒绝/断线错误未计入本次源码错误。
- 已检查 desktop-component-state.png、desktop-node-watches.png：组件状态清楚，监听列表和变化记录无重叠。
- 实机游戏包尚未联调；节点监听为轮询采样，不能保证捕获采样间隔内的全部瞬态变化。需更新 Runtime 才能保证树不沿用旧缓存。

## RPC 紧凑布局实施（2026-10-27）

- 按用户审查选择的紧凑面板实施，IFix/ILRuntime 保留常驻一键按钮；未实施独立展开工作区，避免扩大布局范围。
- 依据独立工程 README，修改 MainWindow.xaml、MainWindow.xaml.cs、DebugTools.cs，新增 RpcPanel.cs，更新 WPF 交互测试；没有修改 Unity 工程或网络协议，Unity MCP/CodeGraph 不适用。
- 函数列表转为搜索弹层；方法签名就地显示，参数 JSON 校验、Ctrl+Enter、可折叠返回结果和复制集中在调用区；模板使用原配置并增加下拉选择。
- 186 项自动断言通过：函数关键词过滤、选择后填入并关闭弹层、无效 JSON/非数组禁用执行、真实 TCP 调用后格式化展开结果、热更新不修改普通 RPC 编辑内容及返回结果、模板恢复。旧功能及关闭生命周期回归保持通过。
- desktop-rpc.png 实际 WPF 离屏截图确认：高频按钮可见、表单与模板分区明确、收起结果没有大块空白结果框。
- Release 构建零警告、零错误；保持简体中文资源和内置日志解码器，仅需更新独立桌面程序。

## 发布目录依赖与残留清理（2026-10-27）

- 以当前项目全新自包含发布 artifacts/verification/publish-audit 为基准，与 portable-win-x64 按相对路径逐项比对；没有过期 DLL、下载缓存、日志、ZIP 或 partial 文件，额外 README.md 为有意附带的说明。
- 清理 RemoteInspector.Core.pdb 与 RemoteInspector.pdb，共 81,460 字节；构建目录 bin/ 中的符号保留，不影响排错。
- 保留 .NET/WPF 自包含运行时、zh-Hans 简体中文资源、枚举/Layer 配置、日志解码器及许可证。未依据静态未引用判断删除框架 DLL。
- Build.ps1 增加发布后根目录 PDB 清理，已重新执行验证：构建零警告、零错误，最终目录无 PDB，主程序、核心库与解码器保留。
- 无功能源码或 Unity 资产变更，未重复运行功能测试。清单记录见 artifacts/verification/dependency-cleanup.json。

## 2026-10-28 场景树定位与收藏

采用根目录 README.md；仅修改独立 WPF 工程，不涉及 Unity 源码、资源或协议，未使用 Unity MCP / CodeGraph。

- MainWindow.xaml.cs：按可见节点及像素偏移恢复刷新位置，激活筛选不主动跳首命中；抑制重建期间自动 BringIntoView；完整路径 Enter/搜索精确定位，重名保留结果。
- MainWindow.xaml：树启用像素滚动且保留虚拟化；Inspector 定位按钮；右键收藏入口。
- DebugTools.cs：Inspector 定位保留属性编辑；收藏定位/移除/会话清空。
- NodeWatches.cs：共享右键菜单支持离线收藏，监听仍仅允许实时连接。
- tests/RemoteInspector.SmokeTests/Program.cs：新增刷新屏幕偏移和完整路径定位回归。

完整模拟联机/WPF 回归 188 项通过（tree-improvements-test.log）；Release 构建 0 警告、0 错误，portable-win-x64 发布成功（tree-improvements-build.log）。真实游戏、大型层级及各种窗口尺寸仍需实际验收；筛选隐藏/销毁节点及滚动范围边界无法保持该节点原屏幕坐标，回退原偏移。
