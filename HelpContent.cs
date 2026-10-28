using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MouseWheelEnhancement;

/// <summary>
/// 「帮助」页的全部文案与排版。
///
/// # 为什么用代码生成而不是写死在 XAML 里
///
/// 帮助页有二十多个参数、每个都要讲清「是什么 / 调大 / 调小 / 例子 / 建议」五件事，
/// 写进 XAML 会是六七百行几乎重复的标签，改一个措辞要在一堆 Border 里找位置。
/// 改成数据 + 统一渲染，文案就是一段一段可以整块替换的字符串。
///
/// # 文案口径
///
/// 面向"第一次打开这个软件、不懂 hook 也不懂 delta"的人：
///   ① 先说人话是什么，再给数字；
///   ② 一定写清"调大会怎样、调小会怎样"——用户调参时想知道的就这个；
///   ③ 每个参数尽量给一个能对上号的例子（几行代码、几下滚轮）；
///   ④ 最后给一句建议，让他不改也能用。
/// </summary>
internal static class HelpContent
{
    internal sealed record Param(
        string Title,
        string Default,
        string What,
        string? Bigger = null,
        string? Smaller = null,
        string? Example = null,
        string? Advice = null);

    internal sealed record Section(string Title, string Intro, IReadOnlyList<Param> Items);

    /* ══════════════ 文案 ══════════════ */

    private static IReadOnlyList<Section> Sections() => new List<Section>
    {
        new("加速",
            "这一组决定「什么时候开始加速」和「最多加速多少」。日常只需要关心前两项。",
            new List<Param>
            {
                new("触发阈值", "10 格/秒",
                    "程序从「完全不插手」变成「开始加速」的那条线。单位是每秒拨几格 —— 滚轮「咔」一下就是 1 格。",
                    "需要更用力、更快地拨才会触发，几乎不会被误伤。调到 18 以上基本只有刻意猛甩才加速。",
                    "轻轻一拨就加速。调到 6 以下时，正常看网页也可能一直被加速，会觉得「页面自己在滚」。",
                    "10 格/秒＝每 0.1 秒拨一格。慢慢找一段文字时大约每秒 3 到 5 格，不触发；想翻到页底时会不自觉连拨，大约每秒 15 到 25 格，立刻触发。",
                    "先保持 10。只有确实被误触发困扰，才往上调。"),

                new("满速点", "40 格/秒",
                    "倍率「拉满」的位置。从触发阈值到满速点之间，倍率是一格一格往上爬的，不会一下跳到最高。",
                    "要滚得更快才能吃满倍率，加速更「温柔」，适合慢节奏的人。",
                    "更快吃满，加速来得更猛；但如果和触发阈值贴得太近，会变成「一过线就窜出去」。",
                    "阈值 10、满速 40 时，你以每秒 25 格滚动（正好在中间），倍率大约是最高倍率的一半；每秒 40 格以上才是满倍率。",
                    "保持 40。觉得不够猛请调「加速倍率上限」，不要动这里 —— 动这里改的是加速的「渐进程度」，不是最终能有多快。"),

                new("加速倍率上限", "3×",
                    "最多把一格放大成几格。3× 表示滚轮拨一格，页面滚三格的距离。整数 1 到 8。",
                    "翻得更远。5× 以上时一格能滚半屏，很容易一甩就过头、找不到刚才的位置。",
                    "更接近原生手感。1× 等于不加速；2× 只在你猛甩时才明显。",
                    "一份 1000 行的代码，不加速滚到底大约要拨 30 下；3× 之后约 10 下；5× 之后约 6 下。",
                    "3 是大多数人的甜点。读长文档多就 4；只在浏览器里想快一点就 2。"),

                new("反向滚动时立刻纠正", "开",
                    "平滑会把一次滚动拆成好几个小步慢慢发。如果你突然往回滚，是「先把之前的发完」还是「立刻掉头」，由这个开关决定。",
                    null, null,
                    "向下猛甩后马上向上滚一格：开启时页面立刻停住并回滚；关闭时还会先往下冲一小段，感觉像「想往回滚却滚不动」。",
                    "保持开启。只有在极少数滚轮上觉得「回滚时页面跳一下」才尝试关闭。"),

                new("反转加速方向", "关",
                    "只给在 Windows 鼠标设置里勾过「滚轮反向 / 自然滚动」的鼠标用。程序自己注入的滚动不会自动跟随系统那个设置，所以要在这里对齐。",
                    null, null,
                    "普通鼠标打开它 → 你往下滚，页面却往上走。",
                    "保持关闭。除非你确认自己在系统设置里做过反向。"),

                new("也增强高精度输入", "关",
                    "触控板两指滚动、部分鼠标的「自由滚轮 / 无级」档，每次只发很小的滚动量（不到一格），而且发得非常密集。这个开关决定要不要连它们一起加速。",
                    null, null,
                    "这类设备每秒能发几十次小滚动，按「每秒格数」算会被判成狂甩，一加速就直接飞到页面底部。所以默认一律放行。",
                    "保持关闭。笔记本两指滚动在默认设置下完全不受影响，手感和系统一致。"),
            }),

        new("平滑滚动",
            "这一组决定「滚动过程顺不顺」：不只是加速时，普通速度拨一格也同样经过它。全部保持默认就已经很接近触控板；只有在觉得「飘」「粘」「卡顿」时才需要动。",
            new List<Param>
            {
                new("施加范围", "所有滚动",
                    "哪些滚动要经过平滑：只有快滚，还是每一次滚动都算。",
                    "选「所有滚动」（默认）：就算你慢慢拨一格，页面也是分成几小步滑上去，起停都有过程，观感统一；代价是普通滚动不再是系统原生的那一跳。",
                    "选「仅加速时」：普通滚动完全不经过本程序，和你没装它时一模一样；只有快滚放大之后才摊帧。",
                    "选「所有滚动」后，慢慢滚一格也能看出惯性；选「仅加速时」，慢慢滚一格就是系统原本的一格 —— 平滑对它不起作用。",
                    "保持「所有滚动」。这是「平滑滚动真的在平滑每一次滚动」的保证；哪天觉得普通滚动的手感被改了，再切回「仅加速时」。" ),

                new("注入帧率", "自动跟随显示器",
                    "每秒把滚动拆成多少个小步。默认不是你选，而是程序直接问系统「当前这块屏幕现在多少 Hz」，" +
                    "然后按那个节奏出帧（165Hz 就是每 6.06 毫秒一小步）。",
                    "只有在你手动关掉自动、改成一个固定值时才会用到：填得比屏幕高，多出来的那几步会在同一帧里被合并掉，白算还费电。",
                    "填得比屏幕低，一步会跨过好几个刷新周期才画出来，看起来就是一顿一顿的。",
                    "屏幕 60Hz → 每 16.67ms 一步；120Hz → 8.33ms；144Hz → 6.94ms；160Hz → 6.25ms；165Hz → 6.06ms；200Hz → 5.00ms；240Hz → 4.17ms。",
                    "保持「自动跟随显示器刷新率」开启。换显示器、改分辨率、笔记本插拔电源导致刷新率变化时，程序会在 2 秒内自动跟上，不用重启。"),

                new("每格拆分", "4",
                    "一格（滚轮「咔」一声，等于 120 个单位）拆成几个小步发送。选 4 就是每小步 30 个单位，四步正好一格。",
                    "更细腻，但单步太小，极少数老程序会直接忽略这一小步（表现是「滚不动」）。",
                    "更接近原生，平滑感变弱。选 2 是折中。",
                    "选 4 时，一格滚动会分成 30 / 30 / 30 / 30 四次发出，所以画面是推进而不是跳动。",
                    "保持 4。真遇到某个程序滚不动，先把它调到 2，还不行就把平滑整个关掉。"),

                new("惯性长度", "45 ms",
                    "停手之后「尾巴」有多长。数学上是个指数衰减的时间常数，你只要理解成「还会滑多久」。",
                    "80 以上：停手后页面还会滑一小段，像触控板；太大就觉得「刹不住」。",
                    "20 以下：一停就停，干脆利落，接近原生。",
                    "45 毫秒大约意味着：你松手后 45 毫秒内把剩下的量匀速发完，肉眼看到的是「轻轻收尾」而不是「突然刹住」。" +
                    "注意：这一项如果调到很低（比如 5~15），一次滚动会在两三帧内就发完，那「松手后的惯性」也就没量可滑了 —— 两项要配合着用。",
                    "30 到 60 之间最像触控板；配合下面「松手后的惯性 300 ms」就是完整的推送手感。"),

                new("松手后的惯性", "300 ms",
                    "滚轮完全停下之后，内容还会顺着原方向滑一小段再慢慢停住 —— 这一项控制那段「滑行」有多长（是一个**时长**，不是时间常数）。" +
                    "和上面的「惯性长度」不是一回事：那个管的是「一次滚动内部怎么摊开」，这个管的是「最后怎么收尾」。",
                    "尾巴更长、更像触控板推一把的感觉。400 ms 以上会明显发飘，尤其快速连甩之后内容还在往下走。",
                    "更干脆。调到 0 就是关闭：松手立刻停住 —— 这是老版本的手感，滚的时候顺，一停就「咔」一下。",
                    "300 毫秒大约是「松手之后还滑小半格」。这里不会「到点把剩下的量一次倒出去」——" +
                    "那样会在结尾突然跳一下；程序是按当时的积压反解出一条衰减曲线，让最后一点量正好在这一刻走完，所以停住是滑进去的。" +
                    "另外：剩下不到 1/3 格时，收尾总会按 70 ms 的时间常数落地（就算这一项设得很短也会），" +
                    "所以把它调小只会让前面更快，不会变成硬停 —— 要硬停请设 0。",
                    "先用 300。觉得「停得太拖」就降到 150；觉得「还是有点段落感」就加到 400。"),

                new("积压上限", "5 格",
                    "连续猛甩时，允许暂存还没发完的量。攒超了这个数，超出的部分直接丢掉。",
                    "更顺滑，但你停手后页面可能还往前飘一小段。",
                    "更跟手，停手即停；但太小的话，快速连甩会显得一顿一顿。",
                    "你连甩 8 格、每格 5 倍，理论上要发 40 格；超过上限的部分会被丢掉，而不是攒着慢慢播 —— " +
                    "宁可少滚一点，也不要「停手之后还自己往下跑」或者「一下子跳过好几格」。",
                    "保持 5。"),
            }),

        new("应用名单",
            "决定这个增强在哪些软件里生效。改动立刻生效，不用重启。",
            new List<Param>
            {
                new("名单模式", "黑名单",
                    "先说清名单是「排除用」还是「只允许用」。",
                    "白名单：只有名单里的程序加速，其他全部不加速。适合「我平时都别动，只在浏览器里用」。",
                    "黑名单：名单里的程序不加速，其他全部加速。适合「个别软件别烦我」。",
                    "只想在 Chrome 里用 → 选白名单，加 chrome.exe；只想让游戏里别乱滚 → 选黑名单，加游戏 exe。",
                    "保持黑名单。默认已经排掉远程桌面和几款对时序敏感的竞技游戏。"),

                new("按什么匹配", "exe 文件名",
                    "按程序的文件名匹配（不区分大小写）。粘贴完整路径也行，程序会自动只取文件名、补上 .exe。",
                    null, null,
                    "填 C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe 和直接填 chrome.exe 是同一个效果。",
                    "不确定 exe 叫什么，就点「从运行中的应用选择」，里面列出的是当前开着窗口的程序。托盘右键也能一键把「当前应用」加入或移出名单。"),

                new("窗口标题正则", "空",
                    "按窗口标题额外判定，不区分大小写。命中后同样按上面的名单模式处理。留空即关闭。",
                    null, null,
                    "填 remote|远程，所有标题里含「远程」的窗口都会被排除（黑名单模式下＝不加速）。适合远程桌面客户端换了 exe 名的情况。",
                    "不会写正则就别填 —— 写错了会立刻标红提示并且不生效，不会影响其他设置。"),
            }),

        new("更新与令牌",
            "更新走 GitHub Releases：程序内下载、校验、替换，不需要跳浏览器。",
            new List<Param>
            {
                new("启动时自动检查更新", "开",
                    "每次启动在后台静默查一次最新版本。",
                    null, null,
                    "已是最新版时什么都不弹；发现有新版才提示，并可一键在程序内下载安装。",
                    "保持开启。"),

                new("GitHub 个人访问令牌", "空（选填）",
                    "一串只有你自己能看到的凭据，用来让程序以「你」的身份访问 GitHub 接口。",
                    null, null,
                    "不填也能查更新和下载，但匿名接口每小时只有 60 次额度，用多了会被限流（提示「接口限流了，约 N 分钟后恢复」）；填了是每小时 5000 次。",
                    "公开仓库用 classic 令牌、一个 scope 都不用勾就能读；fine-grained 令牌只需给该仓库 Contents 只读权限。令牌只以 DPAPI 加密保存在本机配置文件里，别的账户、别的电脑都解不开，不上传、不写日志。"),

                new("填错令牌会怎样", "—",
                    "程序不会因此罢工：GitHub 拒绝令牌时会自动降级成匿名请求再查一遍，并在界面上告诉你「令牌无效，已改用匿名」。",
                    null, null,
                    "所以你可以先不填，遇到限流提示了再来填；填错了也只是退回匿名，不会比不填更差。",
                    "点「验证」可以当场确认令牌有效，并看到本小时还剩多少次额度。"),
            }),

        new("界面上的其他开关",
            "",
            new List<Param>
            {
                new("开机自启", "关",
                    "登录 Windows 后自动启动，直接待在托盘里，不弹窗。",
                    null, null,
                    "用的是当前用户注册表里的启动项，不需要管理员权限，也不会在系统里装服务。",
                    "常用就打开。"),

                new("以管理员身份启动", "关",
                    "开机自启时直接从管理员权限启动，这样连「管理员程序的窗口」（任务管理器、部分安装程序、以管理员运行的游戏启动器）也能加速。",
                    null, null,
                    "它靠一个 Windows 计划任务实现：在「任务计划程序」里能看到名为「鼠标滚轮增强（管理员）」的任务。" +
                    "不用注册表启动项，是因为注册表里的程序一律以普通权限启动，没有例外。" +
                    "第一次打开这个开关会弹一次 UAC，之后就再也不会弹了 —— 提权授权在创建任务时就完成了。" +
                    "关闭开关时程序会把这个任务删掉。",
                    "只有当你经常在管理员窗口里滚动时才需要打开。普通浏览器、Office、记事本都不需要它。"),

                new("关闭窗口时最小化到托盘", "开",
                    "点右上角 × 时是「退到托盘继续工作」还是「真的退出」。",
                    null, null,
                    "退到托盘后滚轮增强仍在生效，第一次关闭时会提示一次，免得你以为程序已经退出、却在滚轮上感觉到它还在。",
                    "保持开启。想彻底停掉就用托盘菜单里的「退出」。"),

                new("在本窗口暂停增强", "关",
                    "让「鼠标滚轮增强」对自己这个设置窗口也不生效，滚动完全交回系统。",
                    "打开它，本窗口的滚动就变回完全原生 —— 用来做 A/B 对比：先在窗口里滚一下感受增强后的手感，打开这个开关再滚一下，立刻能听出差别。",
                    "关掉它，本窗口的滚动同样会被增强。",
                    "程序不会因为「怕自己被自己加速」而默认排除自己。普通速度滚动时它不放大倍率（默认只会做平滑），快到阈值才额外加速 —— 在自己窗口里滚也一样，不会出现越滚越快这种失控。",
                    "保持关闭。要试对比效果时临时打开，试完再关掉。"),
            }),
    };

    private static IReadOnlyList<(string Q, string A)> Faqs() => new List<(string, string)>
    {
        ("装上之后一点感觉都没有？",
            "先看「概览」页第一项「钩子」是不是「运行中」，再看「当前目标应用」是不是在黑名单里。如果一切正常，那是你的滚动速度还没超过触发阈值 —— 试着连续快拨几下，或者把触发阈值暂时调到 6 试试。"),

        ("在某个窗口里完全没效果？",
            "那个窗口很可能以管理员身份运行（任务管理器、安装程序、部分游戏启动器）。Windows 不允许普通权限的程序拦截更高权限窗口的输入，这是系统限制。到「概览」页点「以管理员身份重启」即可覆盖这类窗口；想一劳永逸就打开设置里的「以管理员身份启动」。"),

        ("开了「以管理员身份启动」，每次开机还会弹 UAC 吗？",
            "不会。这个开关是用 Windows 计划任务实现的：你第一次打开时点的那次 UAC，就是把「以高权限运行」这件事一次性授权给任务计划程序；之后每次登录它都以管理员启动，不再询问。如果哪次开机发现它没起来，可以在「任务计划程序」里找名为「鼠标滚轮增强（管理员）」的任务看状态，或者用命令行 MouseWheelEnhancement.exe --taskcheck 打印当前状态。"),

        ("平滑滚动开了，但滚轮一停就「咔」一下停住？",
            "那是「松手后的惯性」太短或为 0。到「平滑滚动」页把它调到 200~300 ms 试一次：滚轮停下后内容会多滑一小段再收住，收尾就没有段落感了。注意它和「惯性长度」是两项，前者管收尾、后者管一次滚动内部怎么摊开。"),

        ("滚到最后一段会「啪」一下停住，前面都很顺？",
            "那是旧版收尾算法的缺陷，1.7.1 已修：最后几个单位（每个约 0.8 像素）过去被按「每帧至少发 1 单位」处理，速度不再衰减，" +
            "而是一直以每秒一格左右走到量发完，然后突然归零 —— 刷新率越高越明显（160Hz 上相当于 130 px/s 直接刹车）。\n" +
            "现在改成「零头跨帧累积 + 落地段」：不足 1 单位的小数跨帧攒着，攒够才发，" +
            "并且松手后剩下不到 1/3 格时会把时间常数拉长到 70 ms，末段速度降到 0.3 格/秒左右再停。\n" +
            "如果还是觉得收尾太拖，把「松手后的惯性」调小；想要干脆的硬停，把它调成 0。"),

        ("平滑滚动开了，但一格一格拨还是段落式的起停？",
            "先看「平滑滚动 → 施加范围」是不是「仅加速时」—— 那一项下普通滚动完全不经过本程序，慢慢拨一格就是系统原生的一跳，平滑对它不起作用，默认应该是「所有滚动」。\n" +
            "范围确认没问题后，再看「每格拆分」：调大（6 或 8）会让单格拆得更细。\n" +
            "最后是收尾：「松手后的惯性」调到 200 ms 以上，停下来才是滑到尾而不是咔一下停住。三项都到位，单格滚动也该有完整的平滑过程。"),

        ("在 Windows 里改了「每次滚动几行」，软件里没跟着变？",
            "两边本来就是同一个值：软件里那一项滑块是直接写系统设置的，反过来在系统面板改完、切回本窗口时会重新读一次（窗口重新获得焦点就同步，不用重启）。" +
            "软件不另存一份「自己认的行数」，所以不会出现两边各说一套的情况；如果系统被设成「一次滚一页」，滑块会禁用并写明原因。"),

        ("整个系统都变得特别慢，一格只走一点点？",
            "先看系统设置 → 蓝牙和其他设备 → 鼠标 →「每次滚动以下行数」是不是被改成了 1。旧版本（1.6.x）为了让某些程序把一格拆得更细，曾把这一项临时降到 1 行，异常退出时可能留在那里。" +
            "现在启动时会按旧版留下的备份自动还原；也可以命令行运行 MouseWheelEnhancement.exe --wheellines 看现状，加 --fix 按备份还原。"),

        ("在本程序的设置页里拨一格，内容直接飞过一页？",
            "这一项以前在「每格滚动格数」调小的时候特别明显（调到 1 时最严重），1.7.2 起已修好。\n" +
            "当时的缘由：本程序的页面是「按像素滚」的（一格 = 行数 × 20 像素），而摊帧会把一格拆成 8 小步，最后几步只有 0.2~0.5 像素，被误判成「已经滚到边界了」而交给系统处理，系统那一跳就是一整行 —— 于是我们发 20 像素、它跳 48 像素，一格顶到一页多。\n" +
            "现在边界只按「真的到顶/到底了」判，小数位移照常自己滚。想确认本机现状：命令行运行 MouseWheelEnhancement.exe --wheeltest lines=1，报告里「单格最大位移」应该正好是 20 像素。"),

        ("界面能跟系统的深色/浅色自动切换吗？",
            "能，而且默认就是这么做的。到「概览 → 外观」确认选的是「跟随系统」；之后在 Windows「设置 → 个性化 → 颜色 → 选择默认应用模式」里切换，本程序的窗口会在 1~2 秒内跟着变，不用重启。标题栏也会一起变。"),

        ("为什么在自己的设置窗口里滚，感觉没有增强？",
            "先看「概览 → 在本窗口暂停增强」是不是被打开了 —— 开着的话本窗口就是完全原生的。默认是关的（也就是对自己也增强）。另外要分清两件事：只有快速拨动才会**加速**；慢慢滚不会变大，但默认会被**平滑**（拆成几小步再滑出去），这不是故障。"),

        ("游戏里没用？",
            "用 Raw Input / DirectInput 独占总线的游戏绕过了系统鼠标钩子，所有同类工具都拿不到事件，属于技术边界。默认黑名单也已经把常见竞技游戏排除了。"),

        ("我的屏幕是 165Hz / 200Hz，需要自己设帧率吗？",
            "不需要。默认就是「自动跟随显示器刷新率」：程序读的是当前这块屏幕的实际 Hz，按它的节奏出帧，换屏幕或改分辨率后会在 2 秒内自动跟上。手动那个 60/90/120/144 只是给特殊情况留的覆盖项（比如笔记本想省电）。"),

        ("笔记本触控板没有任何变化？",
            "这是设计如此。触控板每次只发很小的滚动量、频率又极高，加速后会直接失去控制。见「加速」页最后一项「也增强高精度输入」，默认关闭。"),

        ("按住 Ctrl 滚轮缩放，会不会被加速？",
            "不会。Ctrl 或 Alt 加滚轮（缩放）一律原样放行，因为缩放必须 1:1 才有意义。Shift 加滚轮（横向滚动）同样放行。"),

        ("滚着滚着突然就不加速了？",
            "Windows 在钩子回调超时时会静默摘掉钩子，而且不通知任何人。到「关于」页点「重启钩子」即可恢复，日志里会留下记录。"),

        ("加速后有点「刹不住」，一甩就过头？",
            "按这个顺序调：先把「加速倍率上限」降一格，再把「积压上限」调小，最后把「惯性长度」调到 30 左右。三个一起调容易分不清是哪一个起了作用。"),

        ("某个程序里滚不动 / 滚得很奇怪？",
            "把「每格拆分」从 4 改成 2，它会回到更接近原生的粒度；还不行就关掉平滑，只保留加速。极老的 Win32 程序把每次滚动量按 120 整除取整，收到小步就会丢弃。"),

        ("设置到底存在哪？",
            "就放在软件自己的文件夹里：<程序目录>\\Mouse wheel enhancement data\\config.json，日志在同目录的 logs\\app.log。「关于」页有两个「打开」按钮可以直接过去。"),

        ("能把这套设置带到另一台电脑吗？",
            "可以复制整个 config.json，唯一的例外是 GitHub 令牌 —— 它用当前 Windows 账户的密钥加密，换台机器解不开（程序会当作没填，其它设置照常生效），到新机器上重新填一次即可。"),
    };

    /// <summary>几套现成的参数组合：大多数人其实只是想要一个「方案」，不想逐个调。</summary>
    private static IReadOnlyList<(string Name, string Desc)> Presets() => new List<(string, string)>
    {
        ("看长文档 / 代码为主", "倍率 4×　触发阈值 10　满速点 40　平滑：所有滚动　惯性 45 ms"),
        ("日常网页浏览", "倍率 3×　触发阈值 12　满速点 40　平滑：所有滚动　惯性 45 ms"),
        ("最保守（怕误触）", "倍率 2×　触发阈值 16　满速点 45　平滑：关"),
        ("想更「滑」一点", "倍率 3×　触发阈值 10　满速点 35　平滑：所有滚动　惯性 60 ms"),
    };

    /* ══════════════ 渲染 ══════════════ */

    public static void Render(StackPanel host)
    {
        host.Children.Clear();

        host.Children.Add(Card(Intro()));

        foreach (var section in Sections())
        {
            var panel = new StackPanel();
            panel.Children.Add(Text(section.Title, 14.5, FontWeights.SemiBold, "TextPrimary", top: 0));
            if (!string.IsNullOrWhiteSpace(section.Intro))
                panel.Children.Add(Text(section.Intro, 12, FontWeights.Normal, "TextSecondary",
                    top: 6, bottom: 12, wrap: true));

            foreach (var p in section.Items) panel.Children.Add(ParamBlock(p));
            host.Children.Add(Card(panel));
        }

        host.Children.Add(Card(BuildFaq()));
        host.Children.Add(Card(BuildPresets()));
    }

    private static FrameworkElement Intro()
    {
        var p = new StackPanel();
        p.Children.Add(Text("这个软件是干什么的", 14.5, FontWeights.SemiBold, "TextPrimary", top: 0));

        p.Children.Add(Text(
            "只解决一个具体问题：滚轮在「需要翻很远」的时候太慢。",
            13, FontWeights.Normal, "TextPrimary", top: 10, wrap: true));

        p.Children.Add(Text(
            "它不改鼠标速度、不装驱动。唯一会碰的系统设置是「每次滚动几行」——那项只在你在设置页拖它时才会写，软件启动和退出都不碰。" +
            "你快速连续拨动滚轮的那一瞬间，它把这一段滚动的距离按倍率放大；速度回到正常以后，放大立刻消失，只留下平滑的收尾。",
            12, FontWeights.Normal, "TextSecondary", top: 8, wrap: true));

        p.Children.Add(Text("三条核心规则", 13, FontWeights.SemiBold, "AccentHover", top: 16, bottom: 8));

        void Rule(string n, string head, string body)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var num = Text(n, 12, FontWeights.SemiBold, "AccentHover");
            num.VerticalAlignment = VerticalAlignment.Top;
            g.Children.Add(num);

            var stack = new StackPanel();
            stack.Children.Add(Text(head, 12.5, FontWeights.SemiBold, "TextPrimary", wrap: true));
            stack.Children.Add(Text(body, 12, FontWeights.Normal, "TextSecondary", top: 2, wrap: true));
            Grid.SetColumn(stack, 1);
            g.Children.Add(stack);

            p.Children.Add(g);
        }

        Rule("1", "慢速滚动不放大，但默认会平滑",
            "普通速度拨一格，倍率始终是 1×，不会变大；默认「所有滚动」下这一格还会被拆成几小步滑上去，起停都有过程。想要彻底原生的那一跳，把「施加范围」改成「仅加速时」，或关掉平滑滚动。");
        Rule("2", "快滚才加速",
            "判定依据是「每秒滚几格」，不是「第几格」。所以逐格微调不会被误伤，连拨翻页也不会误判。");
        Rule("3", "随时可退出链路",
            "总开关、名单命中、按着 Ctrl / Alt / Shift、或者反向滚动纠正时，都立刻回到原生行为。");

        return p;
    }

    private static FrameworkElement ParamBlock(Param p)
    {
        var stack = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        head.Children.Add(Text(p.Title, 13, FontWeights.SemiBold, "TextPrimary", wrap: true));

        var badge = new Border
        {
            Background = R("AccentFaint"),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Child = Text($"默认 {p.Default}", 10.5, FontWeights.SemiBold, "AccentHover"),
        };
        Grid.SetColumn(badge, 1);
        head.Children.Add(badge);

        stack.Children.Add(head);

        void Row(string label, string? body, Brush labelBrush)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            var g = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var l = Text(label, 11.5, FontWeights.SemiBold, labelBrush);
            l.VerticalAlignment = VerticalAlignment.Top;
            g.Children.Add(l);

            var v = Text(body!, 12.5, FontWeights.Normal, "TextPrimary", wrap: true);
            Grid.SetColumn(v, 1);
            g.Children.Add(v);

            stack.Children.Add(g);
        }

        Row("是什么", p.What, R("TextMuted"));
        Row("调大", p.Bigger, R("WarnBrush"));
        Row("调小", p.Smaller, R("AccentHover"));
        Row("例子", p.Example, R("SuccessBrush"));
        Row("建议", p.Advice, R("TextMuted"));

        return new Border
        {
            Background = R("CardBg2"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
    }

    private static FrameworkElement BuildFaq()
    {
        var p = new StackPanel();
        p.Children.Add(Text("常见问题", 14.5, FontWeights.SemiBold, "TextPrimary", top: 0, bottom: 12));

        foreach (var (q, a) in Faqs())
        {
            var qb = Text("Q　" + q, 12.5, FontWeights.SemiBold, "AccentHover", wrap: true);
            qb.Margin = new Thickness(0, 0, 0, 3);
            p.Children.Add(qb);

            var ab = Text("A　" + a, 12, FontWeights.Normal, "TextSecondary", wrap: true);
            ab.Margin = new Thickness(0, 0, 0, 14);
            p.Children.Add(ab);
        }

        return p;
    }

    private static FrameworkElement BuildPresets()
    {
        var p = new StackPanel();
        p.Children.Add(Text("懒得逐个调？直接用这几套", 14.5, FontWeights.SemiBold, "TextPrimary", top: 0));
        p.Children.Add(Text("大多数人其实只是想要一个「方案」。照着一行改完就行，不用理解每一项。",
            12, FontWeights.Normal, "TextSecondary", top: 6, bottom: 12, wrap: true));

        foreach (var (name, desc) in Presets())
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var n = Text(name, 12, FontWeights.SemiBold, "TextPrimary", wrap: true);
            n.VerticalAlignment = VerticalAlignment.Top;
            g.Children.Add(n);

            var d = Text(desc, 12, FontWeights.Normal, "TextSecondary", wrap: true);
            Grid.SetColumn(d, 1);
            g.Children.Add(d);

            p.Children.Add(g);
        }

        return p;
    }

    /* ══════════════ 小工具 ══════════════ */

    private static Border Card(FrameworkElement child) => new()
    {
        Background = R("CardBg"),
        BorderBrush = R("BorderBrushSoft"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(18, 16, 18, 16),
        Margin = new Thickness(0, 0, 0, 12),
        Child = child,
    };

    private static TextBlock Text(string text, double size, FontWeight weight, string brushKey,
        double top = 0, double bottom = 0, bool wrap = false)
        => Text(text, size, weight, R(brushKey), top, bottom, wrap);

    private static TextBlock Text(string text, double size, FontWeight weight, Brush brush,
        double top = 0, double bottom = 0, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            Margin = new Thickness(0, top, 0, bottom),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            LineHeight = size * 1.62,
        };
        tb.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Ideal);
        return tb;
    }

    private static Brush R(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
