# Container Sorter

[English](#english) · [中文](#中文)

![version](https://img.shields.io/badge/version-1.11.0-informational)

![game](https://img.shields.io/badge/The%20Long%20Dark-2.55-success)

![loader](https://img.shields.io/badge/MelonLoader-0.7.1-blueviolet)

![license](https://img.shields.io/badge/license-MIT-blue)

---

## English

One key sorts the kinds your backpack shares with the open container into that container, and keeps a fixed count of the rest.

### Features

**One key**

- Press `R` (rebindable) with a container panel open and the backpack is sorted in one pass; both panels and both weight bars update on the spot.
- Nothing happens outside a container panel, no key is taken over and no message appears in the middle of the screen.

**It fills the container**

- Every item is measured against the remaining capacity; a stack that is too big is split and moved until not even one unit fits, instead of being skipped whole.
- A moved item joins an existing stack of the same kind: 20 firewood plus 1 log becomes one stack of 21, not two stacks.

**It fills the curing box too**

- Same key inside a curing box: the kinds the box already holds go in, up to its meat capacity; salt is stored separately, and only whole pieces are taken.

**It keeps a fixed count**

- Write `GEAR_Arrow*20` in the list and the backpack holds 20 arrows after the key press: extras go into the container, a shortfall is taken back out of it.
- Counting is by unit, so 20 arrows in one stack plus 3 in another counts as 23.

**It decides which copy stays**

- The worn copy stays in the backpack and the good one is stored; with equal condition the lighter piece stays (1 kg meat goes in, the 0.5 kg piece stays), and with several tools of one kind one stays.

### Requirements

- The Long Dark **2.55** (built against 2.55)
- [MelonLoader](https://melonwiki.xyz/) **0.7.1** or newer (tested on 0.7.3), plus **ModSettings** (required for the settings page and the key rebinding)

### Installation

1. Make sure `MelonLoader/` and `Mods/ModSettings.dll` are in place.
2. Put `ContainerSorter.dll` into `TheLongDark/Mods`.
3. Start the game.

### Usage

1. Open any container (locker, fridge, toolbox, corpse) or a curing box.
2. Press the sort key, `R` by default.
3. Kinds the container already holds move in by remaining capacity; kinds with a target count are topped up or trimmed first.
4. Worn clothing, the held item, satchel contents and internal supplies such as water or fuel are skipped; the first three can be turned off under **Options → Mod Settings → Container Sorter**, where the key and the switches also live.

### Ignore list

`Mods/ContainerSorterIgnore.txt` is created on first start with comments only, so nothing is ignored until you edit it. One item ID (internal prefab name) per line, organised in sections:

```
#全部无视/Ignore All
GEAR_Revolver
#保留（至少一个）/Keep At Least One
GEAR_HeavyBandage*4
```

| Writing | Meaning |
| --- | --- |
| Entries after Ignore All | never moved into a container |
| Entries after Keep At Least One | 1 unit stays in the backpack, the rest move in, nothing is taken back |
| `GEAR_HeavyBandage*4` | target count 4: surplus moves in, shortfall is taken back out |

- The count separator is `*`, not `#`: `#` starts a comment, so `GEAR_X#4` is read as a note and the entry falls back to its section default.
- The full-width variant works, `*` must be followed by plain digits, and matching is case-insensitive with an optional `GEAR_` prefix (`Acorn*5` equals `GEAR_Acorn*5`).
- Sections can repeat, the last marker wins, entries before the first marker count as Ignore All, and saving the file is enough — the list is re-read on the next key press. To look up a prefab name, turn on the verbose log and copy the names it prints.

### Settings

The in-game page is labelled in Chinese; the rows below are in the same order.

| Setting | Effect | Default |
| --- | --- | --- |
| Sort key | click the row, then press the key you want; keyboard and mouse keys both work | `R` |
| Require Ctrl | keeps the key from colliding with other mods | Off |
| Merge stackable items | off drops every item as its own stack | On |
| Skip worn and held items | off is equivalent to forcing you to unequip | On |
| Skip items in the satchel | the satchel's contents are never moved | On |
| Work with the curing box | off makes the box stop responding to the key | On |
| Enable ignore list | off ignores the file completely | On |
| Keep the best condition in the container | the lowest condition stays in the backpack | On |
| Heavier first when condition ties | the lighter piece stays (meat: the 1 kg piece goes in first) | On |
| Verbose log | per-item lines for troubleshooting | Off |

### Uninstall

Delete `TheLongDark/Mods/ContainerSorter.dll`. `Mods/ContainerSorter.json` and `Mods/ContainerSorterIgnore.txt` can be kept or deleted; nothing is written to the save.

### Compatibility

- Responds only while a container panel or a curing box panel is open; outside one the key does nothing at all.
- Does not touch item definitions, stack rules or container capacity — items move only when you press the key.
- Matching is by prefab name, so a ruined or renamed copy still counts as the same kind; if the game ends up in an Input System-only setup the hotkey disables itself and says so in the log.

### FAQ

**Nothing happens when I press the key.**  
Check that a container or curing box panel is open; with Require Ctrl on, press `Ctrl` + the key. Another mod may own that key — try a different one. A curing box only takes kinds it already holds, only whole pieces, and nothing at all once it reads full.

**Nothing moved, or it stopped halfway.**  
Only kinds the container already holds are moved, and the container being full ends the run. The rest stays in the backpack.

**The count from `ID*number` is not reached.**  
The container does not hold that item or holds too little, so it is topped up as far as the container allows. Worn, held and satchel units count toward the target but are protected from moving.

**Items did not merge into one stack.**  
Liquids and food with too large a freshness gap stay separate; when an item's weight does not follow its unit count, it is stored as its own stack rather than risk a wrong capacity.

### License

MIT — see [LICENSE](LICENSE). Version history: [docs/CHANGELOG.md](docs/CHANGELOG.md).

---

## 中文

一个键把背包里与当前容器同种的物品归位，剩下的按你写的数量留好。

### 功能

**一个键**

- 容器面板打开时按 `R`（可重绑定），一趟把背包整理完；两边面板与负重条当场更新。
- 容器面板没打开时什么都不做，不抢游戏原有按键，屏幕中间也不弹提示。

**把容器装满**

- 每件都按剩余容量算；整堆太大就拆开搬，一直搬到连一份都放不下为止，而不是整堆跳过。
- 搬进去的物品并进同款堆叠：20 根木柴搬进已有 1 根的那堆，容器里只有一堆 21 根，不是两堆。

**腌肉盒也能用**

- 打开腌肉盒按同一个键，盒子里已有的种类按剩余容量放进去；盐是独立仓位，只收整件、成叠的留在背包。

**按固定数量留**

- 清单里写 `GEAR_Arrow*20`，按完键背包里就是 20 支箭：多了把多出来的塞进容器，少了从容器取回来。
- 份数按堆叠累计：背包里 20 支一堆加 3 支一堆，算 23 份。

**决定留哪一份**

- 背包留耐久最差的那件、状态好的收进容器；耐久一样则留更轻的那份（同样新鲜的肉，1kg 的进柜子、半公斤的留在包里），同种工具好几把时留一把。

### 前置条件

- 《The Long Dark》**2.55**（本 mod 针对 2.55 编译）
- [MelonLoader](https://melonwiki.xyz/) **0.7.1** 或更高（实测环境 0.7.3），以及 **ModSettings**（设置页与按键重绑定依赖它）

### 安装

1. 确认游戏目录下有 `MelonLoader/`，且 `Mods/ModSettings.dll` 已存在。
2. 把 `ContainerSorter.dll` 复制到 `TheLongDark/Mods/`。
3. 重启游戏。

### 使用

1. 打开任意容器（柜子、冰箱、工具箱、尸体）或腌肉盒。
2. 按整理键，默认 `R`。
3. 容器里已经有的种类会按剩余容量搬进去；写了目标数量的种类会先补齐或削平再搬。
4. 穿戴中的衣物、手上拿的物品、小包里的东西，以及水分/燃料这类内部供给会被跳过；前三项可以在 **选项 → Mod Settings → Container Sorter** 里关掉，键位与各个开关也在那里。

### 忽略清单

`Mods/ContainerSorterIgnore.txt` 首次启动时自动生成（内容全是注释，默认不忽略任何东西）。每行一个物品 ID（游戏内部 prefab 名），按分区组织：

```
#全部无视/Ignore All
GEAR_Revolver
#保留（至少一个）/Keep At Least One
GEAR_HeavyBandage*4
```

| 写法 | 含义 |
| --- | --- |
| `#全部无视/Ignore All` 之后的条目 | 一个都不搬进容器 |
| `#保留（至少一个）/Keep At Least One` 之后的条目 | 背包留 1 个，其余搬进容器，不会去容器里拿 |
| `GEAR_HeavyBandage*4` | 目标数量 4：多了搬进容器，少了从容器取回 |

- **份数分隔符是 `*`，不是 `#`**：`#` 是注释符号，写成 `GEAR_X#4` 会被当成注释，条目退回所在分区的默认行为。
- 全角 `＊` 也认，`*` 后面必须是纯数字；大小写不敏感，`GEAR_` 前缀可省略（`Acorn*5` 等价 `GEAR_Acorn*5`）。
- 分区可以写多段，后面的覆盖前面的，第一个标记之前的条目按「全部无视」处理；**改完存盘就生效**，下次按键自动重读。想查某个物品的 prefab 名：打开「输出详细日志」，日志里会打印实际搬运 / 跳过的名字。

### 设置

| 项 | 说明 | 默认 |
| --- | --- | --- |
| 整理键 | 点一下设置项，再按下想绑的键；键盘 / 鼠标键都行 | `R` |
| 需要同时按住 Ctrl | 防和其它 mod 抢键 | 关 |
| 合并可堆叠物品 | 关掉则每件单独落一堆 | 开 |
| 跳过穿戴中/手持物品 | 关掉等于强制脱装备 | 开 |
| 跳过小背包里的物品 | 小包内的东西不迁移 | 开 |
| 支持腌肉盒 | 关掉则腌肉盒不再响应整理键 | 开 |
| 启用忽略清单 | 关掉则忽略清单完全不生效 | 开 |
| 保留时优先把高耐久放进容器 | 背包留耐久最低的那件 | 开 |
| 耐久相同时优先搬更重的 | 背包留单份更轻的那份（肉类：先搬 1kg 的块） | 开 |
| 输出详细日志 | 逐件打印迁移明细，排查问题用 | 关 |

### 卸载

删掉 `TheLongDark/Mods/ContainerSorter.dll` 即可。`Mods/ContainerSorter.json` 与 `Mods/ContainerSorterIgnore.txt` 可以留着也可以删掉；存档里不会留下本 mod 的字段。

### 兼容性

- 只在**容器面板或腌肉盒面板打开**时响应按键，其它时候按键没有任何作用。
- 不修改物品定义、堆叠规则或容器容量，只在按下按键的瞬间做搬运。
- 匹配依据是物品的 prefab 名，`RUINED` 状态或改过名字后缀的物品照样算同一种；若游戏切到新版 Input System 独占导致读不到键盘，热键会自行停用并在日志里说明。

### 常见问题

**按了没反应？**  
确认容器面板或腌肉盒是打开的；如果开了「需要同时按住 Ctrl」，要按 `Ctrl + 整理键`。键位也可能被别的 mod 占用，换一个试试。腌肉盒只收它已有的种类、只收整件，面板显示已满时也装不下。

**东西没搬，或者搬了一半就停了？**  
只搬「容器里已经有的种类」，容器满了就结束，剩下的留在背包。

**写了 `物品ID*数字`，数量还是没到目标？**  
容器里没有这种物品，或者不够多，只补到容器允许的数量为止。穿在身上、拿在手里的份数算进目标数量，但设置里勾了跳过的物品不会被搬动。

**没有并成一堆？**  
液体、新鲜度相差太大的食物不并堆；某类物品的数量变化不反映到重量上时，会退回「单独放一堆」，而不是冒险算错容量。

### 许可证

MIT —— 见 [LICENSE](LICENSE)。更新记录见 [docs/CHANGELOG.md](docs/CHANGELOG.md)。
