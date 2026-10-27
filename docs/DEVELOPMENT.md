# 开发说明

面向改代码 / 排查问题的人。玩家向的安装与用法见根目录 [README](../README.md)。

## 文件地图

| 文件 | 职责 |
| --- | --- |
| `ContainerSorterMod.cs` | `MelonMod` 入口：读配置、轮询整理键、把活儿交给 `ContainerTransfer.Run`，并做异常收敛（不刷屏） |
| `ContainerTransfer.cs` | 搬入方向（背包 → 容器）：判断面板是否打开、收集背包快照、排序、按容量搬运、并堆 / 拆堆、刷新界面 |
| `TargetCount.cs` | 取出方向（容器 → 背包）：按「目标数量」补足背包不足的份数 |
| `GearMath.cs` | 两个方向共用的物品读数：名字归一化、份数、单份/整堆重量、耐久、重量账本校验、销毁与份数还原 |
| `IgnoreList.cs` | `Mods\ContainerSorterIgnore.txt` 的解析与热重载（分区标记、目标数量后缀、注释） |
| `Settings.cs` | ModSettings 设置项（`JsonModSettings`），按键字段是 `KeyCode`，由 ModSettings 渲染成重绑定控件 |

## 构建与部署

```bash
dotnet build -c Release -t:Rebuild
cp bin/Release/net6.0/ContainerSorter.dll "<游戏目录>/Mods/"
```

- `net6.0`，`Nullable=enable`，`GenerateAssemblyInfo=false`（程序集信息由 `MelonInfo` 特性提供）
- 游戏类型在 `Il2Cpp` 命名空间，csproj 里靠 `<Using Include="Il2Cpp" />` 全局引入
- `STBlade.Modding.TLD.Il2CppAssemblies.Windows` 2.55.0 传递提供 MelonLoader / HarmonyX / UnityEngine / Assembly-CSharp，
  **不要**再手写 `HintPath` 引 MelonLoader，会重复引用
- `ModSettings.dll` 只从实机 `Mods\` 引用，`Private=false`（运行时由游戏侧提供）
- 部署时 dll 被游戏占用：先 `mv` 再写；`.old.dll` 之类留在 `Mods\` 里会被当成第二个 mod 加载

## 2.55 上确认过的 API 事实

坐标：`shared/il2cppdumper/dump.cs`（离线核对）+ 实机日志。

- **面板**：`InterfaceManager.GetPanel<Panel_Container>()` / `TryGetPanel<T>(out T)`；`IsEnabled()` 判断是否打开；
  容器本体在 `panel.m_Container`。
- **容量与重量**：`Container.m_Capacity`、`Container.GetTotalWeightKG()` 都是 `Il2CppTLD.IntBackedUnit.ItemWeight`，
  **没有 `ToKilograms`**，换算用 `weight / ItemWeight.FromKilograms(1f)`。
- **背包遍历**：走 `Inventory.m_Items`（`List<GearItemObject>`），不用 `GetGearItems(Predicate<T>)`，避开 Il2Cpp 泛型委托的坑。
- **移出背包 / 放入容器**：`Inventory.RemoveGear(go, true)` 与 `Container.AddGear(gi)`。
  `AddGear` **不合并堆叠**——同款会落成新的一堆，20 根木柴搬进去会和已有的 1 根并存（v1.1 之前的行为）。
- **顺序必须是先 `RemoveGear` 再 `AddGear`**：同款多件时 `AddGear` 可能并进已有堆叠并销毁传入对象，
  反过来的话第二次调用会打到已销毁对象。
- **堆叠**：`GearItem.m_StackableItem.m_Units`、`m_StackConditionDifferenceConstraint`（原版没填时按 5% 处理）。
  容器里已有同款堆时合并只改两边的 `m_Units`，不碰 `Inventory` 的增删接口，最安全。
- **拆堆**：`GearItem.InstantiateStackable(n)` 复制自身（**不走 prefab 加载**，规避 Unity 6 的 bundle 阻塞），
  设 `m_Units`，清 `m_InPlayerInventory` / `m_IsInSatchel`，再 `container.AddGear(part)`。
- **反向取回**：`Container.RemoveGear(gi, toInventory: true)` 是原版「从容器取出」的那条路，内部会把物品交给背包；
  背包侧另有 `Inventory.AddGear(gi, enableNotificationFlag)`、`FindByInstanceID(id)`、`GetTotalWeightKG()` 可用于核对落地。
  `RemoveGear` 是否真的交给了背包**没在源码层确认**（IL2CPP 无方法体），所以 `TargetCount.MoveWhole` 会核对
  「实例在背包里 / 背包总重涨了一堆」，不成立再显式 `AddGear`，仍失败就把物品放回容器。
- **单份重量**：`GetItemWeightKG(false)` 是整件（整堆）重量，比较「谁更重」时必须除以 `m_Units`。
- **耐久**：`GetNormalizedCondition()`；比较留 0.0001 容差，避免浮点抖动反复换人。
- **输入**：`InputManager.GetKeyDown(context, key)` 用不了（`m_CurrentContext` 是 private 且无公开访问器），
  轮询走 `UnityEngine.Input.GetKeyDown(KeyCode)`。

## 一次整理的流程

1. `Panel_Container` 未打开 / `m_Container` 为空 / 拿不到 `Inventory` → 直接返回
2. 忽略清单按文件修改时间增量重读（变了才重读）
3. **`TargetCount.Fill`（容器 → 背包）**：先补足「目标数量」不够的份数 —— 它在往外取、腾空间，所以必须排在容量判断之前
4. 重新读容量与已用重量（取回可能改了容器重量），再判 `capacity <= 0` / 已经装满 / 容器空
   —— 这三条出口只要 3 步动过东西就仍要刷新界面
5. 收集背包快照（按设置跳过穿戴中 / 手持 / 小包内 / 隐藏供给），得到 `List<GearItem>`。
   **必须拍在取回之后**，否则刚取回来的份数会被当成「背包里没有」
6. 收集容器里已有的种类集合（`HashSet<string>`，名字去掉 `(Clone)` 后缀）
7. 「保留（至少一个）」/「目标数量」的种类先挑出要留在背包的实例与份数（`PickKeepers` → `KeeperPick`）
8. 排序（`OrderForTransfer`）：同类内按单份重量从重到轻，整件留在背包的排最后
9. 逐件处理（背包 → 容器）：忽略清单 → 保留判断 → 容量计算 → 并堆 / 拆堆 / 普通搬运
10. 刷新界面（三步，各自 try）

## 三种搬运动作（背包 → 容器）

| 动作 | 何时用 | 关键点 |
| --- | --- | --- |
| `MoveOne` | 整件（整堆）都搬 | `RemoveGear` → `AddGear`；容器没接收就退回背包 |
| `MergeUnits` | 容器里有可比配的堆叠，且只搬一部分 | 只改两边 `m_Units`，不碰增删接口；校验容器涨重、背包掉重都约等于搬走那几份 |
| `SplitAndMove` | 容器里没有可合并的堆 | `InstantiateStackable(n)` 拆一份放进去，再把源堆数量降下来；失败用 `RemoveGear` + 销毁 + 还原数量回滚 |

### 重量账本校验（别删）

改完 `m_Units` 之后核对「重量涨了约一件物品那么多」（0.5×~2× 期望值）。两个方向共用这一条
（`MergeUnits` / `SplitAndMove` 在搬入，`ShiftUnits` 在取回）。两类账本差异会被这一步抓住：

- 重量根本不看 `m_Units`（涨 0）→ 说明这类物品的数量不决定重量
- 重量已含数量又乘了一次（涨太多）→ 说明重复计数

不符合就回滚数量、退回普通迁移，绝不让容器容量算错。

### 整组超重时按容量拆

`FitUnits(剩余容量, 单份重量, 份数)` 求最多装几份：先 `floor`，再用乘法回验一次，防止浮点多算一份把容器撑爆。
算出 `fitUnits` 后走「只搬一部分」路径（`MergeUnits` / `SplitAndMove`），两者都要求
`moveUnits + keepUnits == sourceUnits`，正好成立。`fitUnits == 0` 才真的跳过这一堆。

循环只在容器剩余容量 ≤ 0.0005kg 时 `break`，所以效果是「一直搬到再也塞不进任何单个物品为止」。

## 目标数量：两个方向

`物品ID*数字` 解析成 `IgnoreMode.SetCount`（`IgnoreList` 里的 `targets` 字典单独存一份，只收用户写下的条目，不含
`GEAR_` 别名，避免同一条规则被处理两次）。两个方向共用同一份规则：

- **搬入方向**（`ContainerTransfer`）：`IsKeepRule` 把 `SetCount` 和 `KeepOne` 当同一类处理 —— 都是「背包留 N 份、其余搬走」。
  超出目标的部分就靠既有的 keeper + 容量 + 并堆/拆堆逻辑搬进容器，`PickKeepers` 里 `need = KeepCount`。
- **取出方向**（`TargetCount.Fill`）：只在 `背包份数 < N` 时动作，取 `N - 背包份数` 份回来。
  容器里没有这种物品就整类跳过（不凭空变出来），不够就有多少取多少。

取回的三条动作：

| 动作 | 何时用 | 关键点 |
| --- | --- | --- |
| `MoveWhole` | 这一堆全部要取回 | `container.RemoveGear(gi, true)`；核对落地（实例或背包总重），没落地就显式 `AddGear`，再不行放回容器 |
| `ShiftUnits` | 背包里有同款、状态接近的堆 | 只改两边 `m_Units`；容器轻了、背包重了都要约等于这几份，否则双向回滚 |
| `InstantiatePart` | 背包里没有可合并的堆 | `InstantiateStackable(take)` 造一份放进背包，再扣容器那堆的份数；落地失败就还原容器份数 |

### 计数口径（改之前先读）

- `TargetCount.CountInInventory` 统计**全部**背包实例，**包括穿在身上 / 拿在手里 / 小包里的**：
  设置项只决定「能不能搬」，不该影响「背包里到底有几个」，否则拿在手上的工具会被算成 0 份、白取一件回来。
- 搬入方向的 `PickKeepers` 只看 `stock`（按设置过滤过）。两者口径不同是有意的：
  被设置保护起来的实例**算进 N**，但搬不动，所以这种情况下背包最终会多于 N。宁可少搬，不能强行脱玩家的装备。
- 顺序也必须保持「先取回、后搬入」：取回不受容器剩余容量限制，还顺手腾出空间；反过来先搬入会把空间占掉。

### 别把 `GearMath` 拆回去

`GearMath` 里的容差（`WeightMatches` 的 0.5×~2×）、`(Clone)` 归一化、`UnitsOf` 的「非堆叠算 1 份」是两个方向共用的口径。
任何一边单独抄一份，都会出现「补到 N 份」和「留下 N 份」互相打架的循环（这类 bug 在实机上表现为数量来回抖）。

## 忽略清单解析

```
#全部无视/Ignore All            ← 分区标记
GEAR_Revolver
#保留（至少一个）/Keep At Least One
GEAR_HeavyBandage*4            ← 条目级目标数量（双向）
```

- **条目带数字 → `SetCount`（双向）**，不带数字 → 沿用分区模式（`IgnoreAll` / `KeepOne`，单向）。
  分区标记里写的数字（`#保留 4个`）只做单向的默认份数，**不会**让条目变成双向 —— 双向只认条目上的 `*数字`。
- **分区标记判定**（`TryParseMarker`）：只接受「整行注释里只写标记本身」。按 `/`、`｜` 拆最多两段，
  每段 squash（去空白 + 小写）后必须**整段**匹配关键词，任何一段不像标记 → 整行按普通注释处理。
  靠这条把模板里带说明文字的行和真标记区分开，**别改成 contains 判断**。
- 关键词：全部无视/无视全部/忽略全部/ignoreall；保留/保留N个/至少一个/keep N。数字支持 0-9 与 一~十，默认 1。
- 第一个标记之前默认 `IgnoreAll`（兼容没有分区的老文件）。
- **份数分隔符是 `*`**（`Separator`），全角 `＊` 也认。`#` 只当注释：
  `#` 以前是份数分隔符，和注释符号撞车，v1.9 起彻底不给份数用。
  旧写法 `ID#4` 现在等于 `ID` + 一段注释，结果是默认份数，日志会提示一次（只在 `#` 紧跟数字时才提示，
  避免 `GEAR_X # 4 个备用` 这种正常尾注刷警告）。
- `*` 后面必须是紧跟（可隔空格）的纯数字，且数字后是行尾/空白/分隔符；否则整段当备注丢掉并告警。
- 行内可用逗号/分号/制表符写多个条目；`//` 与 `#` 都能起注释，行尾也能跟。
- 允许省略 `GEAR_` 前缀：载入时同时塞入去前缀的别名，但统计只数用户写下的条目。
- **热重载**：每次按整理键 `Reload()` 只比 `File.GetLastWriteTimeUtc`，变了才重读；文件被删则清空并告警。

## 界面刷新

直接改数据绕过了原版 `Panel_Container.OnMoveItem()`，所以刷新要自己补，三步各自独立 try：

1. `RefreshTables()` —— 立刻重建容器表 + 背包表
2. `ForceRefresh()` —— 置 `m_ForceRefreshRequested`，下一帧重算过滤列表与选中项。
   只做第 1 步会残留已搬走的条目（过滤列表没重算），这一步是关键
3. `Panel_Inventory.RefreshTable()` —— 背包面板正在显示时再刷它自己的格子和负重

## 离线自测（桩工程）

容器与背包的真实行为只能在实机上验，但两个方向的**算术与流程**可以离线跑：工作区里的
`projects/ContainerSorterStub`（不在本仓库内）用桩类型链接本仓库的 `IgnoreList.cs` / `GearMath.cs` / `TargetCount.cs`：

```bash
cd <工作区>/projects/ContainerSorterStub
dotnet run     # 全绿输出「全部通过」，任何一项失败退出码非 0
```

覆盖范围：清单解析（`*数字` → 双向、`*4张` → 回退单向、全角 `＊`、`GEAR_` 别名、行尾注释）、目标数量补齐的
整堆移走 / 拆一份 / 并进背包已有堆 / 容器不够 / 容器没有 / 非堆叠 / 重量账本异常回滚 / `RemoveGear` 不落地兜底。
桩里的 `AddGear` 合并、`Destroy` 摘链、重量 = 单份 × 份数都是**假设**，所以它只能证明 mod 自己的算术与流程，
替代不了实机验证；改了 `TargetCount` 或 `GearMath` 就回来跑一遍。

## 设计约束

- 只在容器面板打开时动作，不在其它界面产生副作用
- 任何一步不如预期都要**回滚或退回背包**，宁可少搬也不能丢物品 / 算错容量
- 物品读数一律走 `GearMath`，别在别处另抄一份容差或归一化
- 日志默认只打汇总行，逐件明细走「输出详细日志」，避免刷屏
- 源码注释只写「做什么 / 为什么（一句）/ 违反会怎样」，版本演进写进 [CHANGELOG](CHANGELOG.md)

## 日志对照

| 日志 | 含义 |
| --- | --- |
| `剩余 X kg 只够 A/B 份` | 整组塞不下但单份装得下，搬了 A 份（需开详细日志） |
| `跳过 xxx（…，连一份都放不下）` | 剩余空间连单个都不够，跳过这一堆继续试别的 |
| `容器没有接收 xxx，已退回背包` | 原版容量判定更严或容器拒绝该分类，物品已退回背包 |
| `xxx 的堆叠重量没有随数量变化，已回滚` | 该类物品的堆叠账本不走 `m_Units`，退回「单独放一堆」 |
| `拆堆失败（xxx）` / `容器没有接收拆出来的一份（xxx），已回滚` | 拆分没成，数量已还原 |
| `目标数量：从容器取回 N 份（K 种）` | 取出方向动了东西（K = 发生取回的种类数） |
| `…，S 种仍不够（容器里没有或数量不足）` | 有种类没补到目标，通常就是容器里没有 / 数量不足 |
| `背包没有接收从容器取出的 xxx，尝试放回容器` | 取出方向两条落地校验都失败，物品已退回容器 |
| `背包没有接收拆出来的 xxx（N 份），容器份额已还原` | 拆一份放进背包失败，容器那边的份数已还原 |
