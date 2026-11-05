# RemoteInspector - Unity 远程调试工具

## 📁 项目结构

```
I:\Trunk\RemoteInspector\
├── Editor\                          # 编辑器端代码
│   ├── Core\                        # 服务器核心
│   │   ├── RemoteInspectorServer.cs     # TCP服务器，管理所有连接
│   │   ├── InspectorConnection.cs        # 单个客户端连接处理
│   │   ├── MessageDispatcher.cs          # 消息分发器
│   │   └── FileUploadHandler.cs         # 文件上传处理
│   ├── Data\                        # 数据结构
│   │   ├── NodeTreeData.cs              # 节点树数据
│   │   ├── ComponentData.cs             # 组件数据
│   │   ├── MaterialData.cs              # 材质数据
│   │   └── FrameStatsData.cs            # FPS统计数据
│   ├── Styles\                      # UI样式
│   │   └── RemoteInspectorStyles.cs      # IMGUI统一样式
│   └── Windows\                     # 窗口面板
│       ├── RemoteInspectorWindow.cs     # 主窗口（入口）
│       ├── NodeTreePanel.cs             # 节点树
│       ├── InspectorPanel.cs            # 属性检视器
│       ├── FpsGraphPanel.cs            # FPS图表
│       ├── FileUploadPanel.cs           # 文件上传
│       └── ConnectionPanel.cs          # 连接设置
├── Runtime\                         # 运行时代码（打入游戏包）
│   ├── Core\                        # 客户端核心
│   │   ├── RemoteInspectorClient.cs     # TCP客户端
│   │   ├── CommandConsole.cs            # 聊天命令窗口（`或F12打开）
│   │   └── MessageHandler.cs           # 消息处理器
│   ├── Collectors\                   # 数据采集器
│   │   ├── NodeTreeCollector.cs         # GameObject树采集
│   │   ├── ComponentCollector.cs        # 组件信息采集（反射）
│   │   ├── MaterialCollector.cs        # 材质/Shader采集
│   │   └── FrameStatsCollector.cs      # FPS采集
│   └── Utils\                        # 工具类
│       ├── TextureCache.cs             # 纹理缓存
│       └── SerializationHelper.cs      # 序列化辅助
└── Shared\                          # 共享代码
    ├── Protocol\                    # 协议定义
    │   ├── MessageProtocol.cs           # TCP协议常量
    │   └── MessageSerializer.cs         # JSON序列化
    └── Commands\                     # 命令枚举
        └── CommandDefinitions.cs         # 所有命令ID定义
```

## ⚙️ 核心设计决策

### 1. TCP协议（8字节固定包头）
```
[4字节 int32 总长度][2字节 ushort 命令ID][2字节 ushort 序列号][JSON包体]
```
- 支持粘包/拆包自动处理
- 心跳：每5秒一次，超时15秒断开
- 序列号用于匹配请求/响应

### 2. 纹理缓存机制
- 客户端维护已发送纹理的 `HashSet<int>`
- 编辑器请求纹理时，客户端检查缓存，已发送过则回复 `cached=true`（不传数据）
- 首次传输：JPG压缩(quality=60) → Base64 → 分块(32KB/块)传输
- 编辑器端缓存 `Dictionary<int, Texture2D>`，避免重复显示

### 3. 节点树同步
- 客户端启动时发送完整树（`NODE_TREE_RESPONSE`）
- 编辑器按需请求单个节点详情（`GET_NODE_DETAIL`）
- 节点标识：GameObject InstanceID

### 4. 组件数据采集
- Transform：本地+世界空间双套数据
- 材质：Shader名、所有Shader属性、纹理引用
- 其他组件：通过反射读取 `[SerializeField]` 和 `public` 字段

### 5. 暂停/恢复
- `PAUSE_CLIENT` → `Time.timeScale = 0`
- `RESUME_CLIENT` → `Time.timeScale = 1`
- 同时暂停数据采集循环，节省性能

### 6. 客户端命令控制台
- 默认隐藏，按 `` ` `` 或 `F12` 打开
- 命令：`/connect` `/disconnect` `/pause` `/resume` `/status` `/help` `/clear`

### 7. 文件上传
- 分块上传：64KB/块
- 路径：`Application.persistentDataPath` 下相对路径
- 重载回调：`FileReloadExtension.OnFileReceived` 事件，用户可自定义热重载逻辑

## 🔧 使用方法

### 编辑器端
1. 将 `Editor\` 目录放入 Unity 项目
2. 打开菜单：**Window → Remote Inspector**
3. 启动游戏（Play Mode）→ 服务器自动启动（端口9555）
4. 等待客户端连接，查看节点树、FPS、属性

### 客户端（Runtime）
1. 将 `Runtime\` 目录放入游戏项目
2. 在场景中创建空GameObject，挂载 `RemoteInspectorClient` 组件
3. 配置 IP 和端口（默认 `127.0.0.1:9555`）
4. 可选：挂载 `CommandConsole` 组件（`` ` `` 打开命令行）
5. 打包游戏 → 运行 → 输入 `/connect <编辑器IP> <端口>`

### 数据更新频率
- **每帧**：每帧采集FPS，每帧采集节点树（流量大）
- **每秒**：每秒采集一次，FPS按秒汇总（推荐）
- **每5秒**：适合远程排查
- **手动**：手动刷新

## 📋 命令列表

| 命令 | ID | 说明 |
|------|-----|------|
| HEARTBEAT | 0x0001 | 心跳保活 |
| GET_NODE_TREE | 0x0010 | 获取节点树 |
| NODE_TREE_RESPONSE | 0x0011 | 节点树响应 |
| GET_NODE_DETAIL | 0x0012 | 获取节点详情 |
| NODE_DETAIL_RESPONSE | 0x0013 | 节点详情响应 |
| PAUSE_CLIENT | 0x0020 | 暂停游戏 |
| RESUME_CLIENT | 0x0021 | 恢复游戏 |
| GET_FRAME_STATS | 0x0030 | 请求FPS数据 |
| FRAME_STATS_RESPONSE | 0x0031 | FPS数据响应 |
| FILE_UPLOAD_START | 0x0040 | 开始上传 |
| FILE_UPLOAD_CHUNK | 0x0041 | 上传数据块 |
| FILE_UPLOAD_END | 0x0042 | 上传完成 |
| FILE_UPLOAD_ACK | 0x0043 | 上传确认 |
| GET_TEXTURE | 0x0050 | 请求纹理 |
| TEXTURE_RESPONSE | 0x0051 | 纹理数据响应 |

## ⚠️ 注意事项

1. **IL2CPP兼容**：所有反射部分使用 `[Preserve]` 标记，避免代码剥离
2. **平台支持**：PC/Android/iOS/HM 全平台 TCP 通用
3. **多客户端**：支持同时连接多个游戏客户端，编辑器可查看不同连接的状态
4. **性能**：反射采集仅在用户请求节点详情时触发，不会持续影响游戏性能
5. **安全性**：仅用于开发调试，**不要**打包到正式发布版本中


## 2026-11-04 RPC 按需查找与反射调用

注册 RPC API 保留，RPC_CALL/RPC_LIST 现由客户端队列切到 UnityMain 执行。参数使用项目已接入的 Newtonsoft.Json 解析 JSON 数组并按声明类型严格校验，不再将整个数组当单个字符串或将错误参数默认为 0。

新增 ReflectionRpcProtocol（0x0098—0x009d），分别处理程序集/类/方法查询、实例查询与同步调用。元数据查询在工作线程；实例与执行在 UnityMain。按完整声明签名区分重载，默认选择当前场景唯一的激活组件；歧义、销毁和范围变化明确失败。可显式查询未激活/其他场景；普通 C# 对象仅复用已注册委托持有的实例，不自动构造或访问单例 getter。执行结果包含原始内部异常、目标路径和实际执行时间。

桌面端所有目录/方法/实例请求均由刷新按钮触发，窗口打开及选中类不自动拉取；按连接会话缓存。只支持 CLR 已加载类和同步方法；ILRuntime 解释类型及 IL2CPP 已裁剪掉的成员不在此反射目录中。需要更新对应游戏包，并在实际 IL2CPP 包中保留待调试成员。

## 2026-11-05 Performance 接口接收

PerformanceBridge 实现 Performance 包内的 IPerformanceForwarder，构造时 RegisterForwarder(this)，Dispose 时释放注册句柄；不再订阅 Performance.JankSnapshot 事件。回调仅提交现有有界队列，TCP/会话和协议转换仍由本工具负责。Performance SDK 只认识通用接口，不引用或构造 RemoteInspector。接口归 SDK 现有程序集，不新增契约/适配程序集或 asmdef；接收工具对 SDK 的接口保持单向依赖。即时主线程抓栈继续使用 SDK 的通用公开 API。


## 2026-11-05 Performance 转发容错

转发线程逐包隔离序列化/发送异常，失败不再永久停止后续事件转发；失败时以 [RemoteInspector.Performance]、eventId、状态及异常类型记录原因，连续失败限流，不重试可能已发送的包。空序列化结果拒绝发送。空堆栈项跳过，后续有效样本继续转发，缺失采样次数按 1 处理。继续使用 SDK 通用接收接口，无跨程序集反向依赖。


## 2026-11-05 IL2CPP / MSVC C1001 修复

MSVC 14.51 在 PerformanceBridge.SendLoop 生成的多层异常处理尾部触发内部错误。保留线程入口签名及转发行为，将队列读取、单包发送、失败日志和资源清理拆成私有小方法，减少 IL2CPP 生成函数中的嵌套异常处理与跨 finally 跳转。Client 与 WatchDog 源码同步；不修改生成 C++、编译器参数、优化级别或程序集。

已用 WatchDog 当前 Bee Player 构建图重新执行 C# 编译、UnityLinker 和 IL2CPP 转换；原失败目标 h93ew9voveun.obj 编译成功，GameAssembly.dll 全部 C++ 编译与链接成功。12 项采样/转发回归通过。仅构建 Library 下的 GameAssembly 产物，未覆盖正在运行的游戏目录；在 Unity 继续 Build 可完成最终打包。


## 2026-11-05 SDK窗口批量转发

新增0x00B0开始、0x00B1确认、0x00B2批量推送、0x00B3停止；开始/停止在接收工作线程处理，不依赖UnityMain。PerformanceBridge实现SDK IStackSamplingReceiver并启动/释放窗口句柄，回调仅复制入最多8批的有界队列，既有发送线程传输；超限丢最旧批并累计transportDropped，最终批优先保留。断线/销毁取消SDK窗口并清队列。会话ID过滤防止旧结果写入新窗口，单次错误不终止发送线程。

Client和WatchDog共享实现；WatchDog没有bq业务日志库，RemoteInspectorClient中的Client专用bq转发入口以SUPERMAIN_BQLOG条件隔离，不要求SDK接入日志库。需要重建新游戏包，旧包不支持该窗口协议；不静默退回一秒即时轮询。
