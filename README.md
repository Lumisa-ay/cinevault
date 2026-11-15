# 映库 CineVault

影视资源工作台。使用 C# / ASP.NET Core MVC 提供 TMDB 电影同步、多来源检索、磁力资源匹配、qBittorrent 下载与 HTTP 下载；同一套界面也可作为 Sites 静态交互演示。

## 本地 Docker 部署与内置账户

### 最新电影同步与 4K 中字磁力下载

TMDB 配置成功后，进入「多来源检索」会首次自动同步最新电影；资源库为空时，首页也会加载这一页。点击「同步最新电影」可重新查询。电影按首映日期倒序显示，排除未来首映日期；日期上限按北京时间计算。数据通过官方 Discover API 获取，合并到现有持久资源库，不清除已有任务和资源。最低评分可设为 0 查看未评分的新电影。连接测试现在同时检查认证配置和实际电影查询。

磁力检索使用 **Prowlarr 已配置并启用的网上资源索引**，下载使用 **qBittorrent Web UI API**。系统没有内置一个可保证找到所有电影的磁力站点；需先在 Prowlarr 添加可用索引。仅提供片名资料的 TMDB 不能代替资源索引。

如果还没有这两个服务，可在项目根目录运行以下脚本，构建本系统并启动配套服务：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-local.ps1 -EnableBt
```

以上命令从包含 `compose.yaml` 的项目目录运行；从外层「资源下载」工作区运行时，脚本路径为 `.\sites\cinevault\scripts\update-local.ps1`。已有 Prowlarr / qBittorrent 可直接接入，使用不带 `-EnableBt` 的更新命令即可。配套服务的 Web UI 仅绑定本机地址，BT 不额外发布入站宿主机端口，默认使用出站连接发现和连接可用节点。

| 服务 | 浏览器访问地址 | 映库中的服务地址 |
| --- | --- | --- |
| 映库 | `http://localhost:5188` | — |
| Prowlarr | `http://localhost:9696` | `http://prowlarr:9696/` |
| qBittorrent | `http://localhost:8081` | `http://qbittorrent:8080/` |

首次启动后，在 Prowlarr 配置索引，从“设置 → 常规”复制其 API Key；在 qBittorrent 设置 Web UI 用户名和密码。LinuxServer 镜像的 qBittorrent 首次临时密码可从其容器日志查看，随后在 Web UI 修改。将这些信息填入映库的「来源与密钥 → 磁力资源索引与 BT 下载器」，保存后分别测试。配套服务的下载器保存根目录填写 `/downloads`，和本系统使用同一个宿主机下载目录。接入宿主机上的服务时，容器通常通过 `host.docker.internal` 加实际端口访问；不要填写容器自身的 `localhost`。

对单部电影点击「搜索 4K 中字磁力」，候选按片名/年份匹配、4K、中字和做种数排序，再选择加入队列。资源名称中的 `4K` / `2160p` 用于识别画质，`CHS` / `CHT` / 中字 / 简中 / 繁中用于识别字幕；国语或 Mandarin 音轨不会当作中文字幕。界面会标明名称标签未经实际文件验证。索引仅返回 `.torrent` 下载地址而没有磁力或 BTIH 时，不会猜测资源哈希。

「同步并下载 4K 中字（最多 3 部）」每次检查本页前 3 部电影。只有**片名和年份匹配、4K、名称标注中文字幕且有做种者**的候选才自动入队；没有满足条件的资源会跳过，并逐部显示原因。其他画质、字幕未知或片名/年份不匹配的资源不会自动降级下载，仍可由用户手工核对候选。

BT 和 HTTP 任务使用同一持久化任务列表和并发上限，BT 状态与进度取自下载器。支持暂停、继续、取消与手动重试；提交网络失败按原有重试设置退避重试。取消 BT 任务保留下载文件，清理历史不会删除文件。下载完成后，qBittorrent 仍可能按其自身设置继续做种。若下载器已有相同资源但位于不同目录，系统会提示核对目录，不会把它误报为已保存到新位置。

新增接口：`POST /api/media/{id}/magnets`、`POST /api/sync/latest/download`；`POST /api/search` 可传 `source=tmdb, type=movie, mode=latest`。资料同步不自动下载；明确的批量下载按钮或选择资源入队才会开始 BT 下载。资源索引 Key 和下载器密码沿用加密配置与管理员访问控制。

配套镜像与 API 参考：[LinuxServer qBittorrent](https://docs.linuxserver.io/images/docker-qbittorrent/)、[LinuxServer Prowlarr](https://docs.linuxserver.io/images/docker-prowlarr/)、[qBittorrent Web UI API](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29)、[TMDB Discover](https://developer.themoviedb.org/reference/discover-movie)。

### 网页配置 TMDB / IMDb 密钥

管理员登录后，进入左侧「来源与密钥」（`/admin/sources`）。只有管理员能打开该页面、修改凭据或测试连接，普通用户只会看到来源是否可用。

- **TMDB**：选择 API Key（v3）或 API Read Access Token（Bearer），填写凭据并保存，再点击「测试已保存的 TMDB 连接」。检索请求立即使用新配置，不需要修改 `.env` 或重启容器。
- **IMDb 官方 API**：选择 AWS Data Exchange 模式，填写 IMDb API Key、AWS Access Key ID / Secret Access Key，以及 Data Set ID / Revision ID / Asset ID；临时 AWS 凭据还需 Session Token。当前适配使用固定的官方 us-east-1 入口和 SigV4 签名，按 `tt0133093` 这样的编号查询片名与评分；年份和类别保持未知，查询时使用「全部」类别。
- **OMDb API**：如果密钥由 `omdbapi.com` 签发，选择 OMDb 第三方模式；它支持按英文片名或 IMDb 编号查询，并标明 IMDb 评分由 OMDb 提供。每页最多 10 项，查询评分需要补查详情并使用接口额度。它与 IMDb 官方 API 是不同的服务。
- **IMDb 本地数据集**：保留原有无密钥方式，切回该模式即可继续使用 `/data/imdb` 中的数据文件。

密钥字段始终不回填原值。留空保留原值，输入新值替换；勾选清除可删除保存的凭据。更换 TMDB 凭据类型时需填写对应的新值。清除 TMDB Key 后不会恢复环境变量中的旧 Key。切换 IMDb 模式会保留其他模式已保存的凭据，除非勾选清除全部在线凭据；从临时 AWS 凭据改为长期凭据时，可以单独清除 Session Token。

来源配置使用 ASP.NET Core Data Protection 加密后写入 `/data/source-settings.protected`，跟随现有 Docker 数据卷持久化；备份与迁移时需同时保留 `/data/keys`。未设置网页配置时兼容原来的 `CINEVAULT_TMDB_API_KEY` 环境变量。网页配置优先。接口响应、配置页和错误提示不返回密钥；保存的密钥不会写入源码包。

“已配置”表示凭据已保存，不代表联网验证通过。连接测试只使用已保存的配置，测试失败不会删除密钥。IMDb API 测试会发送一次资料查询；本地数据集测试仅检查两个文件是否存在。

参考：[TMDB 认证](https://developer.themoviedb.org/docs/authentication-application)、[IMDb 官方 API 调用](https://data.imdb.com/documentation/api-documentation/calling-the-api/)、[OMDb 参数](https://www.omdbapi.com/)。

### 多来源检索（2026-11-15）

左侧「多来源检索」沿用“推荐电影查询源”会话中的五个来源。主页「检索新资源」也会进入该页面。

| 来源 | 当前接入方式 | 影片文件 |
| --- | --- | --- |
| Internet Archive | 官方 Advanced Search 与 Metadata API，无需 Key；可按电影/电视剧、最低评分和页码查询 | 在结果中点击「读取下载文件」，选择来源公开列出的文件，加入现有下载队列 |
| TMDB | 官方 v3 API；由管理员在「来源与密钥」保存 API Key 或 Read Access Token，也兼容原有环境变量 | 提供资料、评分、类型，不提供影片文件 |
| 豆瓣电影 | 带片名的网页检索入口 | 不提供下载直链 |
| JustWatch | 带片名的网页入口，地区由 `CINEVAULT_JUSTWATCH_REGION` 配置，默认 `jp`；官方合作 API 尚未接入 | 查询观看平台 |
| IMDb | 可选择本地数据集、官方 AWS API（按编号）或 OMDb 第三方接口（按片名 / 编号） | 提供资料与评分，不提供影片文件 |

更新已有 Docker 部署，保留账户、设置、下载任务、文件和数据卷：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/update-local.ps1
```

若未配置 Key 或导入数据，页面会显示相应状态并提供网页查询链接。来源连接失败也会逐一显示，不会把示例数据当作真实结果。

启用 IMDb：从 <https://data.imdb.com/non-commercial-datasets/> 获取两个官方数据集，复制到容器数据卷内 `/data/imdb/`。例如在项目目录执行：

```powershell
docker compose exec web mkdir -p /data/imdb
docker compose cp ./title.basics.tsv.gz web:/data/imdb/title.basics.tsv.gz
docker compose cp ./title.ratings.tsv.gz web:/data/imdb/title.ratings.tsv.gz
```

IMDb 数据限个人及非商业用途。查询会流式扫描文件，超过 65 秒将提示超时；没有中文译名映射，不匹配时请用英文原名。评分过滤和排序针对每来源当前页，IMDb 先过滤再分页；可继续翻页，最多 20 页。评分为 0 表示暂无评分。勾选「同时显示暂无评分」可在最低分大于 0 时仍保留这些内容。

每项结果显示评分来源及人数（来源提供时）。Internet Archive 的用户评分由 5 分制折算为 10 分制，不能视为 IMDb / 豆瓣评分；不同来源的同名条目独立保留。检索结果合并进共享资源库（最多 1000 项），可在「内容比较」中选择最多四项比较。搜索和元数据读取不会自动开始下载。文件列表过滤私有文件、受限项目和危险路径，并展示来源标注的许可信息，实际下载仍受来源可访问性影响。

联网请求沿用现有代理配置，明确允许 `archive.org`、`api.themoviedb.org`、`www.omdbapi.com` 与 `api-fulfill.dataexchange.us-east-1.amazonaws.com`。更换代理后可通过 `CINEVAULT_SOURCE_PROXY` 设置；留空则使用公共 DNS 直连。带认证头的 API 请求不跟随重定向；下载文件重定向仍执行原有公共地址与下载检查。

新增登录保护 API：`GET /api/sources`、`POST /api/search`、`POST /api/resources/archive/{identifier}`。修改请求继续要求 CSRF 令牌。API Key 不出现在配置状态接口、搜索响应或错误文本中。

本地部署使用完整 ASP.NET Core MVC 服务，包含真实抓取与 HTTP 下载。登录采用应用内置用户，无需 ChatGPT 账户或外部登录服务。默认创建管理员 `admin`，不开放自助注册。

首次部署（Windows PowerShell）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/init-local.ps1
docker compose up -d --build
```

打开 http://localhost:5188 。初始密码位于项目目录的 `.secrets/admin-password.txt`，只用于首次创建管理员。若当前终端找不到 Compose 插件，可直接调用本机 Docker Desktop 的插件：

```powershell
& 'C:\Program Files\Docker\Docker\resources\cli-plugins\docker-compose.exe' -f compose.yaml up -d --build
```

默认端口只绑定 `127.0.0.1`。容器使用 .NET 10 和非 root 的 app 用户运行，并包含健康检查及自动重启配置。

### 用户与登录

- 登录后，管理员可从左侧“用户管理”创建普通用户或管理员、重置他人密码、启用/停用账户。
- 顶部用户名可进入“修改密码”，也可退出登录。
- 密码长度为 12–128 个字符，数据库仅保存带随机盐的密码哈希；初始密码文件不进入镜像或源码下载包。
- 连续 5 次登录失败后锁定该账户 15 分钟；管理员重置密码会清除锁定。
- 修改或重置密码、停用账户会使该账户已有登录立即失效。
- 登录、账户表单及 API 修改请求均校验 CSRF 令牌；Cookie 设置 HttpOnly、SameSite=Strict，默认会话有效期 8 小时。
- 所有用户共享同一资源库、下载队列与目录设置。账户管理与来源密钥配置需要管理员权限。

### 数据与宿主机下载目录

| 内容 | 保存位置 |
| --- | --- |
| 用户、任务、设置、资源库、登录加密密钥 | Docker 命名卷 `cinevault_cinevault-data`，容器内 `/data` |
| 影片下载文件 | 默认宿主机项目内 `downloads`，容器内 `/downloads` |
| 首次管理员密码 | 宿主机 `.secrets/admin-password.txt` |

重建镜像、重启或重新创建容器不会清空账户、任务及文件。卸载时保留数据卷，不要使用 `docker compose down -v`，除非确实要删除数据。

修改宿主机保存目录时，编辑 `.env` 后重新创建容器，例如：

```dotenv
CINEVAULT_PORT=5188
CINEVAULT_DOWNLOADS_PATH=D:/Movies/CineVault
```

```powershell
docker compose up -d --force-recreate
```

网页中填写的是容器内路径 `/downloads` 或其子目录，而不是 Windows 的盘符路径。选择新宿主机目录不会自动移动原有下载文件。Linux 宿主机需确保挂载目录对镜像的 app 用户（UID 1654）可写。

常用命令：

```powershell
docker compose ps
docker compose logs --tail 100 web
docker compose restart web
docker compose down
```

## 不使用 Docker 时运行

安装 .NET 10 SDK，在项目根目录执行：

```powershell
dotnet run --project server/CineVault.csproj
```

打开 http://localhost:5188 ，初次运行会创建 `admin`，随机初始密码写入 `server/App_Data/initial-admin-password.txt`。也可通过 Bootstrap__AdminPasswordFile 指定初始密码文件。账户已存在时不会因配置变化而重置密码。

直接运行时默认仅接受本机连接，数据位于 `server/App_Data`。容器通过端口映射接收连接；主机名称由 AllowedHosts 约束。

## 保存目录

默认根目录为 server/App_Data/downloads。要保存到其他位置，修改 server/appsettings.json 并重启服务，例如：

```json
"AllowedDownloadRoots": ["D:\\Movies", "E:\\TV"]
```

在“目录与设置”中填写允许范围内的绝对路径，点击“检查目录”，再保存设置。开启自动归档后，电影和电视剧分别保存到 Movies、Series 子目录。已创建任务保留当时的路径和重试策略。禁止越出允许根目录、网络共享路径以及符号链接/目录联接。

## 下载行为

- 内置下载器处理公开 HTTP/HTTPS 文件直链；磁力和 ED2K 链接可复制到相应下载器。
- 下载 .torrent 只保存种子文件，不会自动下载种子中的电影。网盘登录、HLS 合并、DRM 与 BitTorrent 下载不在本版本中。
- 支持并发数 1–4，自动重试 0–5 次、手动重试、暂停、继续、取消。
- 续传依赖源服务器返回 ETag/Last-Modified 和正确的 Range 响应；不满足条件时从头下载。
- 先写 .part，完整下载后改名；取消会保留部分文件。“清理已结束记录”仅移除任务记录，不删除已下载文件或部分文件。
- 返回 HTML/JSON 的链接会进入失败/重试状态，避免把网页误当作视频保存。
- 队列及进度以 JSON 持久化。重启后未完成的下载重新排队，暂停任务保持暂停。
- 文件名带有任务 ID，防止覆盖已有文件；下载地址只允许 HTTP/HTTPS 标准端口，并检查域名解析和重定向地址。

## 抓取配置与当前验证范围

目标地址：https://www.gying.net/

2026-11-15 已按要求在本地 Docker 部署中关闭 robots.txt 检查（Crawler__RespectRobotsTxt=false）。启用明确列出的源站代理、补齐服务器缺失的官方交叉签名中间证书后，实际首页请求返回 HTTP 200，但页面为 ParkLogic 域名停放/广告跳转页（标题 Redirecting...），没有影视目录。此时返回 source_domain_parked，不会把广告跳转页面当作影片，也不会清空已有资源。

在线演示的影片、评分与队列状态均为示例，没有真实影片下载地址。本地版只显示实际解析的数据；抓取失败会保留上次数据并返回错误。

Crawler:RespectRobotsTxt 默认为 true，可按 User-agent 和每个 URL 检查规则，支持最长匹配、同长允许优先、通配符及结束标记。本地 Docker 已将该选项设为 false，直接读取页面；可通过 .env 的 CINEVAULT_RESPECT_ROBOTS=true 恢复检查。抓取受限时保留已有目录与资源，可在“下载任务 → 新建任务”手动添加 HTTP/HTTPS 直链。

适配器支持 JSON-LD Movie / TVSeries，以及详情页的 h1、评分正则和公开资源链接。它不执行网页 JavaScript。页面依赖动态接口、访问验证或页面结构变化时，需要先检查实际公开页面，再调整解析器；当前不保证可直接抓到 gying.net 的资源。

server/appsettings.json 的 Crawler 节点：

| 配置 | 含义 |
| --- | --- |
| BaseUrl | 源站根地址 |
| RespectRobotsTxt | 是否检查 robots.txt；应用默认 true，本地 Docker 当前 false |
| ListPaths | 要扫描的列表页路径，最多 4 个 |
| DetailPathPattern | 详情页链接路径正则 |
| RatingPattern | 评分正则，命名分组 rating |
| GenrePattern | 类型正则，命名分组 genre |
| MaxDetailPages | 每次最多读取的详情页，限制 1–30 |
| DelayMilliseconds | 请求间隔，限制 500–10000 ms |

JSON-LD 评分会按 bestRating 换算到 10 分制。真实模式显示“源站评分”，未解析到的 IMDb 评分显示空值；不推断或编造其他来源评分。无评分内容需把最低评分调为“不限”查看。

## Sites 版

Sites 部署 dist 中的静态界面，不运行 .NET 进程，也不能写入访客的任意磁盘目录。静态版提供筛选、比较、模拟队列和浏览器内偏好保存；运行本地版后由同源 /api 自动切换到真实服务。本地版提供相同界面，无需从在线演示跨域连接本机。

“目录与设置”和帮助弹窗均可下载此 MVC 项目。

## API

API 均需要有效登录；修改请求需带 X-CSRF-TOKEN（工作台 HTML 的 csrf-token meta 字段）。浏览器 Origin 必须与当前服务同源。/healthz 仅提供无敏感信息的匿名健康检查。

| 方法 | 路径 | 行为 |
| --- | --- | --- |
| GET | /api/health | 服务识别 |
| GET | /api/media | 已保存资源库与抓取警告 |
| POST | /api/crawl | 抓取列表和详情页 |
| GET/PUT | /api/settings | 读取/保存设置 |
| GET | /api/defaults | 默认设置 |
| POST | /api/directory/check | 检查目录可写性，正文 directory |
| GET/POST | /api/downloads | 列出/创建任务 |
| POST | /api/downloads/{id}/{action} | pause、resume、retry、cancel |
| DELETE | /api/downloads/finished | 清理已完成/取消记录 |

创建任务可传 mediaId、resourceId、directory；手动直链任务传 title、url、directory。

## 验证

```powershell
npm test
dotnet build tests/CineVault.Checks.csproj
dotnet tests/bin/Debug/net10.0/CineVault.Checks.dll
```

当前功能已在 .NET 8 兼容目标中通过 116 项 C# 检查，并通过 9 项前端检查。生产目标为 .NET 10；兼容目标的验证命令如下：

```powershell
dotnet build tests/CineVault.Checks.csproj -p:CineVaultFramework=net8.0
dotnet tests/bin/Debug/net8.0/CineVault.Checks.dll
```

C# 检查使用受控 HTTP 响应和实际文件写入，覆盖电影日期排序、密钥保存与切换、资源名称匹配、4K 与中文字幕识别、Prowlarr / qBittorrent 协议、批量下载条件、HTTP/BT 共享并发、失败重试、暂停续传、取消清理、队列恢复和目录/地址约束。前端检查覆盖首次同步、最新电影筛选、候选显示与文本转义。这些检查不等同于真实来源或下载器联通成功。

账户功能曾通过 32 项集成检查、3 项容器重建数据检查，并确认旧会话可跨容器重建继续使用。`tests/auth-smoke.py` 覆盖登录、权限、CSRF、密码变更、停用、锁定与数据持久化，应仅对一次性测试容器运行。最新 BT 功能尚需在配置真实索引与下载器后做端到端验证。

## 发布与源码打包

本地运行：

```powershell
dotnet publish server/CineVault.csproj -c Release -o artifacts/publish
```

发布目录会包含 wwwroot 资源；从发布目录运行 CineVault。重新生成源码下载包：

```powershell
python scripts/package-source.py
```

打包排除运行数据、编译产物、Git 元数据和 Sites 项目标识。

## 当前源站网络兼容配置

Docker Desktop 环境默认通过 CINEVAULT_SOURCE_PROXY=http://http.docker.internal:3128 请求明确配置的 Network:ProxyHosts（www.gying.net、gying.net）。其他域名及下载地址继续使用公网地址检查。代理配置来自本地服务端配置，网页不能修改该允许列表。

如宿主机没有 Docker Desktop 代理，可在 .env 中将 CINEVAULT_SOURCE_PROXY 设为空，或填写可用的 HTTP/HTTPS 代理地址，再运行 docker compose up -d --force-recreate。更换 Crawler:BaseUrl 时也应同步检查 Network:ProxyHosts。

server/Certificates/chain.pem 仅包含 Let’s Encrypt 官方交叉签名中间证书，帮助构建到系统已有 ISRG Root X1 的有效证书链；不会添加新的根信任或关闭证书验证。证书来源、指纹与有效期记录在同目录 README.txt 中。
