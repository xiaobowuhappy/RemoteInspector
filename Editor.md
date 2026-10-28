# Remote Inspector 编辑器使用说明
 
## 1. 工具路径

运行程序：

```text
RemoteInspector.exe
```

![Remote Inspector 程序所在目录](docs/images/editor/tool-path.png)

## 2. 启动工具并开启监听

双击 `RemoteInspector.exe` 启动工具，默认监听端口为 `9555`。点击“启动监听”。

![启动工具并开启监听](docs/images/editor/start-listening.png)

## 3. 在游戏中连接查看器

可通过以下方式连接：

1. 点击 GM 面板中的“远程 Inspector”快捷按钮。原文说明该按钮默认连接作者的 IP，使用时需确认目标地址。
2. 在聊天框输入 `..inspector,ip`，其中 `ip` 替换为运行查看器的电脑 IP。

![GM 面板中的远程 Inspector 按钮](docs/images/editor/connect-gm.png)

![聊天框连接命令示例](docs/images/editor/connect-command.png)

![游戏中的 Inspector 连接提示](docs/images/editor/connect-status.png)

## 4. 场景节点树

1. 游戏中弹出连接提示，表示连接成功。
2. 连接后自动刷新场景节点树。
3. 节点激活状态分为“已激活”“自身未激活”“父级未激活”。
4. 支持模糊查找、组件名称查找和节点名称查找。
5. 支持收藏节点，方便下次直接定位。
6. 关闭搜索后保留历史搜索定位，避免重新查找。
7. 支持按路径定位节点，例如输入 `UIRoot/BG/Leve` 直接定位。

![场景节点树、激活状态与搜索](docs/images/editor/scene-tree.png)

## 5. 属性查看和编辑

1. 支持工程中常见的枚举类型，通过弹出下拉框选择或复选。
2. 提供类似 Unity Inspector 的属性查看方式。
3. 支持编辑数值和向量；修改后点击“应用”，实时生效。
4. 支持查看纹理并导出到本地，**仅支持可读写纹理**。
5. 支持复制节点路径。
6. 支持手动刷新同步。
7. 支持全部展开，方便查找。
8. 支持全部折叠。
9. 提供快捷编辑及原始数据查看，便于快速校对属性名称和属性值。
10. 支持查看材质 Shader、材质属性、材质纹理及变体等。
11. 支持新增收藏节点，方便下次快捷查找。
12. 支持定位节点，减少滚动节点列表查找的操作。

![属性查看、数值与向量编辑](docs/images/editor/property-inspector.png)

![节点属性的原始数据](docs/images/editor/property-raw-data.png)

![属性工具栏与材质查看入口](docs/images/editor/material-inspector.png)

## 6. 截图定位功能

1. 抓取远程游戏画面。
2. 开启点击截图定位节点的功能。
3. 点击截图中的图片或按钮，左侧显示所有命中（hit）对象，结果可能有多个。
4. 点击查找结果，快速定位 UI 节点。

![通过远程游戏截图定位 UI 节点](docs/images/editor/screenshot-locate.png)

## 7. 动态监视

1. 选择场景节点，右键“加入监听”或“移除监听”。
2. 监听对象的属性、组件变化会显示在列表中，方便追踪。

![通过节点右键菜单加入或移除监听](docs/images/editor/watch-menu.png)

![动态监视中的属性与组件变化](docs/images/editor/watch-changes.png)

## 8. 离线快照

保存场景树、节点属性及日志快照，使用二进制格式存储。

![保存离线快照](docs/images/editor/snapshot-save.png)

![保存为 bin 格式的 Remote Inspector 快照](docs/images/editor/snapshot-file.png)

## 9. 日志查看

实时查看并过滤日志。

![实时日志查看与过滤](docs/images/editor/logs.png)

## 10. RPC 远程调用

1. 支持内置热更加载，方便调试热更代码。
2. 支持存储常用函数调用，封装常用函数列表。

![RPC 远程调用面板](docs/images/editor/rpc.png)

## 11. 远程文件管理

支持文件上传和下载。

![远程文件管理](docs/images/editor/file-manager.png)

## 12. 压缩日志

支持双击查看远端压缩日志文件。

![远端压缩日志文件列表](docs/images/editor/compressed-log-list.png)

![压缩日志内容查看](docs/images/editor/compressed-log-view.png)

## 13. 布局切换

提供以下布局：

- 默认布局
- 对象排查
- 日志排查
- 性能观察

![布局切换菜单](docs/images/editor/layout-menu.png)

### 对象排查

![对象排查布局](docs/images/editor/layout-object.png)

### 日志排查

![日志排查布局](docs/images/editor/layout-logs.png)

### 性能观察

![性能观察布局](docs/images/editor/layout-performance.png)

## 14. 后续扩展

原文标注：性能分析、卡顿日志收集即将上线。此处保留 PDF 导出时的说明，实际支持情况以当前工具版本为准。
