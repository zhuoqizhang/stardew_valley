# MyFirstMod 进度快照

> 生成时间：2026-09-06（无 git 仓库，以下基于源码内容 + 文件修改时间戳整理）

## 项目概况

SMAPI（《星露谷物语》）Mod，作者 zhzhuoqi，UniqueID `zhzhuoqi.MyFirstMod`，当前 manifest 版本 `1.0.0`。核心玩法：给 Pierre 商店加一套"期货交易"系统——玩家可以预先签约在未来某天以约定价格交割啤酒/淡啤酒，到期不交割则违约扣罚金。

## 已完成的模块

| 文件 | 状态 |
|---|---|
| [ModEntry.cs](ModEntry.cs) | 已接入 SMAPI 生命周期事件：`Saving`/`SaveLoaded`（存档持久化）、`DayEnding`（违约结算）、`DayStarted`（任务栏自愈重建）、`AssetRequested`（注入违约信邮件）。还挂了 Harmony 补丁入口。 |
| [ContractManager.cs](ContractManager.cs) | 核心逻辑：签约（`SignContract`）、交割（`FulfillContract`）、到期违约结算（`SettleDefaults`，差价罚金 = max(0, 市价×1.10 − 约定价)）、违约信邮件拼装（`QueueDefaultMail`，专门处理了 SpriteText 换行符 `^` 和 `=` 特殊字符的坑）、任务日志同步/自愈（`RebuildQuestLog`）、存档序列化（`ToSaveData`/`LoadFromSaveData`）。 |
| [FuturesContract.cs](FuturesContract.cs) / [FuturesContractSaveData.cs](FuturesContractSaveData.cs) | 数据模型 + 存档 DTO（`SDate` 用 `DaysSinceStart` 转 int 存取，因为 `SDate` 没有无参构造函数）。 |
| [FuturesMenu.cs](FuturesMenu.cs)（最大的文件，~33KB） | 两个 Tab 的 UI：交易 Tab（签约啤酒/淡啤酒期货）+ 当日交割 Tab（对着背包点击交割）。 |
| [Patches/PierreShopPatch.cs](Patches/PierreShopPatch.cs) | Harmony postfix 打在 `Utility.TryOpenShopMenu`，进店前弹"购买商品 / 期货交易 / 算了"三选一对话，仅拦截 `SeedShop`（Pierre），不影响 Robin 木匠铺等其他商店。 |
| [DateHelper.cs](DateHelper.cs) | "下下周五"到期日计算 + 中文日期格式化（如"春28日"）。 |

## 最近改动时间线（按文件 mtime）

DateHelper (8/9) → PierreShopPatch (8/16) → FuturesContract/SaveData (8/16) → FuturesMenu (8/22 22:31) → ModEntry (8/22 22:45) → **ContractManager (8/23 00:08，最新)**。

`bin/Debug/net6.0/MyFirstMod.dll` 的时间戳（8/23 00:08）和最后一次源码改动完全对齐，说明最新代码曾编译通过并生成过 dll。

ModEntry.cs 里大量 `[DIAG]` 诊断日志和注释说明，这一轮改动是在修一个具体 bug：**存档重新加载后任务日志（quest log）记住的期货承诺数量，比 `ContractManager.Contracts` 里实际数据要多**——现在已加上持久化（`Saving`/`SaveLoaded`）+ 每日自愈重建（`RebuildQuestLog`）双保险，日志里还留着回归检测（"Mismatch confirmed" 一旦再出现就说明又坏了）。

同时顺带修了一个隐藏坑：`InvalidateCache("Data/mail")` 字符串重载在非英文语言下匹配不到本地化后缀的资源名（如 `Data/mail.zh-CN`），导致违约信永远发不出去；已改成用 `NameWithoutLocale` 谓词重载。

## 目前偏"测试/占位"的部分

- F5/F6 是调试热键（F5 随机改啤酒价格，F6 直接开期货菜单），还没有正式的游戏内触发方式验证完整流程。
- `FuturesMenu` 标题写着"期货交易系统 - 测试中"，价格是硬编码占位值（啤酒 45G / 淡啤酒 300G），`GetMarketPrice` 也是写死的假市场价（啤酒 50G / 淡啤酒 65G），都还没接真实市场模块。
- 菜单里有专门的"(测试)"按钮/行，用明日到期而非正常的"下下周五"，方便快速触发违约测试。

## 已知问题 / 风险（2026-09-06 更新）

1. ~~保证金疑似未真正扣款~~ **已解决——但不是"补上扣款"，而是反转了方向**：2026-09-06 与用户确认后，保证金方向从"玩家签约时付给系统"改为"皮埃尔签约时预付给玩家，违约时没收"。详见 `plan.md` 第 5.5 节偏离记录，以及新建的 `SettlementMath.cs`（纯逻辑，`ComputeMargin`/`ComputePriceGapPenalty`/`ComputeDefaultDeduction`）+ `MyFirstMod.Tests` 项目（9 个测试全过，覆盖余额不足、双重 clamp 之类的边界情况）。
2. 项目没有 git 版本管理，进度只能靠文件时间戳推断，风险较高。
3. ~~没有独立设计文档文件~~ 已解决：用户提供了 `plan.md`，现在是仓库里的真实设计文档。

## 建议的下一步

1. 接入真实的市场价格系统，替换 `GetMarketPrice` 占位值。
2. 把 F5/F6 调试热键和菜单里的"(测试)"行为收掉或加开关，避免正式版里留调试入口。
3. 建一个 git 仓库做版本管理。
4. `plan.md` 第 8.3 节的"待办事项优先级"里还写着"违约结算（DayEnding）—— 未开始"，但实际上早就实现了，这份清单本身已经过时，值得找机会更新一遍。
