# 星露谷期货交易系统 —— 设计文档 v1.0（MVP）
 
> 本文档记录与皮埃尔交易的远期合约（Forward Contract）系统的完整设计，
> 涵盖数据结构、状态机、UI 布局与关键流程伪代码，供开发时直接参考实现。
 
---
 
## 1. 系统概述
 
### 1.1 本质说明
本系统实现的是**远期合约（Forward）**，而非标准期货：
- 价格在签约时锁定，不做逐日盯市（mark-to-market）
- 到期只有两种结局：**实物交割** 或 **违约**
- 不支持提前平仓（预留后续扩展）
 
### 1.2 核心规则速览
| 项目 | 规则 |
|---|---|
| 每单数量 | 固定为 1 |
| 保证金 | 约定交易额的 10%，**签约时由皮埃尔预付给玩家**（2026-09-06 起，见 5.5 节偏离记录） |
| 交割方式 | 仅限当面找皮埃尔，在其商店 UI 的"当日交割"栏操作 |
| 交割窗口 | 交割日当天，可反复尝试直到当晚 |
| 交割顺序 | 严格 FIFO（按签约时间排序），系统自动预演分配，玩家不可跳序 |
| 违约判定时机 | 交割日 `DayEnding`（夜间结算），当天未完成交割的合约一律判违约 |
| 违约罚金 | 没收（此前皮埃尔预付的）保证金 + `max(0, 违约日市场价×1.10 - 约定价) × 数量`，**两部分先合并成一个总额，再统一套用下方"罚金上限"规则**（不是分别对保证金、差价罚金各自套用上限） |
| 罚金上限 | 扣至玩家金币为 0 为止，不足部分免除，不倒欠 |
| 提醒方式 | 签约生成 Quest；交割日当天收到提醒信 |
 
---
 
## 2. 数据结构
 
### 2.1 合约表（FuturesContract）
 
| 字段名 | 类型 | 说明 |
|---|---|---|
| `ContractId` | string (GUID) | 合约唯一标识 |
| `ItemId` | string | 标的物品 ID（如啤酒） |
| `AgreedPrice` | int | 约定交割单价（签约时锁定） |
| `Margin` | int | 皮埃尔预付给玩家的保证金（= AgreedPrice × 10%，四舍五入）；签约时发放给玩家，违约时从玩家账户没收 |
| `SignedDate` | WorldDate / int (总天数) | 签约日期，用于 FIFO 排序 |
| `DueDate` | WorldDate / int (总天数) | 交割到期日 |
| `Status` | enum `ContractStatus` | 见下方枚举 |
| `PlayerId` | long | 所属玩家（多人模式预留） |
| `QuestId` | string \| null | 关联的任务栏 Quest ID，结算后清空 |
 
### 2.2 合约状态枚举
 
```csharp
public enum ContractStatus
{
    Pending,    // 挂起中，等待交割日
    Fulfilled,  // 已如约交割
    Defaulted   // 已违约（含罚金已结算）
}
```
 
### 2.3 玩家期货账户（可选，用于统计/UI 汇总）
 
| 字段名 | 类型 | 说明 |
|---|---|---|
| `TotalContractsSigned` | int | 累计签约数，用于解锁"卖出/平仓"功能的阈值判断 |
| `TotalDefaults` | int | 累计违约次数（可用于后续声誉/信用系统扩展） |
 
---
 
## 3. 状态机
 
```
                    ┌─────────────┐
   玩家下单签约  ──▶  │   Pending    │
                    └──────┬──────┘
                           │
              到期日当天，玩家找皮埃尔交割
                           │
              ┌────────────┴────────────┐
              │                         │
        背包中有该物品                背包中没有该物品
        （FIFO 队列中可交割）         （FIFO 队列中排队/背包为空）
              │                         │
              ▼                         │
        当场结算：                       │
        扣物品 + 玩家收款                 │
        Status → Fulfilled              │
        移除 Quest                      │
                                        │
                          当晚 DayEnding，仍为 Pending
                                        │
                                        ▼
                                 违约结算：
                                 没收保证金
                                 + 差价罚金（扣到0为止）
                                 Status → Defaulted
                                 移除 Quest，次日发违约通知信
```
 
---
 
## 4. UI 设计（继承 ShopMenu）
 
### 4.1 整体布局
 
```
┌───────────────────────────────────────────┐
│  [买卖期货]   [当日交割]   ← Tab 切换         │
├───────────────────────────────────────────┤
│                                             │
│  【买卖期货 Tab】                             │
│  [合约品种切换: 啤酒 | 三文鱼 | 铜矿 ...]        │
│  ┌───────────────┬─────────────────────┐   │
│  │  简易折线示意图   │  当前价格: 125G        │   │
│  │  (最近N个价格点) │  较昨日: +3.2%         │   │
│  │                │  可选交割日: 周五×N周    │   │
│  └───────────────┴─────────────────────┘   │
│  [做多] [做空]  数量: 点击累加(每次+1单)         │
│  当前拟签: 3 单 @ 125G，需保证金 37.5G(→38G)    │
│  [确认签约]                                  │
│                                             │
├───────────────────────────────────────────┤
│  【当日交割 Tab】                             │
│  今日到期合约（按 FIFO 排序展示）:               │
│                                             │
│  啤酒 x1  约定价45G  [可交割] → 绿色按钮        │
│  啤酒 x1  约定价50G  [可交割] → 绿色按钮        │
│  啤酒 x1  约定价48G  [库存不足，排队等待] → 灰色  │
│  三文鱼x1 约定价80G  [库存不足，排队等待] → 灰色  │
│                                             │
└───────────────────────────────────────────┘
```
 
### 4.2 交互要点
 
**买卖期货 Tab**
- 品种切换：复用 `ShopMenu` 分类按钮模式（`ClickableTextureComponent`）
- 折线图：无需图表库，用 `SpriteBatch` 手动画点连线，价格数组归一化映射到固定像素高度区域，可用绿/红区分涨跌趋势
- 数量：每次点击"做多/做空"按钮即生成一张独立合约（数量固定1），可连续点击多次生成多张
- 保证金：实时显示"当前拟签 N 单，需保证金 XG"，点击"确认签约"后统一扣款、批量写入合约表
 
**当日交割 Tab**
- 数据来源：每次打开时查询 `DueDate == 今日 && Status == Pending` 的合约，按 `SignedDate` 升序排列
- 状态预演算法：见第 5 节伪代码，决定每行是"可交割"（绿色）还是"排队等待"（灰色）
- 点击"交割"后：扣背包物品、玩家收款、状态改 `Fulfilled`、移除 Quest，**立即重新执行预演算法刷新整个列表**（因为库存变化可能让后续排队合约变为可交割）
 
---
 
## 5. 关键流程伪代码
 
### 5.1 签约（下单确认）
 
```
函数 SignContract(itemId, agreedPrice, dueDate):
    margin = Round(agreedPrice * 0.10)
    增加玩家金币 margin   // 皮埃尔预付保证金给玩家；这是发钱，不是收钱，不需要校验余额
 
    contract = new FuturesContract:
        ContractId = GenerateGuid()
        ItemId = itemId
        AgreedPrice = agreedPrice
        Margin = margin
        SignedDate = 当前游戏日期
        DueDate = dueDate
        Status = Pending
        PlayerId = 当前玩家
 
    questId = CreateQuest(
        title: $"{itemId}期货 - {dueDate}交割",
        description: $"约定价{agreedPrice}G，需在到期日找皮埃尔交割"
    )
    contract.QuestId = questId
 
    写入合约表(contract)
    玩家期货账户.TotalContractsSigned += 1
```
 
### 5.2 当日交割栏 —— FIFO 预演状态计算
 
```
函数 ComputeDeliveryQueueStatus():
    今日到期合约列表 = 查询(DueDate == 今日 && Status == Pending)
    按 SignedDate 升序排序(今日到期合约列表)
 
    库存计数器字典 = {}  // key: ItemId, value: 玩家背包中该物品数量
    对 今日到期合约列表 中每个 contract:
        若 库存计数器字典 不含 contract.ItemId:
            库存计数器字典[contract.ItemId] = 玩家背包.统计数量(contract.ItemId)
 
        若 库存计数器字典[contract.ItemId] > 0:
            contract.显示状态 = "可交割"（可点击）
            库存计数器字典[contract.ItemId] -= 1
        否:
            contract.显示状态 = "库存不足，排队等待"（不可点击）
 
    返回 今日到期合约列表（含显示状态，供UI渲染）
```
 
### 5.3 点击交割按钮
 
```
函数 OnDeliveryButtonClicked(contract):
    若 contract.显示状态 != "可交割":
        return  // 理论上不可点击，UI层已拦截
 
    若 玩家背包.数量(contract.ItemId) < 1:
        return  // 二次校验，防止并发/刷新延迟导致的异常
 
    扣除玩家背包(contract.ItemId, 1)
    增加玩家金币(contract.AgreedPrice)
    contract.Status = Fulfilled
    移除Quest(contract.QuestId)
 
    重新调用 ComputeDeliveryQueueStatus() 刷新整个列表UI
```
 
### 5.4 夜间违约结算（DayEnding 事件）
 
```
函数 OnDayEnding():
    今日到期未结算合约 = 查询(DueDate == 今日 && Status == Pending)
 
    对 今日到期未结算合约 中每个 contract:
        marketPrice = 获取当前市场价(contract.ItemId)
        差价罚金 = Max(0, marketPrice * 1.10 - contract.AgreedPrice) * 1  // 数量固定为1
        应扣总额 = contract.Margin + 差价罚金   // 没收此前预付给玩家的保证金 + 差价罚金，先合并成一个总额
 
        实际扣款 = Min(玩家当前金币, 应扣总额)  // 合并后统一套用上限，扣到0为止、不倒欠
        扣除玩家金币(实际扣款)                  // 注意：不能分两次分别对 margin、差价罚金各自 Min(...)，
                                              // 那样两次上限各自成立，但相加后仍可能超过玩家实际余额
 
        contract.Status = Defaulted
        移除Quest(contract.QuestId)
        玩家期货账户.TotalDefaults += 1
 
        标记待发信(次日,"违约通知", 内容: $"{contract.ItemId}期货违约，没收保证金{contract.Margin}G + 差价罚金{差价罚金}G，实际共扣{实际扣款}G")
```
 
> 注：保证金是皮埃尔在签约时预付给玩家的（非玩家上缴），因此违约结算时保证金和差价罚金
> **必须先相加成一个总额，再统一套用"扣到玩家金币为0为止、不倒欠"的上限规则**——不能先把
> 保证金单独 `Min(玩家金币, margin)` 扣一次，再把差价罚金单独 `Min(玩家金币, 差价罚金)` 扣一次，
> 那样两次各自都"不倒欠"，但两次扣款相加起来仍可能超过玩家扣款前的实际余额，等价于变相倒欠。
 
---
 
## 5.5 实现偏离记录：保证金方向反转（玩家→系统 改为 皮埃尔→玩家）
 
> 记录时间点：2026-09-06，存档持久化、任务栏自愈（7.5 节，记录时编号为 6.5，2026-09-11 因插入
> 第6节"市场定价模型"而整体后移，详见该节）、违约结算逻辑均已实现之后。
 
**偏离内容：**
本文档第 1.2、2.1、5.1、5.4 节原设计中，保证金的方向是**玩家签约时向系统支付保证金，违约时系统没收**。
经与玩家确认设计意图后，改为反向：**皮埃尔在签约时把保证金预付给玩家，违约时才从玩家账户没收回去**；
交割成功时保证金仍归玩家所有、不会被没收，但它是合约价的预付部分而非额外奖励——交割时应付的金额会
扣除已预付的保证金，玩家整个合约周期下来净得的总额刚好等于 `AgreedPrice`，不多不少（2026-09-13 澄清，
详见下方对照表最后一行）。
 
**发现过程：** 复查代码时发现 `ContractManager.SignContract` 从未真正执行"扣除玩家金币 margin"这一步——
只是把数值算出来存进了 `FuturesContract.Margin` 字段，`SettleDefaults` 也从未单独扣过保证金，
违约信里"没收保证金"那一行金额实际上玩家从未被扣过。这是原方向下一个尚未补完的实现缺口。
趁着要处理这个缺口，和玩家确认后直接反转了设计方向，而不是简单补上原方向缺失的扣款代码。
 
**改动前 / 改动后对照：**
 
| | 改动前（原设计方向） | 改动后（当前方向） |
|---|---|---|
| 签约时 | 玩家支付 margin 给系统；金币不足则中止签约 | 皮埃尔预付 margin 给玩家；发钱不需要校验余额 |
| 违约时 | 视 margin 为已扣（仅记录用），只对差价罚金部分做 `Min(玩家金币, 差价罚金)` | margin 与差价罚金**先相加成一个总额**，再统一 `Min(玩家金币, 总额)` |
| 交割成功时 | margin 留在系统手里，交割不涉及 margin 归还 | 交割款扣除已发放的 margin，玩家最终净得刚好等于合约价（`AgreedPrice`），margin 是预付款而非额外奖励 |
 
**改动原因：** 玩家（本文档使用者）明确要求反转方向，已通过对话确认设计意图如此，非误改；
不属于需要进一步讨论的开放问题。

**2026-09-13 澄清：margin 是预付款，不是独立奖励。** 上线后发现代码里交割成功时是
`Money += AgreedPrice`（发全额），完全没有扣减 margin——这与本节最初讨论时留下的表述一致（原表格
"交割成功后不追讨，玩家净得 margin"，暗示 margin 是签约时到手、交割时还能额外再拿一次的独立奖励，
两笔钱互不影响），但玩家复核后明确要求改为"margin 是 `AgreedPrice` 的预付部分"这个口径：
交割时改为 `Money += (AgreedPrice - Margin)`（见 `SettlementMath.ComputeDeliveryPayout`），
使玩家整个合约周期净得总额恰好是 `AgreedPrice`，而不是 `AgreedPrice + Margin`。
**违约结算（`SettleDefaults`）的公式不受影响，无需改动**：没收 margin 本身就是把预付款要回去，
差价罚金是独立于"margin 是不是预付款"这件事之外、由市场价偏离触发的额外惩罚，两者从设计上就没有
耦合，见 5.4 节和上表"违约时"一行——这行在本次澄清前后含义没有变化。
 
---
 
## 6. 市场定价模型

> 记录时间点：2026-09-11 定稿，2026-09-14 正式接入。本节记录啤酒/淡啤酒每日市场价格的模拟定价
> 模型——经过多轮离线模拟调参后定稿，落地为独立纯逻辑类 `PriceModel.cs`（零 SMAPI/Game1 依赖，
> 风格仿照 `SettlementMath.cs`）。**接入状态（已生效）**：`PriceModel` 在每日 `DayStarted` 时算出
> 当日模拟价并写入价格历史；`ContractManager.GetMarketPrice`（违约结算取价的入口）现在读取这套
> 模拟价格的最新一条，不再直接返回游戏原生真实售价（详见 6.6 节）。

### 6.1 设计动机：为什么要分层叠加

单一的随机游走（每天纯随机涨跌）做不出这套系统想要的手感：它既不会有玩家能通过观察发现的确定性
规律，也不会有"整体温和、偶尔剧烈"的厚尾波动。因此把模型拆成 7 层，每层各自解决一个独立诉求、
各自可以单独写纯函数测试：

| 层 | 解决的诉求 |
|---|---|
| ① 均值回归 | 防止价格无止境游走偏离到不合理区间，把系统"拉回"锚点附近 |
| ② 独立噪声 | 每天最基础的"手感"波动来源，且啤酒和淡啤酒各自独立，不会永远同步 |
| ③ 周几周期 | 刻意设计的**隐藏规律**：交割日（周五）前后价格系统性偏高，周一前后偏低，不告知玩家，
| | 奖励认真观察价格走势、试图"逢低签约、逢高交割"的玩家 |
| ④ 节日脉冲 | 让游戏本身的 8 个节日在这套经济系统里也有存在感，呼应节日期间摆摊/送礼等经济活动的设计精神 |
| ⑤ 极端事件（跳跃） | 模拟现实市场的黑天鹅冲击，避免价格曲线长期过于"温顺"、可预测 |
| ⑥ 季节乘数 | 呼应星露谷本身的四季经济主题（夏季饮品类商品景气度更高的直觉设定） |
| ⑦ 长趋势（双锚点） | 让价格在跨越数十天的尺度上呈现出方向性的"上行/下行"阶段，而不只是绕均值反复震荡 |

7 层按上表顺序**依次叠加**在前一层的输出上（详见 6.2 节公式），即"昨日价格 → 层① → 层② → …
→ 层⑦ → clamp → 今日价格"这样一条流水线，而不是 7 个独立分量相加。

### 6.2 分层公式与参数表

设 `P0` = 该物品的游戏原生真实售价（`ContractManager.GetNativeSellPrice`，永远不变），
`price` 为流水线中间变量，初值 = 昨日价格。

**① 均值回归**（回归力度 `k = 0.2`，向 `P0` 拉回）：
```
price = 昨日价格 + k × (P0 - 昨日价格)
```

**② 独立噪声**（每个物品各自独立抽样，幅度 4%~5%，符号随机）：
```
magnitude ~ Uniform(0.04, 0.05)
sign ~ {-1, +1} 各 50%
price = price × (1 + sign × magnitude)
```

**③ 周几周期**（确定性偏移，交割日周五前后偏高、周一前后偏低）：

| 周几 | 偏移 |
|---|---|
| 周一 | -3% |
| 周二 | -1.5% |
| 周三 | 0% |
| 周四 | +1.5% |
| 周五 | +3% |
| 周六 | +1.5% |
| 周日 | -1.5% |

```
price = price × (1 + 周几偏移)
```

**④ 短期节日脉冲**（全部 8 个原生节日均触发，窗口=节前3天～节后2天，共6天）：

| 节日 | 季节/日期 |
|---|---|
| 蛋蛋节 Egg Festival | 春13日 |
| 花舞会 Flower Dance | 春24日 |
| 海之节 Luau | 夏11日 |
| 萤火虫舞会 Dance of the Moonlight Jellies | 夏28日 |
| 秋季集市 Stardew Valley Fair | 秋16日 |
| 幽灵节 Spirit's Eve | 秋27日 |
| 冰雪节 Festival of Ice | 冬8日 |
| 冬日盛宴 Feast of the Winter Star | 冬25日 |

| 距节日天数 | 偏移 |
|---|---|
| 节前3天 | +2% |
| 节前2天 | +4% |
| 节前1天 | +7% |
| 节日当天 | +10%（峰值） |
| 节后1天 | +3% |
| 节后2天 | -5%（回落） |
| 窗口外 | 0% |

```
price = price × (1 + 节日脉冲偏移)
```

**⑤ 极端事件（跳跃）**（每天 8% 概率触发，触发时对全部物品共享同一次冲击，各自按 beta 缩放）：
```
triggered ~ Bernoulli(0.08)          // 每天判定一次，非按物品判定
若 triggered:
    magnitude ~ Uniform(0.25, 0.40)
    sign ~ {-1, +1} 各 50%
    sharedShock = sign × magnitude   // 当天所有物品共享这同一个 sharedShock
    price = price × (1 + sharedShock × beta)   // beta 见 6.3 节
```

**⑥ 季节乘数**（确定性，随游戏季节切换）：

| 季节 | 乘数 |
|---|---|
| 春 | 1.0 |
| 夏 | 1.1 |
| 秋 | 1.0 |
| 冬 | 0.9 |

```
price = price × 季节乘数
```

**⑦ 长趋势（双锚点）**（每日趋势推力 1.5%，方向由最近的"大节日"锚点决定）：

只用两个"大节日"作为长趋势方向锚点：**秋季集市（秋16日）** 和 **冬日盛宴（冬25日）**
（这两个日期同时也是 6 节日脉冲表里的两个节日——短期脉冲和长趋势锚点互不冲突，是两层独立效果）。

判定规则：**临近锚点 → 上涨趋势；刚过锚点 → 下跌趋势**；下跌区间在"刚过锚点"到"两锚点中点"
之间，上涨区间在"中点"到"下一个锚点（含当天）"之间：

```
dayOfYear = 季节序号(0~3) × 28 + 当月日期        // 1~112，一年112天
a1 = 72   // 秋16日（秋季集市）
a2 = 109  // 冬25日（冬日盛宴）
gapA = a2 - a1 = 37     // 秋季集市 → 冬日盛宴，37天
gapB = 112 - gapA = 75  // 冬日盛宴 → 下一年秋季集市，75天

posFromA1 = ((dayOfYear - a1) mod 112 + 112) mod 112   // 0~111，"距上次经过a1的天数"

若 posFromA1 == 0:              趋势 = 上涨   // 当天正好是秋季集市
否则若 posFromA1 <= gapA:        // 落在 (a1, a2] 区间
    若 posFromA1 <= gapA/2（取整，18）: 趋势 = 下跌   // 刚过秋季集市
    否则:                              趋势 = 上涨   // 临近冬日盛宴
否则:                            // 落在 (a2, a1+112] 区间
    posFromA2 = posFromA1 - gapA
    若 posFromA2 <= gapB/2（取整，37）: 趋势 = 下跌   // 刚过冬日盛宴
    否则:                              趋势 = 上涨   // 临近下一年秋季集市

price = price × (1 + 趋势方向 × 0.015)
```

按此规则实测会产生 **18天/19天** 一组（来自 37 天短间隔的两半）和 **37天/38天** 一组
（来自 75 天长间隔的两半）交替出现的趋势窗口，与本节设计目标"18天/37天两种交替时长的趋势窗口"一致。

**Clamp（取值范围保护）**：以上 7 层全部叠加完成后，最终价格统一钳制到 `[P0×0.4, P0×2.2]`
区间内，`P0` 是该物品的游戏原生真实售价，永远不随模拟结果改变。

### 6.3 跨物品关联（beta 缩放）

啤酒与淡啤酒共享第④节日脉冲层、第⑤跳跃层（同一个 `sharedShock`）、第⑥季节乘数层、第⑦长趋势层——
这四层统称"市场因子"，两个物品在同一天读到的市场因子完全相同（跳跃层按各自 beta 缩放后除外）。
各自独立、不共享的只有第②独立噪声层。

| 物品 | Beta |
|---|---|
| 啤酒 Beer | 1.0（基准） |
| 淡啤酒 Pale Ale | 0.6（同向但更温和） |

这样制造出"同涨同跌但不完全同步"的相关性——离线模拟测算两者日收益率相关系数约 **0.89**：
足够高、看得出明显联动，但又不是完全锁定的 1.0，留出各自独立噪声造成的日常小分歧。

### 6.4 参数来源说明

> 本节的所有具体数值（回归力度 0.2、噪声 4%~5%、周几偏移表、节日脉冲表、跳跃概率 8%/幅度
> 25%~40%、季节乘数表、趋势推力 1.5%）都是**经过多轮离线模拟调参后确定的参数，不是拍脑袋数字**。
> 6.2/6.3 节的公式结构本身已经过验证，能稳定产出预期的"18天/37天交替趋势窗口"和"约0.89的
> 啤酒/淡啤酒相关系数"这两个结构性结论。**如果后续体感觉得不对（比如波动太剧烈/太温吞、
> 节日感不够强），应该优先调整本节列出的参数常量取值，而不是改动 6.2/6.3 节的公式结构**——
> 公式结构改了，上面两条结构性结论就需要重新验证，参数常量的调整则不会。

### 6.6 与 GetMarketPrice 的接入状态（已正式生效）

> 记录时间点：2026-09-11 写下过渡期方案，2026-09-14 正式切换生效。

`ContractManager.GetMarketPrice` 是违约结算（5.4 节差价罚金）取价的唯一入口。**现在它读取的是
`PriceModel` 算出的当日模拟价格**——具体做法是返回 `BeerPriceHistory`/`PaleAlePriceHistory`
（`ModEntry` 的 `DayStarted` 事件上、每天调用一次 `UpdateDailyPrices` 追加进去的历史序列）里最新
的那一条，不再直接返回游戏原生真实售价。

**降级兜底**：如果对应物品的价格历史仍然为空（正常流程下不会发生——`UpdateDailyPrices` 在每个
`DayStarted` 都会跑，严格早于当天任何 `DayEnding` 结算能看到"今天到期"的合约；但为了不让一个全新
存档/host 万一在第一次 `DayStarted` 之前就跑到了违约结算而抛异常或算出荒谬的结果），`GetMarketPrice`
会退回到 `GetNativeSellPrice`（游戏原生真实售价）——一个总是可用、合理的兜底值，而不是返回 0 或抛出
异常。

**没有变的部分**：`AgreedPrice`（合约约定价）依然只在签约那一刻由 `GetNativeSellPrice` 锁定
（`SignContractAsHost`），签约之后终身不变；这次切换只影响"违约时用来衡量约定价偏离了多少的市场价"
这一项，不影响合约本身的定价方式。这两个方法此前就是刻意分开的两件事（前者是合约本身的定价，
后者是判定违约罚金时的比较基准），这次切换没有改变这个分工，只是把后者的数据源从"原生价"换成了
"模拟价"。

---

## 7. 多人同步架构（Host 权威模型）

> 记录时间点：2026-09-11 ～ 2026-09-14。本节完整记录"支持真正的远程联机"这一轮多阶段重构的最终架构——
> 起因、反编译确认的关键事实、消息协议、三个分岔点的最终选择、host 权威模型的具体设计、过程中发现并
> 修复的一处竞态漏洞，以及和第 5.5 节经济模型修正的关系。代码里大量注释写的是"plan.md section 7"，
> 指的就是本节。

### 7.1 背景与设计动机

在这轮重构之前，本 mod 完全没有考虑过远程联机场景：`ContractManager` 是每个客户端进程各自一份、互不
相通的内存状态，`Helper.Data.WriteSaveData`/`ReadSaveData` 无条件调用，`SettleDefaults`/
`UpdateDailyPrices` 也无条件调用。逐一确认后发现三个实际问题：

1. **远程联机下会直接崩溃**：反编译 SMAPI 的 `DataHelper.WriteSaveData`/`ReadSaveData` 确认，两者内部
   都有 `if (!Context.IsOnHostComputer) throw new InvalidOperationException(...)`——任何真正的远程
   farmhand（不同物理电脑），在存档/读档时会崩溃。
2. **`DayEnding`/`DayStarted` 是每个客户端各自触发一次**：反编译确认 `SMultiplayer`/`SCore` 把
   `GameLoop.DayEnding` 挂在原版 `Game1.newDayAfterFade` 上，没有任何 `IsMainPlayer` 判断——每个装了
   本 mod 的客户端都会各自跑一遍 `SettleDefaults`/`UpdateDailyPrices`。
3. **合约数据从未在客户端之间同步**：`ContractManager.Contracts` 从始至终只存在于产生它的那个客户端
   进程里，farmhand 签的合约、host 永远看不到，反之亦然。

问题 1+2 叠加共享钱包（`FarmerTeam.money`，反编译确认，见 7.2 节）意味着：如果多个客户端都对同一份共享
钱包各自执行一次违约扣款，会造成真实的重复扣款。问题 3 意味着"任意玩家可交割任意人的合约"这个目标在
当时的架构下根本无法达成——不是过滤条件的问题，是压根没有跨客户端的数据同步机制。

整个重构因此拆成两部分：**第一部分"止血"**（7.3 节）先解决崩溃和重复扣款，代价是 farmhand 端功能不完整；
**第二部分"同步架构"**（7.4 节起）在此之上补上真正的跨客户端数据流转，让 farmhand 端的功能变得完整。

### 7.2 贯穿整个架构的关键反编译结论

以下事实均通过反编译 `StardewModdingAPI.dll`/`Stardew Valley.dll` 确认，不是凭训练记忆假设：

- **`Context.IsMainPlayer`**（`Game1.IsMasterGame && ScreenId==0 && !(TitleMenu.subMenu is FarmhandMenu)`）：
  判断"逻辑主机"——单机时恒真，联机时只有真正的房主为真，与物理机器无关。用于"这件事整个会话只应该
  发生一次"的场景（`SettleDefaults`/`UpdateDailyPrices`/签约与交割的权威裁决）。
- **`Context.IsOnHostComputer`**（`!IsMainPlayer ? IsSplitScreen : true`）：判断"是否与主机同一台物理
  电脑"——主机自己和分屏模式下共享同一台电脑的其他玩家都为真，只有真正远程联机的 farmhand 为假。用于
  精确匹配 `Helper.Data.WriteSaveData`/`ReadSaveData` 自己的崩溃条件。
- **`Farmer.Money` 在共享钱包下直接读写同一份共享池**：反编译 `Farmer._money`/`FarmerTeam.GetMoney`
  确认，`useSeparateWallets == false` 时，`Farmer.Money` 的 getter/setter 最终都落在同一个
  `FarmerTeam.money`（网络同步的 `NetIntDelta`）上——任何客户端的 `Game1.player.Money += x` 已经就是
  正确的共享钱包操作，不需要额外的跨玩家 API。
- **`Helper.Multiplayer.SendMessage` 是星型拓扑，host 是强制中转站**：反编译 `SMultiplayer.
  BroadcastModMessage`/`ReceiveModMessage` 确认，非 host 发送者的消息只会发给 `HostPeer`，farmhand
  之间不能互相直连；host 收到后如果目标玩家里包含别人，会主动转发。这也是选择"host 权威"架构的根本
  原因——网络层本身就已经是这个形状。
- **消息用 Newtonsoft.Json 序列化，走 `NetDeliveryMethod.ReliableOrdered`**：反编译
  `LidgrenServer.sendMessage` 确认直连/局域网连接下可靠且保证顺序；Galaxy（Steam/GOG）连接没有反编译到
  同等确凿的字节级证据，但和游戏本体所有其他关键状态共用同一条消息通道，合理推断同样可靠。
- **`playerIDs: null` 的广播会触发发送者自己的 `OnModMessageReceived`（自投递）**：反编译
  `BroadcastModMessage` 的 `flag` 判断确认——`toPlayerIds == null` 时 `flag` 恒为 `true`，发送者自己
  也会同步收到一份。本架构没有依赖这个行为（host 一律直接本地调用权威方法，不通过自投递），但所有
  "整体替换/幂等应用"类方法（`ApplyContractSignedFromNetwork`/`ApplyContractFulfilledFromNetwork`）
  都按"可能被调用不止一次"设计，这个自投递事实验证了这个防御性设计确实会被触发，不是过度设计。

### 7.3 止血阶段（第一部分）：崩溃与重复扣款修复

- `ModEntry.OnSaving`/`OnSaveLoaded`：用 `Context.IsOnHostComputer` 做门槛，farmhand 端跳过存档
  读写，避免崩溃。
- `ModEntry.OnDayEnding`（`SettleDefaults`）/`OnDayStarted`（`UpdateDailyPrices`）：用
  `Context.IsMainPlayer` 做门槛，保证这两件事整个会话只发生一次。
- 完成后的中间状态：远程联机下不再崩溃、不再重复扣款/重复算价，但 farmhand 端看到的合约列表、价格
  数据仍然是空的（因为读写存档被跳过，且没有任何同步机制）——这不是最终目标，只是防止崩溃的过渡态。

### 7.4 消息协议完整清单

所有消息类型定义在 `MultiplayerMessages.cs`，类型常量集中在 `MultiplayerMessageTypes`：

| 消息类型 | 方向 | 用途 | 关键字段 |
|---|---|---|---|
| `RequestSignContract` | farmhand → host | 请求签约；host 是唯一权威，价格/到期日都由 host 自己重算，不信任线路上的值 | `ItemId`、`DueDateKind`（枚举，不传具体日期）、`RequesterPlayerId` |
| `ContractSigned` | host → 所有人 | 合约已创建，广播确认 | `Contract`（复用 `FuturesContractSaveData`） |
| `RequestDeliver` | farmhand → host | "我点击了背包里的这个物品，有没有合约可以交割" | `ItemId`、`RequesterPlayerId` |
| `DeliverApproved` | host → 请求者 | FIFO 找到并锁定了一张合约，批准交割 | `ContractId`、`AgreedPrice` |
| `DeliverRejected` | host → 请求者 | 没有可交割的合约 | `ItemId`、`Reason` |
| `DeliverConfirmed` | farmhand → host | 批准后的扣物品/加钱已在本地成功执行 | `ContractId` |
| `DeliverFailed` | farmhand → host | 批准后发现物品已经不在背包里，防御性失败 | `ContractId` |
| `ContractFulfilled` | host → 所有人 | 锁定的合约最终确认为 Fulfilled | `ContractId`（其余字段所有客户端早已从 `ContractSigned` 得知，不需要重传） |
| `ContractStateSync` | host → 所有人 / host → 新加入的玩家 | 全量状态快照（合约列表 + 价格历史），违约结算后、每日价格更新后、新玩家加入时发送 | `Contracts`（复用 `List<FuturesContractSaveData>`）、`MarketPrices`（复用 `MarketPriceSaveData`） |

`Margin` 不出现在任何消息里——它是 `AgreedPrice` 的确定性函数（`SettlementMath.ComputeMargin`），任何
客户端拿到 `AgreedPrice` 就能自己算出一致的结果，不需要额外传输。

### 7.5 三个分岔点的最终选择

| 分岔点 | 最终选择 | 简要原因 |
|---|---|---|
| 签约流程 | **方案B：请求-审批式** | 让"host 是唯一数据权威"这条原则在协议里没有例外，即使签约本身没有真正的资源争用 |
| 交割流程 | **方案1：预先锁定式验证** | 钱和物品的正确性优先于极小概率冲突场景下的手感；先斩后奏式的回滚逻辑历史上是多人游戏最容易出漏洞的地方 |
| 数据广播方式 | **全量同步** | 这个 mod 的数据量级小（现实中同时挂着的合约是个位数，价格历史封顶 90 天×2 物品），全量同步在实现复杂度和正确性上双赢；增量同步最终还是要再实现一遍全量同步作为重连兜底，等于工作量更大 |
| 任务栏可见性 | **方案A：不改动 `RebuildQuestLog`** | `RebuildQuestLog` 本来就没有按签约人过滤，只要全量同步让 `Contracts` 在所有客户端上一致，farmhand 打开任务日志自然就能看到所有人的未结算合约——零额外实现成本，是同步架构的自然副产品 |

### 7.6 签约流程（方案B：请求-审批式）

1. farmhand 点击签约按钮 → `FuturesMenu` 判断 `Context.IsMainPlayer`：
   - 是 host：直接同步调用 `ContractManager.SignContractAsHost(itemId, dueDateKind, 自己的PlayerId)`，
     不经过网络。
   - 不是 host：调用 `ContractManager.RequestSignContract(itemId, dueDateKind)`，只发送
     `RequestSignContract` 消息，不改动任何本地状态；`FuturesMenu` 置 `isPendingSignRequest = true`，
     禁用签约按钮并显示"等待房主确认..."。
2. host 收到 `RequestSignContract`（`ModEntry.OnModMessageReceived`，`Context.IsMainPlayer` 门槛）→
   调用 `SignContractAsHost`：自己用 `GetNativeSellPrice`/`DateHelper.ResolveDueDate` 重新计算价格和
   到期日（完全不信任消息里除 `ItemId`/`DueDateKind` 以外的任何计算结果——但当前实现里
   `RequestSignContractMessage` 本来就没有携带价格/日期字段，这条原则从协议设计上就没有例外可钻）、
   创建合约、发保证金（`Game1.player.Money += contract.Margin`）。
3. `SignContractAsHost` 把结果通过 `ApplyContractSignedFromNetwork` 应用到自己的本地状态（这是唯一
   真正修改 `Contracts`/任务栏的方法，host 本地调用和其他客户端收到广播后调用走的是同一个方法），
   然后广播 `ContractSigned` 给所有人（`playerIDs: null`）。
4. 每个客户端（含 host 自己，可能通过自投递收到，见 7.2 节）收到 `ContractSigned` 后调用
   `ApplyContractSignedFromNetwork`——按 `ContractId` 去重，重复调用是安全的空操作。
5. `FuturesMenu` 订阅 `ContractManager.ContractSignedApplied` 事件，收到后清除 `isPendingSignRequest`
   并显示"签约成功：...G，获得保证金...G"。

已知的、有意接受的局限：合约之间没有签约人归属（符合"资金全部计入共享池"的多人协作前提），如果
另一个玩家恰好在自己请求还没返回时也签了一张合约，`ContractSignedApplied` 可能先展示那一张的反馈
文字——纯粹是提示文字层面的巧合，不影响钱/合约本身的正确性。

### 7.7 交割流程（方案1：预先锁定式验证）

**"处理中"状态的实现**：不是新增 `ContractStatus` 枚举值，而是 `ContractManager` 内部一个纯运行时的
`private readonly HashSet<string> lockedContractIds`（存 `ContractId`）。选择这个实现方式而不是枚举值，
是因为枚举值会被 `ToSaveDataEntry`/`ToSaveData()` 一起序列化进存档——如果存档恰好卡在锁定期间，合约会
被永久卡在一个没有任何代码知道该怎么处理的状态里。用独立的 `HashSet<string>` 从结构上排除了这个风险：
它和 `ToSaveData()` 之间没有任何代码连接，进程重启后自然清空为空集。

流程：

1. farmhand 点击背包物品 → `FuturesMenu.OnInventoryItemClicked` 判断 `Context.IsMainPlayer`：
   - 是 host：直接同步调用 `ContractManager.TryLockNextDeliverableContractAsHost(itemId, ...)`。
   - 不是 host：调用 `ContractManager.RequestDeliver(itemId)`，只发消息，不做任何本地扣减；置
     `isPendingDeliveryRequest = true`。
2. host（无论是自己同步调用，还是收到 `RequestDeliver` 消息）执行 FIFO 查询：`Contracts` 里
   `Status == Pending && ItemId匹配 && DueDate == 今天 && 不在lockedContractIds里`，按 `SignedDate`
   升序取第一个，命中则加入 `lockedContractIds` 并返回。**这里不检查库存**——请求本身就是点击背包里
   已经存在的物品触发的，"有没有这个物品"这件事在请求发出的那一刻就已经成立。
3. 命中：farmhand 端收到 `DeliverApproved(contractId, agreedPrice)` 后才真正执行"扣背包物品1个 +
   `Money += (agreedPrice - margin)`"（`margin` 用 `SettlementMath.ComputeMargin(agreedPrice)` 本地
   现算，不需要合约的完整记录，也不需要消息里带 `Margin` 字段——这一点在阶段四金额修正时专门确认过，
   见 7.10 节），然后发送 `DeliverConfirmed`。
   未命中：收到 `DeliverRejected`，显示"暂无可交割合约"，不碰库存/金钱。
4. host 收到 `DeliverConfirmed` → `HandleDeliverConfirmedAsHost`：从 `lockedContractIds` 移除，调用
   `ApplyContractFulfilledFromNetwork`（按 `ContractId` 查找、`Status != Pending` 则空操作的幂等方法）
   标记 Fulfilled，广播 `ContractFulfilled` 给所有人。
   host 收到 `DeliverFailed`（farmhand 发现物品已经不在背包里的防御性失败路径）→
   `HandleDeliverFailedAsHost`：只从 `lockedContractIds` 移除，合约回到 Pending，供下一个排队者认领。

**和 `BuildDeliveryRows`/FIFO 预演算法的关系**：预演算法完全没有改动，因为它本来就是"如果只有我一个人
在交割"的个人估算展示，从来不是真正裁决交割归属的权威逻辑——真正的裁决完全交给 7.7 节这套机制。
`lockedContractIds` 是 host 私有状态、从不广播，farmhand 端的本地 `Contracts` 镜像结构上就没有能力
知道"这张合约现在是不是正被别人抢单中"。多人场景下的实际后果：预演栏显示的"可交割"数量可能因为别人
正在交割同一批合约而暂时过时，最坏情况下点击后收到 `DeliverRejected`——不是钱/物品层面的错误，只是
"重新点一下就好"的体验小摩擦。

### 7.8 全量同步与新玩家加入补发

三处触发全量同步（`ContractManager.BroadcastContractStateSync`，内部调用
`multiplayerHelper.SendMessage(..., MultiplayerMessageTypes.ContractStateSync, playerIDs: ...)`）：

1. `SettleDefaults`：仅当 `dueToday.Count > 0`（真的有合约违约）时广播给所有人（`playerIDs: null`）——
   没有变化就不用广播。
2. `UpdateDailyPrices`：每次调用后无条件广播给所有人——每天必然产生新的一条价格历史，没有"什么都没变"
   的空转场景。
3. `ModEntry.OnPeerConnected`（反编译确认：`PeerConnected` 在游戏批准连接之后才触发，此时新玩家的
   Farmer 已经真实存在，是发送消息的安全时机；处理函数内部无论如何都判断 `Context.IsMainPlayer`，
   不管这个事件到底只在 host 触发还是所有客户端都收到，只有真正的 host 会执行发送）→
   `ContractManager.SendFullStateSyncTo(玩家ID)`，只发给这一个新连接的玩家。这一步补上了此前止血阶段
   遗留的缺口——"farmhand 中途加入看不到之前已经存在的合约/价格历史"，从止血阶段的"已知限制"变成了
   "已解决"。

接收端 `ApplyContractStateSyncFromNetwork` 是一个很薄的包装：`LoadFromSaveData(message.Contracts)` +
`LoadMarketPriceSaveData(message.MarketPrices)` + `RebuildQuestLog()`——复用现成的存档读取方法做"整体
清空重建"，不重新实现一遍。**无条件调用 `RebuildQuestLog()`，不做增量对比**：这正是"任务栏可见性方案A"
真正生效的机制——一旦每个客户端的 `Contracts` 通过全量同步变得一致，本来就没有按签约人过滤的
`RebuildQuestLog` 自然会把farm-wide 所有未结算合约显示进每个人自己的任务栏。没有做增量对比是刻意的：
触发全量同步的三个场景频率都很低（每天最多几次），`RebuildQuestLog` 本身也已经被设计成"可以安全地
每天早上无条件重复调用"，加一层 diff 判断的收益配不上复杂度。也没有让全量同步触发
`ContractSignedApplied` 这类"我的操作被确认了"的事件——那类事件专属于"这是我自己发起的请求"，全量同步
是被动的后台补齐机制，混在一起会在别人操作时对本地玩家弹出误导性的反馈文字。

**验证过的边界情况**：farmhand 在"已发送交割/签约请求、尚未收到批准"这个等待窗口期，如果恰好收到一次
全量同步，不会产生冲突——等待状态（`isPendingSignRequest`/`isPendingDeliveryRequest`/
`pendingDeliveryItemId`/`pendingDeliveryClickX/Y`）全部是 `FuturesMenu` 自己的私有字段，
`ApplyContractStateSyncFromNetwork` 及其调用的所有方法都只碰 `ContractManager.Contracts`/价格历史/
`Game1.player.questLog`，`ContractManager` 结构上不持有任何 `FuturesMenu` 实例的引用，两者之间没有
任何代码路径可以互相影响。

### 7.9 已发现并修复的竞态问题：交割锁定与违约结算的冲突

**问题**：`SettleDefaults` 最初的查询条件只看 `Status == Pending && DueDate == today`，完全没有排除
`lockedContractIds` 里的合约。如果一张合约恰好处于"host 已批准交割、farmhand 还没来得及确认"这个窗口
期，而这时候触发了当晚的 `DayEnding`（例如 host 自己先去睡觉，farmhand 还醒着正在交割），这张
"马上要交割成功"的合约会被直接判违约——没收保证金 + 差价罚金，这是真实的错误扣款，不是体验问题。

**第一轮修复及其暴露的更深问题**：把 `lockedContractIds` 加入排除条件本身是对的，但排查后发现现有的
锁释放机制（`HandleDeliverConfirmedAsHost`/`HandleDeliverFailedAsHost`，分别对应 `DeliverConfirmed`/
`DeliverFailed` 消息）**没有任何超时兜底**——锁只会在收到 farmhand 主动发回的这两条消息之一时才释放。
如果 farmhand 在"收到 `DeliverApproved`"和"发出确认/失败消息"这个窗口期掉线、崩溃或强制退出，锁会
永久留在 `lockedContractIds` 里。叠加上面的排除条件，这样的合约会**永久免于违约判定**——既不会交割
成功（没人确认），也不会违约（被排除在判定之外），从整个经济系统里彻底消失。这是一个几乎不需要精确
时机就能触发的、可被利用的规则漏洞，不是理论上存在但难以触发的边界情况。

**最终修复**：`SettleDefaults` 开头无条件清空整个 `lockedContractIds`，再查询 `dueToday`（不再需要
额外的排除条件——清空之后集合必然为空，查询发生时不可能有任何合约仍处于锁定状态）：

```csharp
public void SettleDefaults(SDate today)
{
    lockedContractIds.Clear();

    List<FuturesContract> dueToday = Contracts
        .Where(c => c.Status == ContractStatus.Pending && c.DueDate.Equals(today))
        .ToList();
    // ...
}
```

**为什么在这里无条件清空是安全的**：`SettleDefaults` 每晚只运行一次（`DayEnding`），而正常的锁定
——从批准到确认/失败——预期在白天的几百毫秒内就会 resolve。等到夜里 `SettleDefaults` 运行时，任何
还活着的锁，按定义就已经是"请求方已失联/放弃"，不可能是"仍在进行中"。清空之后，这张合约会在**同一次**
`SettleDefaults` 调用里正常参与今晚的违约判定——一个卡死的锁最多只能撑到锁定发生当天的夜间结算，
不可能被用来无限期逃避违约。

**为什么没有为这个修复单独写单元测试**：`SettleDefaults` 本身依赖 `Game1.player.Money`/
`Game1.player.questLog`/`ItemRegistry.Create`，脱离真实游戏进程无法构造，和这个方法里其他所有逻辑
一样不可单测。这次修复本质上是"在方法里调整了一行代码的调用时机"（把 `Clear()` 放在查询之前），不是
新增了一段可以被独立抽出来验证的数学/算法逻辑（不像 `SettlementMath` 系列函数那样有边界值、舍入这些
真正需要测试覆盖的风险点），所以没有勉强抽一个人造的最小单元来测。

### 7.10 与第 5.5 节经济模型修正的关系

这一轮重构过程中，另外发现并修正了一个和多人同步本身无关、但影响面同样是"玩家实际能拿到多少钱"的
问题：交割成功时代码一直是 `Money += AgreedPrice`（发全额），完全没有扣减签约时已经预付的 `Margin`，
导致玩家顺利交割一张合约能拿到 `AgreedPrice + Margin`（110%），而不是设计文档 5.5 节最初讨论时应该
是的 `AgreedPrice`（100%，margin 是预付款、不是独立奖励）。修正为 `Money += (AgreedPrice - Margin)`
（`SettlementMath.ComputeDeliveryPayout`），`SettleDefaults` 的违约结算公式**没有**跟着改——没收
margin 本身就是把预付款要回去，差价罚金是独立于"margin 是不是预付款"这件事之外、由市场价偏离触发的
额外惩罚，两者从设计上从未耦合。完整的改动前后对照、发现过程见第 5.5 节（该节已经在 2026-09-13 更新
过，本节不重复）。

### 7.11 已知限制与未验证事项

- **没有做过真机联机测试**。本节记录的每一条流程结论，验证方式都是代码走查 + 反编译佐证（确认调用
  链、字段读写范围、消息路由语义），不是拿两台真实客户端连起来跑一遍。签约（阶段二）、交割（阶段三）
  在单机模式下（`Context.IsMainPlayer` 恒真，走host同步分支）已经过用户手动验证；`PeerConnected`
  补发、farmhand 端的完整异步流程、多个farmhand并发交割同一批合约的真实竞争，都还没有在真实多人
  会话里跑过。
- **Galaxy（Steam/GOG）连接的可靠性没有反编译到字节级证据**——只反编译确认了直连/局域网走
  `NetDeliveryMethod.ReliableOrdered`，Galaxy 路径只看到调用 Galaxy SDK 自身的发送接口，合理推断
  同样可靠，但不如 Lidgren 那一侧确凿。
- **分屏（local split-screen）场景没有专门测试**——`Context.IsMainPlayer`/`IsOnHostComputer` 的语义
  在分屏下有据可查（见 7.2 节），代码按这两个语义的正确区分实现，但没有实机验证过分屏具体表现。
- **多人预演栏的"可交割"数量是估算，不是精确值**（见 7.7 节），这是设计上接受的局限，不是 bug。
- **签约/交割等待期间收到别人操作的确认消息，可能弹出无关的反馈文字**（见 7.6 节），纯 UI 层面的
  巧合，不影响资金/合约正确性，没有专门修复。

### 7.12 最终代码结构一览

| 文件 | 本轮新增/主要改动内容 |
|---|---|
| `MultiplayerMessages.cs`（新建） | `MultiplayerMessageTypes` 常量 + 9 个消息 DTO（7.4 节列表）+ `ContractDueDateKind` 曾短暂放在这里，后移到 `DateHelper.cs` |
| `ContractManager.cs` | 构造函数新增 `IMultiplayerHelper`；签约拆成 `SignContractAsHost`/`RequestSignContract`/`ApplyContractSignedFromNetwork`；交割新增 `lockedContractIds`、`TryLockNextDeliverableContractAsHost`、`HandleDeliverRequestAsHost`、`RequestDeliver`、`ConfirmDeliver`、`ReportDeliverFailed`、`HandleDeliverConfirmedAsHost`、`HandleDeliverFailedAsHost`、`ApplyContractFulfilledFromNetwork`；全量同步新增 `BroadcastContractStateSync`、`SendFullStateSyncTo`、`ApplyContractStateSyncFromNetwork`；`SettleDefaults` 加锁清空 + 排除逻辑（7.9 节）；新增 `ContractSignedApplied`/`DeliverApprovedReceived`/`DeliverRejectedReceived` 三个事件 |
| `ModEntry.cs` | `OnSaving`/`OnSaveLoaded` 加 `IsOnHostComputer` 门槛；`OnDayEnding`/`OnDayStarted` 加 `IsMainPlayer` 门槛；新增 `OnModMessageReceived`（9 个消息类型的分发）、`OnPeerConnected`；移除了和本轮无关的 F5 调试热键 |
| `FuturesMenu.cs` | 签约/交割四个入口全部按 `Context.IsMainPlayer` 分支为"host 同步执行"或"farmhand 发请求等待"；新增 `isPendingSignRequest`/`isPendingDeliveryRequest`/`pendingDeliveryItemId`/`pendingDeliveryClickX/Y` 等待态字段；新增 `OnContractSignedApplied`/`OnDeliverApprovedReceived`/`OnDeliverRejectedReceived` 事件处理；构造函数/`cleanupBeforeExit` 里配套订阅/退订 |
| `DateHelper.cs` | 新增 `ContractDueDateKind` 枚举 + `ResolveDueDate`（host 用自己的 `SDate.Now()` 重算到期日，不信任线路上的计算结果） |
| `SettlementMath.cs` | 新增 `ComputeDeliveryPayout(agreedPrice, margin)`（7.10 节经济模型修正） |
| `MyFirstMod.Tests/*` | `SettlementMathTests.cs` 新增 `ComputeDeliveryPayout` 相关 3 个测试；测试总数 45 → 48；`ResolveDueDate` 相关的测试尝试后按用户要求撤销（会破坏测试项目"零游戏依赖"的可移植性），最终没有落地测试，只在 `ContractManager`/`FuturesMenu` 层面靠代码走查确认 |

多人相关的代码新增/改动全部依赖 `Game1.player`/`Context`/`IMultiplayerHelper`，和这个项目里所有涉及
真实游戏状态的代码一样，脱离真实游戏进程无法单元测试——这轮重构没有新增任何一个多人流程相关的单元
测试，全部通过代码走查 + 反编译佐证 + 用户手动单机验证完成，7.11 节列出的是仍然没有验证过的部分。

---

## 8. 待后续版本扩展的功能（本期不做）

- 提前平仓 / 卖出期货（预留：达到 `TotalContractsSigned` 阈值后解锁）
- 部分违约的精细化处理（当前为整单判定：要么交割，要么违约）
- 出货箱自动交割拦截
- K线图 / 更丰富的价格图表
- 多品种差异化交割节奏（当前统一走"周五"逻辑）
- 违约信用记录对后续保证金比例的影响
 
---
 
## 8.5 实现偏离记录：任务栏从 1:1 改为 N:1 分组
 
> 记录时间点：合约数据结构（第4步）实现完成后，"当日交割"栏（第5步）实现之前。
 
**偏离内容：**
本文档第 2.1 节原设计中，`FuturesContract.QuestId` 对应的是**一张合约 ↔ 一条独立任务**（1:1）。
实际实现中改为了 **N:1**：多张同品种、同交割日的合约，共享同一条按 `"{ItemId}.{DueDate}"` 分组的任务，
任务描述中的数量文字随签约笔数递增（x1 → x2 → x3），避免玩家看到大量重复的"啤酒期货"任务糊满任务栏。
 
**判断：这个偏离本身是合理的**，属于任务栏展示层面的优化，不影响合约表底层仍然是逐条独立记录（`ContractManager.Contracts`）。
 
**需要注意的连带影响（第5步"当日交割"实现时必须处理）：**
- 第 5.2 节 FIFO 预演算法本身**不受影响**——合约表底层还是独立记录，排序、逐条判断可交割/排队等待的逻辑照常按合约粒度进行。
- 但**交割或违约结算后，任务栏的分组任务需要同步更新**，具体规则：
  - 结算掉一条合约后，需要检查：**同一个分组任务下是否还有其他 `Status == Pending` 的合约**
    - 有 → 只更新任务描述里的数量文字（比如 x3 → x2），任务本身保留
    - 没有 → 才把整条分组任务从任务日志中移除
- 这部分逻辑在原设计文档第 5.3/5.4 节的伪代码中**没有覆盖**（因为原设计假设 1:1，"结算掉直接移除对应任务"即可），
  实现第5步时需要在 `OnDeliveryButtonClicked` 和 `OnDayEnding` 两处流程中都补上这段"分组任务数量同步"逻辑，
  避免出现"合约表里已经结算掉了，但任务栏描述数量没有对应减少"的不一致。
 
---
 
## 9. 开发顺序建议
 
1. 合约表 + 状态枚举（纯数据层，无 UI 依赖）
2. `DayEnding` 违约结算逻辑（可脱离 UI 独立测试）
3. 继承 `ShopMenu`，实现"买卖期货" Tab（签约流程）
4. 实现"当日交割" Tab（FIFO 预演算法 + 交互）
5. Quest 与信件系统接入
6. 折线图绘制（视觉打磨，可放在功能验证完成之后）
 
---
 
## 10. 实现状态快照 + 方法论备忘（持续更新）
 
> 本节记录截至目前的真实实现状态，与前面章节的"原始设计"存在若干经过讨论后确认的偏离。
> 每次有新的架构级决策，都应该在这里补记，而不是散落在对话历史里。
 
### 10.1 已验证落地的架构决策
 
**开发方式：不继承 `ShopMenu`，改为手写 `IClickableMenu`**
理由：`FuturesMenu` 的结构（Tab 切换、折线图、持仓列表）和原生 `ShopMenu`（商品网格+单一库存面板）差异太大，继承实际上要重写的方法（`draw`/`receiveLeftClick`/`performHoverAction`）和手写工作量相近，但手写不用先读懂父类内部状态，调试更可控。
 
**方法论：任何不确定的游戏原生 API，一律先反编译验证，不凭训练记忆猜测**
已经验证过的具体案例：
- `StardewModdingAPI.Utilities.SDate` 的星期映射（春季第1天=周一）
- `StardewValley.Quests.Quest` 的 `daysLeft`/`GetDaysLeft()`/`IsTimedQuest()` 字段和方法语义
- 罗宾木匠铺的分流触发路径（`GameLocation.performAction` → `GameLocation.carpenters` → `createQuestionDialogue`），发现和皮埃尔商店**不共用**触发路径
- 皮埃尔商店的真实触发路径（`Utility.TryOpenShopMenu` 静态方法，用 `shopId == "SeedShop"` 判断比按 NPC 名字判断更贴近引擎实际语义）
- `ShopMenu`/`InventoryMenu` 出售物品的点击处理逻辑（左键=整格全卖，右键=卖1个/Shift卖半堆，与买货时的 Shift=5个/Ctrl=25个 是完全不同的两套逻辑，容易搞混）
 
**接入皮埃尔商店的方式：Harmony Postfix patch `Utility.TryOpenShopMenu`**
- 判断条件用 `shopId == "SeedShop"`，不用 NPC 名字判断，因为该方法本身不感知点击的是哪个 NPC，只认 shopId，这样罗宾（`shopId == "Carpenter"`）天然不受影响
- 用 Postfix 而不是 Prefix：让原版的营业时间/店主在场等判断先跑完，只在 `__result == true` 时介入替换成选择对话，不用自己重新实现这些判断
- **已知待观察的风险点**：Postfix 介入前，原版 `ShopMenu` 实例其实已经被创建（`Game1.activeClickableMenu` 已赋值），如果玩家选"购买商品"走 `isReopeningVanillaShop` 标志重新打开真正商店，需要关注开店音效是否重复播放；`isReopeningVanillaShop` 必须用 `try/finally` 保护，否则一旦中途异常，标志卡死会导致皮埃尔商店永久失去选择对话入口
 
**任务栏关联：从设计文档原定的 1:1 改为 N:1 分组**（详见第 8.5 节，此处不重复）
 
### 10.2 交割交互设计：从"点击自绘列表行"改为"点击背包物品"
 
> 这是对第 4.2 节、第 5.2/5.3 节原始设计的**修正**，原因和具体方案如下。
 
**变更原因**：原设计里"当日交割"栏是自己画一行行合约列表、点击列表行触发交割。讨论后认为更好的方式是复用游戏原生"卖东西给商店"的交互——玩家直接点击自己背包里的物品来交割，这样可以复用 `InventoryMenu` 这个已经打磨过库存展示、格子命中检测的原生控件，不用自己重新发明一套点击响应逻辑。
 
**关键约束（反编译已确认，必须遵守）：**
- **不能直接调用 `InventoryMenu.leftClick`/`rightClick`**，因为这两个方法的语义是"摘除整格或半堆物品"，和"交割固定消耗 1 个"的需求不兼容。必须改用只读的定位方法（如 `getItemAt(x, y)`）拿到点击的是哪个格子/哪个 `ItemId`，扣减逻辑自己写，不借用原生的数量语义。
- 品质（普通/银/金/铱）不同的同名物品天然分处不同格子，`getItemAt` 命中哪一格就精确对应哪个品质，不需要额外处理。
- 结算相关的音效（`Game1.playSound("sell")`）和粒子特效（`TileSheets\debris`）是独立于 `ShopMenu` 业务逻辑的通用 API，可以直接复用以保持原版观感一致，不受上面"不能复用点击语义"这条限制的影响。
 
**修正后的交割流程（替代原 5.3 节伪代码）：**
```
函数 OnInventoryItemClicked(x, y):
    item = inventoryMenu.getItemAt(x, y)   // 只读定位，不摘除物品
    若 item 为空:
        return
 
    候选合约 = 查询(ItemId == item.ItemId && DueDate == 今日 && Status == Pending)
    按 SignedDate 升序排序(候选合约)
    合约 = 候选合约中第一个（FIFO 最早的一张）
 
    若 合约 为空:
        return   // 该品种今天没有对应到期合约，点击无效果（可加提示音）
 
    手动从 Game1.player.Items 中扣除该物品 1 个（不使用 leftClick/rightClick 的整堆/半堆逻辑）
    增加玩家金币(合约.AgreedPrice)
    合约.Status = Fulfilled
    同步更新任务栏分组任务（规则见第 8.5 节）
 
    可选：播放 "sell" 音效 + debris 粒子特效，保持原版观感
```
 
**未来到期合约的展示方式**："当日交割"相关 UI 区域应同时展示**所有** `Status == Pending` 的合约（不限当天），按 `DueDate` 升序、同日内再按 `SignedDate` 排序；非当天到期的行整行置灰、不可交互，仅作预告。`InventoryMenu` 的 `highlightMethod` 可用于让背包格子按"是否对应今天可交割的品种"来区分高亮/变暗状态。
 
### 10.3 待办事项优先级（本节内容会随进度更新）
 
1. ~~合约数据结构~~ ✅ 已完成
2. ~~签约流程 + 任务栏集成（含 N:1 分组）~~ ✅ 已完成
3. ~~接入皮埃尔商店入口（Harmony patch）~~ ✅ 已完成
4. ~~当日交割功能~~ ✅ 已完成，交互方案为"点击背包物品"（见 10.2）
5. ~~违约结算（`DayEnding`）~~ ✅ 已完成（`SettlementMath.cs`）；`GetMarketPrice` 已切换为读取
   `PriceModel` 模拟价格（见 6.6 节）
6. ~~存档持久化~~ ✅ 已完成（合约数据见 `FuturesContractSaveData.cs`；价格历史见 `MarketPriceSaveData.cs`）
7. 折线图 / Tab 视觉打磨 —— 未开始，明确推迟到功能验证完成之后
8. ~~市场定价模型接入~~ ✅ 已完成 —— `PriceModel.cs` 分层公式与每日价格历史记录已实现（见第 6 节），
   `GetMarketPrice` 已正式切换到模拟价格（见 6.6 节）
9. ~~多人同步架构（host 权威模型：签约/交割请求-审批、全量同步、新玩家加入补发）~~ ✅ 已完成
   （见第 7 节）；仍未做真机联机测试，已知限制见 7.11 节
 
 
