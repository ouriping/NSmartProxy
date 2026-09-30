# NSmartProxy 安装与部署安全审计

本文档文件名为 `SECURITY_FIX.md`，放在仓库根目录。记录在 `ouriping/NSmartProxy`（fork 自 `tmoonlight/NSmartProxy`，默认分支 `master`）源码中核对过的安装、部署和出厂配置问题。下文「问题描述」保留审计时的代码位置；对应修复已经写入后续提交，主要包括：随机初始管理员口令、默认关闭匿名登录并校验管理员角色、管理端口默认绑定本机且登录改为 POST + HttpOnly Cookie、会话改为服务端保存的随机令牌、控制通道与反向隧道启用 TLS 并校验指纹、清空默认端口映射、收紧 FTP/Docker/Windows 服务权限、PBKDF2 口令、日志路径校验，以及把服务端和客户端目标框架升到 `net8.0`。

管理端口在 Linux 的 `HttpListener` 上不能可靠启用 HTTPS，因此默认只监听 `127.0.0.1`。需要从公网管理时，应放在 TLS 反向代理后面，而不是把 `WebAPIAddress` 改成 `0.0.0.0` 后继续使用明文 HTTP。

实现时把服务端、客户端和 FTP 插件的 `log4net` 升到 `3.3.2`。2.0.17 已修复 CVE-2018-1285，但 GHSA-4f7c-pmjv-c25w（CVE-2026-40021）覆盖 3.3.0 之前的全部版本。登录、改密、客户端登录以及新增/修改用户口令的接口只接受 POST 表单体，不再从查询字符串读取口令。WinForms / WinService 仍通过 `packages.config` 引用 `log4net` 2.0.17，需要在 Windows 上还原包后再升到 3.3.2。

审计对象是安装脚本、Windows 服务安装、Dockerfile、Azure Pipelines、默认 `appsettings.json`、服务端 Web 管理接口、客户端登录缓存、控制通道与隧道传输，以及仓库里声明的依赖版本。仓库中没有 `docker-compose` 文件，也没有 Linux systemd unit。

行号以审计时的 `master` 工作区为准。标注「待确认」的条目没有在本环境实际跑通对应运行时。

## 汇总

| 编号 | 严重程度 | 标题 | 主要位置 |
| --- | --- | --- | --- |
| 1 | 高 | 出厂管理员账号密码为 `admin` / `admin` | `src/NSmartProxy/Extension/HttpServer_APIs.cs` |
| 2 | 高 | 默认允许匿名登录，管理接口不校验管理员角色 | `NSPServerConfig.cs`、`HttpServer.cs`、`HttpServer_APIs.cs` |
| 3 | 高 | 管理后台监听所有网卡，登录口令经 HTTP GET 提交 | `HttpServer.cs`、`Web/login.html` |
| 4 | 高 | 会话令牌密钥过短、明文落盘、固定 IV，且不校验过期 | `Server.cs`、`EncryptHelper.cs`、`SecurityTcpClient.cs` |
| 5 | 高 | 客户端与服务端的认证和隧道默认明文传输 | `SecurityTcpClient.cs`、`Server.cs` |
| 6 | 高 | 客户端默认配置会把 RDP、HTTP 等内网端口映射出去 | 各 `appsettings.json` |
| 7 | 高 | FTP 插件默认弱口令，根目录是整个盘符 | `plugins/NSmartProxyFTP/appsettings.json` |
| 8 | 高 | 容器以 root 运行 SDK 镜像，并暴露过大端口段 | 两个 `Dockerfile` |
| 9 | 高 | Windows 客户端服务以 LocalSystem 安装 | `ProjectInstaller.Designer.cs` |
| 10 | 中 | 用户密码只做无盐 SHA-256，用户库文件权限未收紧 | `EncryptHelper.cs`、`Server.cs` |
| 11 | 中 | 客户端口令出现在命令行，令牌明文写入 `.usercache` | `NSmartProxyClient.cs`、`UserCacheManager.cs` |
| 12 | 中 | 日志下载只检查扩展名，可路径穿越读取 `.log` 文件 | `HttpServer_APIs.cs` |
| 13 | 中 | 证书口令硬编码，PFX 写到工作目录 | `CAGen.cs`、`HttpServer_APIs.cs` |
| 14 | 中 | Debug 构建跳过全部 Web 鉴权 | `HttpServer.cs` |
| 15 | 中 | 管理界面使用有 XSS 公告的 jQuery 3.4.0，并把数据直接插入 HTML | `Web/jquery-slim.min.js`、`dashboard.js`、`connections.js` |
| 16 | 中 | FTP 插件锁定已停止支持的 .NET Core 2.2，以及受 CVE 影响的 log4net 2.0.8 | `plugins/NSmartProxyFTP/NSmartProxyFTP.csproj` |
| 17 | 中 | 服务端目标框架和 Docker 基础镜像停留在已停止支持的 .NET 6 | `NSmartProxy.ServerHost.csproj`、`Dockerfile` |
| 18 | 中 | 个人部署脚本用未认证的 HTTP GET 触发远程重启 | `src/deploy.cmd` |
| 19 | 低 | 未登录即可查询端口和版本，异常响应带回堆栈 | `HttpServer_APIs.cs`、`HttpServer.cs` |
| 20 | 低 | 管理页面静态文件无需登录即可下载 | `HttpServer.cs` |
| 21 | 低 | `System.Text.Json` 8.0.0 落在已知拒绝服务公告的版本区间（触发条件待确认） | WinForms `packages.config` |
| 22 | 低 | 自带 `appsettings.json` 含 `//` 注释，当前解析器可能无法加载（待确认） | `ConfigHelper.cs` 与各 `appsettings.json` |

严重程度分布：**高 9，中 9，低 4，合计 22。**

已核对、当前版本不落在对应 CVE 影响范围内、因此不单列问题的依赖：

- `Newtonsoft.Json` 13.0.3。CVE-2024-21907 影响 13.0.1 之前的版本。
- 主工程 `log4net` 2.0.17。CVE-2018-1285 在 2.0.10 修复；FTP 插件仍引用 2.0.8，见问题 16。
- `LiteDB` 5.0.21。CVE-2022-23535 影响 5.0.13 之前的版本。
- `Bootstrap` 4.3.1。CVE-2019-8331 在 4.3.1 修复。
- WinForms `packages.config` 中的 `System.Net.Http` 4.3.4。CVE-2018-8292 影响 4.3.4 之前的版本。

---

## 1. 出厂管理员账号密码为 admin / admin

- **严重程度：** 高
- **涉及文件：**
  - `src/NSmartProxy/Extension/HttpServer_APIs.cs` 第 38–42 行
  - `README_SERVER_CN.md` 第 47 行
  - `README.md` 第 254 行

空用户库第一次初始化时会写入固定账号：

```38:42:src/NSmartProxy/Extension/HttpServer_APIs.cs
            //如果库中没有任何记录，则增加默认用户
            if (Dbop.GetLength() < 1)
            {
                AddUserV2("admin", "admin", "1");
            }
```

`isAdmin` 传入 `"1"`，这是管理员。官方安装说明要求安装后访问 `http://ip:12309`，并写明出厂用户密码为 `admin/admin`。

- **潜在影响：** 任何能访问管理端口的人都可以用公开口令登录，添加用户、断开客户端、下载日志、绑定端口。内网穿透服务通常把管理端口暴露在公网。
- **修复建议：** 首次启动不要写入固定口令。若数据库为空，生成一次性随机密码并只打印到安装者当前终端，或强制进入改密后才能提供服务。安装文档改为要求立即修改密码。示例：

```csharp
if (Dbop.GetLength() < 1)
{
    var password = RandomNumberGenerator.GetString(
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 20);
    AddUserV2("admin", password, "1");
    Console.WriteLine("首次启动已创建管理员 admin，初始密码只显示这一次：");
    Console.WriteLine(password);
}
```

同时在配置里增加 `mustChangePassword`，未修改前拒绝反向注册和端口监听。

## 2. 默认允许匿名登录，管理接口不校验管理员角色

- **严重程度：** 高
- **涉及文件：**
  - `src/NSmartProxy.Data/Config/NSPServerConfig.cs` 第 15 行，`supportAnonymousLogin = true`
  - `src/NSmartProxy.ServerHost/ServerHost.cs` 第 86–89 行：配置文件不存在时直接 `new NSPServerConfig()` 并保存
  - `src/NSmartProxy/Extension/HttpServer_APIs.cs` 第 244–265 行：用户名为空时自动创建 `temp_` 用户并签发令牌
  - `src/NSmartProxy.Infrastructure/Extensions/HttpServer/HttpServer.cs` 第 187–199 行
  - `src/NSmartProxy/Authorize/NSPServerContext.cs` 第 22、31 行：`TokenCaches` 只被创建，全仓库没有写入或校验

字段默认值是打开匿名登录。配置文件缺失时会把这个默认值存下来。仓库自带的 `src/NSmartProxy.ServerHost/appsettings.json` 没有 `supportAnonymousLogin` 字段；Newtonsoft.Json 对缺失字段会保留字段初始化值，因此解析成功时匿名登录仍为 true。该文件含注释，解析是否成功见问题 22。

管理接口的校验只做了这些事：Cookie `NSPTK` 存在，解密后的用户名非空。代码注释写着「根据不同的用户角色分配权限」，但没有实现。`TokenCaches` 也没有参与校验。匿名登录拿到的令牌同样能通过这段检查，从而调用带 `[Secure]` 的 `AddUserV2`、`RemoveUser`、`GetUsers`、`SetConfig`、`BanUsers`、`GetLogFile`、`GenerateCA`。

- **潜在影响：** 在默认配置能成功启动时，远程攻击者无需口令即可获得管理能力，包括创建新的管理员、踢掉已有客户端、读取日志和用户口令哈希。
- **修复建议：**

```csharp
public bool supportAnonymousLogin = false;
```

`[Secure]` 校验中至少确认用户存在、未禁用，并且管理类接口要求 `isAdmin == "1"`。匿名用户即使保留，也只能注册自己的隧道，不能调用用户管理和配置接口。签发的令牌写入 `TokenCaches`，校验时必须命中，退出或过期后删除。

## 3. 管理后台监听所有网卡，登录口令经 HTTP GET 提交

- **严重程度：** 高
- **涉及文件：**
  - `src/NSmartProxy.Infrastructure/Extensions/HttpServer/HttpServer.cs` 第 76 行：`http://+:{WebManagementPort}/`
  - `src/NSmartProxy/Web/login.html` 第 37 行：`<form ... action="Login" method="get">`
  - `HttpServer.cs` 第 165–173 行：接口参数只从 `request.QueryString` 读取
  - `HttpServer_APIs.cs` 第 222–234 行：用脚本写 Cookie，没有 `HttpOnly`、`Secure`、`SameSite`
  - `src/NSmartProxy.Data/Config/NSPServerConfig.cs` 第 11 行，以及服务端 `appsettings.json` 第 4 行：默认管理端口 `12309`
  - `README_SERVER_CN.md` 第 37–47 行：安装步骤是 `sudo dotnet ...`，然后用 `http://ip:12309` 登录

`http://+:端口/` 会绑定所有网卡。配置里没有管理地址字段，安装后无法只听 `127.0.0.1`。登录表单使用 GET，口令进入 URL、浏览器历史、代理日志和 `Referer`。服务端即使把表单改成 POST，现有代码仍然只读查询字符串。Cookie 由页面脚本写入，浏览器不允许脚本设置 `HttpOnly`。

- **潜在影响：** 管理端口随安装暴露在公网上；同一网络或中间设备上的人可以拿到管理员口令；一旦页面存在脚本注入，会话 Cookie 可被读走。
- **修复建议：**

```json
{
  "WebAPIPort": 12309,
  "WebAPIAddress": "127.0.0.1"
}
```

监听前缀改为 `https://127.0.0.1:12309/`，公网访问只通过反向代理或 SSH 隧道。登录改为 POST，服务端从请求体读取口令。成功后由服务端下发：

```csharp
response.Headers.Add("Set-Cookie",
    "NSPTK=" + token + "; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=28800");
```

在防火墙中默认只放行业务端口，不放行 `12309`。Windows 上 `http://+:端口/` 需要管理员或 URL ACL，这不是把进程长期以管理员运行的理由。

## 4. 会话令牌密钥过短、明文落盘、固定 IV，且不校验过期

- **严重程度：** 高
- **涉及文件：**
  - `src/NSmartProxy/Server.cs` 第 54、145–156 行：密钥文件 `./nsmart_sec_key`，不存在时 `RandomHelper.NextString(8)`，再 `File.WriteAllText`
  - `src/NSmartProxy.Infrastructure/EncryptHelper.cs` 第 186–213 行：默认密钥 `"12345678"`，IV 为固定字节 `Keys`
  - `EncryptHelper.cs` 第 206–209 行：密钥不足 32 字节时用空格补齐
  - `src/NSmartProxy.Infrastructure/StringUtil.cs` 第 104–121 行：令牌内容是 `用户名|日期`
  - `src/NSmartProxy.Infrastructure/Shared/SecurityTcpClient.cs` 第 181 行注释：「尚未增加时间戳规则」
  - `SecurityTcpClient.cs` 第 180–187 行：解密成功且用户名存在即通过，不检查日期

密钥只有 8 个字符，文件按默认权限创建，Linux 上通常是 `0644`，同机其他用户可读。IV 写死在程序里。令牌没有随机数、没有服务端会话号。日期被解析后没有和当前时间比较。进程启动后、读到文件之前，静态字段仍是源码中的 `"12345678"`。

- **潜在影响：** 读到 `nsmart_sec_key` 的人可以为 `admin` 伪造当天令牌，直接调用管理接口和反向连接。令牌被窃取后也不会因为日期字段失效。固定 IV 让相同明文得到相同密文。
- **修复建议：** 用 `RandomNumberGenerator` 生成至少 32 字节密钥，保存时设置权限：

```csharp
if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
{
    File.WriteAllText(path, Convert.ToBase64String(key));
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}
```

令牌改为随机 256 位会话 ID，服务端只保存哈希，并记录用户、角色和过期时间。不要把可逆的用户名密文当作会话凭证。AES 若仍用于其他数据，IV 必须每次随机生成并随密文保存。

## 5. 客户端与服务端的认证和隧道默认明文传输

- **严重程度：** 高
- **涉及文件：**
  - `src/NSmartProxy.Infrastructure/Shared/SecurityTcpClient.cs` 第 109–119 行：TCP 连接后直接发送 `0xF9`、长度和 ASCII 令牌
  - `src/NSmartProxy/ClientConnectionManager.cs` 第 48 行：反向连接端口 `IPAddress.Any`
  - `src/NSmartProxy/Server.cs` 第 196 行：配置端口同样是 `IPAddress.Any`
  - `Server.cs` 第 843–853 行附近：消费端与客户端之间直接拷贝流
  - `Server.cs` 第 427 行：只有消费端口已经绑定证书时，才对外部访问调用 `ProcessSSL`
  - `src/NSmartProxy.Infrastructure/Extensions/StreamExtension.cs` 第 88–93 行：该 TLS 只包一层 `SslStream.AuthenticateAsServer`

客户端登录、心跳、反向连接和控制指令都走明文 TCP。令牌以 ASCII 写在流上。隧道里的 RDP、FTP、HTTP 内容默认也不加密。证书只影响已经单独绑定证书的对外端口，不影响客户端到服务端这条控制通道。

- **潜在影响：** 公网路径上的监听者可以取得登录令牌，并看到隧道里的口令和文件。伪造或重放令牌后可以占用受害者的隧道。
- **修复建议：** 控制通道先做 TLS 1.2+（或 1.3），校验证书或钉扎指纹，再发送令牌。数据通道不要把内网协议原样暴露到公网；至少为每条隧道提供 TLS，或让使用者明确知道该端口是明文。示例配置：

```json
{
  "Tls": {
    "Enabled": true,
    "CertificatePath": "/etc/nsp/server.pfx",
    "CertificatePasswordEnv": "NSP_CERT_PASSWORD",
    "ClientPinnedThumbprint": "<发布时打印的指纹>"
  }
}
```

证书口令从环境变量或系统密钥存储读取，不要写进仓库或 `appsettings.json`。

## 6. 客户端默认配置会把 RDP、HTTP 等内网端口映射出去

- **严重程度：** 高
- **涉及文件：**
  - `src/NSmartProxyClient/appsettings.json` 第 17–47 行：映射 UDP `5901`、TCP `80`、TCP `3389` 到外网 `12345` 等
  - `src/NSmartProxyWinform/appsettings.json` 第 6–18 行：映射 `3389`、`80`、`21`
  - `src/NSmartProxyClient/NSmartProxyClient.csproj` 第 32–37 行：该文件 `CopyToPublishDirectory` 为 `Always`
  - `README.md` 第 99–127 行：安装示例同样把 `3389` 作为默认映射

这些不是被注释掉的样例。控制台客户端的发布输出会带上这份配置。客户端启动时按文件注册隧道。服务端若允许匿名登录（问题 2），安装后不做修改就会把本机远程桌面和网站映射到公网端口。

- **潜在影响：** 按文档解压即运行时，内网 RDP、FTP、VNC 或 HTTP 会出现在服务器的公网端口上，且隧道本身默认明文（问题 5）。
- **修复建议：** 发布包中的 `Clients` 使用空数组。危险端口不要出现在默认文件里。启动时如果配置仍包含 `3389`、`5900`、`22`、`445` 等端口，打印警告并要求 `--i-understand` 一类的显式开关。示例：

```json
{
  "ProviderWebPort": 12309,
  "ProviderAddress": "127.0.0.1",
  "Clients": []
}
```

## 7. FTP 插件默认弱口令，根目录是整个盘符

- **严重程度：** 高
- **涉及文件：**
  - `plugins/NSmartProxyFTP/appsettings.json` 第 8–13 行
  - `plugins/NSmartProxyFTP/NSmartProxyFTP.csproj` 第 18–20 行：配置会复制到输出目录
  - `plugins/NSmartProxyFTP/FtpServer.cs` 第 30–33 行：监听 `IPAddress.Any`

默认用户是：

```json
{
  "username": "admin",
  "password": "654123",
  "rootDir": "d:\\"
}
```

口令是明文，根目录是 `d:\`。FTP 服务监听所有网卡，同一份配置还会把 FTP 端口和被动端口经 NSmartProxy 映射出去。

- **潜在影响：** 使用这份默认配置启动插件的机器，可用公开口令读写 `d:\` 下的文件。映射到公网后影响不再限于本机。
- **修复建议：** 删除仓库中的真实口令和盘符根目录，改为占位符。启动时若口令仍是 `654123` 或根目录是盘符根，拒绝启动。FTP 账号使用哈希保存，数据连接限制在配置的目录内，并默认只绑定需要的地址。

## 8. 容器以 root 运行 SDK 镜像，并暴露过大端口段

- **严重程度：** 高
- **涉及文件：**
  - `src/NSmartProxy.ServerHost/Dockerfile` 第 18–23 行
  - `src/NSmartProxyClient/Dockerfile` 第 2–5 行
  - `azure-pipelines.yml` 第 67–85 行：流水线在发布目录里 `docker build` 并推送 `tmoonlight/nspserver`、`tmoonlight/nspclient`

两个 Dockerfile 都使用 `mcr.microsoft.com/dotnet/sdk:6.0` 作为运行镜像，没有 `USER`，因此容器内进程是 root。服务端 `EXPOSE 12300-22300` 声明了一万个端口。镜像注释写 “need combile 1st”，构建上下文取决于调用者；`COPY / /app/` 会把上下文根目录整份拷进镜像。

- **潜在影响：** 容器被攻破后具备容器内 root 权限。SDK 镜像包含编译工具，攻击面大于运行时镜像。过大的端口声明会促使运维把大段公网端口映射进容器，其中包含管理端口和业务端口。
- **修复建议：**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/NSmartProxy.ServerHost/NSmartProxy.ServerHost.csproj \
    -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN useradd --create-home --uid 10001 nsp
WORKDIR /app
COPY --from=build /out .
USER nsp
EXPOSE 7841 7842
ENTRYPOINT ["dotnet", "NSmartProxy.ServerHost.dll"]
```

管理端口 `12309` 不要默认映射到宿主机公网。用 `--read-only`、去掉多余 Linux capabilities，并把 `nsmart_user.db`、密钥文件放到单独的卷上，卷内文件权限设为 `0600`。

## 9. Windows 客户端服务以 LocalSystem 安装

- **严重程度：** 高
- **涉及文件：** `src/NSmartProxyWinService/ProjectInstaller.Designer.cs` 第 36–46 行

```36:46:src/NSmartProxyWinService/ProjectInstaller.Designer.cs
            this.serviceProcessInstaller1.Account = System.ServiceProcess.ServiceAccount.LocalSystem;
            this.serviceProcessInstaller1.Password = null;
            this.serviceProcessInstaller1.Username = null;
            // ...
            this.serviceInstaller1.StartType = System.ServiceProcess.ServiceStartMode.Automatic;
```

服务名是 `NSPClient`，开机自动启动，账户是 `LocalSystem`。服务端文档要求用管理员命令行执行 `action:install`。服务端使用 `PeterKottas.DotNetCore.WindowsService`，仓库里没有再指定服务账户；该库安装出的账户是否也是 LocalSystem，**待确认**。可以确认的是管理监听前缀 `http://+:端口/` 在 Windows 上通常要求提升权限或预先配置 URL ACL。

- **潜在影响：** 客户端进程被漏洞或恶意隧道利用时，得到的是操作系统最高本地权限，而不是一个只能访问指定端口的普通用户。
- **修复建议：** 改为虚拟服务账户或专用低权限用户，只给它读取自己的配置目录和访问目标内网端口的权限：

```csharp
this.serviceProcessInstaller1.Account = ServiceAccount.LocalService;
```

不要为了 `http://+:端口/` 让整个服务端长期以管理员运行。安装时用 `netsh http add urlacl` 把具体前缀授权给该服务账户。

## 10. 用户密码只做无盐 SHA-256，用户库文件权限未收紧

- **严重程度：** 中
- **涉及文件：**
  - `src/NSmartProxy.Infrastructure/EncryptHelper.cs` 第 563–574 行
  - `src/NSmartProxy/Extension/HttpServer_APIs.cs` 第 217、257、279、312、353 行：比对和保存都是 `EncryptHelper.SHA256(口令)`
  - `src/NSmartProxy/Server.cs` 第 53、99 行：数据库路径 `./nsmart_user.db`，直接 `new LiteDbOperator`，没有设置文件权限
  - `src/NSmartProxy.Data/DBEntites/User.cs`：`userPwd` 与用户名、是否管理员一起存放

SHA-256 没有盐，也没有工作因子。`admin` 的哈希可以预计算。`GetUsers` 返回的是去掉部分展示字段后的用户 JSON；哈希仍在数据库文件里。数据库文件用默认权限创建。

- **潜在影响：** 备份、容器卷或同机用户读到 `nsmart_user.db` 后，可以用彩虹表恢复弱口令，再登录管理端。
- **修复建议：** 使用 PBKDF2、bcrypt 或 Argon2id，每用户随机盐。比对使用常量时间比较。创建数据库和密钥文件后立即收紧权限。已有哈希无法直接升级，应在下次登录成功时重新哈希，并作废旧值。

## 11. 客户端口令出现在命令行，令牌明文写入 .usercache

- **严重程度：** 中
- **涉及文件：**
  - `src/NSmartProxyClient/NSmartProxyClient.cs` 第 62–67 行：参数个数为 4 时，`args[1]` 当用户名、`args[3]` 当密码，不校验开关名称
  - `src/NSmartProxyClient/Properties/launchSettings.json` 第 5 行：`commandLineArgs` 为 `-p admin -pwd admin`
  - `README.md` 第 173–181 行：示例是 `./NSmartProxyClient -u admin -p admin123`，并说明会生成 `.usercache`
  - `src/NSmartProxy.ClientRouter/Authorize/UserCacheManager.cs` 第 65–98、108–119 行：把令牌和用户名以 JSON 写到缓存文件，`File.Create` / `File.WriteAllText` 未收紧权限

口令会出现在进程列表和 shell 历史里。缓存里保存的是可以继续登录的令牌，不是一次性票据。

- **潜在影响：** 同一主机上的其他用户可以读取命令行或 `.usercache`，接管该客户端身份。开发启动配置里的 `admin` / `admin` 还会强化问题 1 的默认口令。
- **修复建议：** 口令从环境变量、标准输入或系统凭据管理器读取，不要放进参数。`.usercache` 保存后设置 `0600`，并考虑使用操作系统凭据库。删除 `launchSettings.json` 里的默认口令。参数解析应识别 `-u` 和 `-p`，避免把任意第 2、第 4 个参数当成凭据。

## 12. 日志下载只检查扩展名，可路径穿越读取 .log 文件

- **严重程度：** 中
- **涉及文件：** `src/NSmartProxy/Extension/HttpServer_APIs.cs` 第 116–132 行

```csharp
string suffix = Path.GetExtension(filekey);
string fileFullPath = baseLogFilePath + "/" + filekey;
if (allowedSuffix == suffix)
{
    var fs = new FileStream(fileFullPath, ...);
}
```

`baseLogFilePath` 来自 `Server.cs` 第 117 行，值为 `./log`。`filekey` 来自查询字符串。扩展名是 `.log` 时，`../../其他目录/文件.log` 这类路径不会被去掉。`GetLogFileInfo` 第 87 行对 `lastLines` 直接 `int.Parse`，没有上限。

- **潜在影响：** 已登录用户，或在问题 2 的匿名管理权限下的远程调用者，可以读取工作目录之外、扩展名为 `.log` 的文件。过大的 `lastLines` 会让进程占用大量内存。
- **修复建议：**

```csharp
var root = Path.GetFullPath("./log");
var name = Path.GetFileName(filekey);
if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("非法日志名");
var full = Path.GetFullPath(Path.Combine(root, name));
if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    throw new InvalidOperationException("非法路径");
```

`lastLines` 限制在 1 到 1000。

## 13. 证书口令硬编码，PFX 写到工作目录

- **严重程度：** 中
- **涉及文件：**
  - `src/NSmartProxy/Extension/CAGen.cs` 第 51–56 行：PFX 口令为 `WeNeedASaf3rPassword`，证书有效期约 10 年
  - `src/NSmartProxy/Extension/HttpServer_APIs.cs` 第 795–810 行：`GenerateCA` 把 `Export(X509ContentType.Pfx)` 的结果写到 `./temp`
  - 同文件第 831–846 行：绑定证书时把临时文件移到 `./ca`

口令写在源码里。导出到 `./temp` 的调用没有再加导出口令。这两个目录没有权限设置。

- **潜在影响：** 能读取安装目录或仓库的人可以打开生成的证书私钥，冒充对应主机上的 TLS 终点。
- **修复建议：** 删除源码中的固定口令。生成证书时从环境变量读取导出口令，文件权限设为 `0600`，用完即从 `./temp` 删除。不要把带私钥的 PFX 提交到仓库或打进发布包。

## 14. Debug 构建跳过全部 Web 鉴权

- **严重程度：** 中
- **涉及文件：** `src/NSmartProxy.Infrastructure/Extensions/HttpServer/HttpServer.cs` 第 187–200 行

```csharp
#if !DEBUG
if (method.GetCustomAttribute<SecureAttribute>() != null)
{
    // 校验 NSPTK
}
#endif
```

Release 才会执行这段校验。Debug 构建中，带 `[Secure]` 的管理接口不检查登录。`azure-pipelines.yml` 的发布配置是 `Release`，但 Dockerfile 只写「先编译」，没有强制 `-c Release`。服务端工程的 Debug 输出路径是 `../../test/build/nspserver`。

- **潜在影响：** 若安装包或容器按 Debug 编译，管理接口对所有能访问 `12309` 的人开放。
- **修复建议：** 鉴权不要放在 `#if !DEBUG` 里。需要本地免登录时使用单独的配置项，默认 false，并且只在监听地址是环回地址时生效。发布脚本显式使用 `-c Release`。

## 15. 管理界面使用有 XSS 公告的 jQuery 3.4.0，并把数据直接插入 HTML

- **严重程度：** 中
- **涉及文件：**
  - `src/NSmartProxy/Web/jquery-slim.min.js` 第 1 行：文件头为 `jQuery v3.4.0`
  - `src/NSmartProxy/Web/dashboard.js` 第 191–194 行：日志行拼进 HTML 后调用 `$("#tbodyLogs").html(logText)`
  - `src/NSmartProxy/Web/connections.js` 第 70–95 行：`host`、`description` 等字段拼进 HTML
  - `src/NSmartProxy/Web/users.js` 第 143–164 行：用户名拼进 HTML
  - `login.html` 第 37–43 行与问题 3 的 Cookie 写法

jQuery 3.5.0 之前受 **CVE-2020-11022**、**CVE-2020-11023** 影响。公告内容是：把不可信 HTML 传给 `.html()`、`.append()` 等 DOM 方法时可能执行脚本，3.5.0 修复。本仓库的管理页确实把服务端返回的日志、主机名、描述和用户名交给 `.html()`。会话 Cookie 又不是 `HttpOnly`。

- **潜在影响：** 若日志、隧道描述或用户名被攻击者控制，管理员打开页面时脚本会在管理源上执行，并可以读走 `NSPTK`。
- **修复建议：** 将 `jquery-slim.min.js` 换成本地校验过的 3.7.x 或更新版本。展示文本使用 `textContent` 或 jQuery 的 `.text()`，不要拼接 HTML。Cookie 改为服务端 `HttpOnly`。

## 16. FTP 插件锁定已停止支持的 .NET Core 2.2，以及受 CVE 影响的 log4net 2.0.8

- **严重程度：** 中
- **涉及文件：** `plugins/NSmartProxyFTP/NSmartProxyFTP.csproj` 第 5–9 行

```xml
<TargetFramework>netcoreapp2.2</TargetFramework>
<PackageReference Include="log4net" Version="2.0.8" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="2.2.0" />
```

.NET Core 2.2 已于 2019-12-23 停止支持。`log4net` 2.0.8 受 **CVE-2018-1285** 影响：2.0.10 之前解析 log4net 配置时未禁用 XML 外部实体。利用前提是攻击者能控制被解析的 `log4net.config`。主工程引用的是 2.0.17，不在该 CVE 范围内。插件引用的 `NSmartProxy.ClientRouter` 现为 `net6.0`，和插件的 `netcoreapp2.2` 也不在同一目标框架上，按当前工程直接编译插件是否成功，**待确认**。

- **潜在影响：** 继续用 2.2 运行时部署插件，意味着运行时安全更新已经停止。若部署目录中的 `log4net.config` 可被替换，解析配置时存在 XXE 风险。
- **修复建议：** 插件改到当前仍受支持的 `net8.0`，`log4net` 升到 2.0.17 或该分支的后续安全版本。配置文件权限设为仅服务账户可读，不要从不可信目录加载 XML 配置。

## 17. 服务端目标框架和 Docker 基础镜像停留在已停止支持的 .NET 6

- **严重程度：** 中
- **涉及文件：**
  - `src/NSmartProxy.ServerHost/NSmartProxy.ServerHost.csproj` 第 5 行：`net6.0`
  - `src/NSmartProxyClient/NSmartProxyClient.csproj` 第 5 行：同样是 `net6.0`
  - 两个 Dockerfile 的基础镜像 `mcr.microsoft.com/dotnet/sdk:6.0`

.NET 6 已于 2024-11-12 停止支持。安装文档仍指引用户安装 .NET Core 运行时后直接运行这些程序。Docker 镜像使用的是 SDK 标签，而不是运行时标签。

- **潜在影响：** 按仓库现状做的安装，运行时不再收到安全补丁。镜像里还带有 SDK 工具链。
- **修复建议：** 将可执行项目改为 `net8.0` 或当前的 STS/LTS 版本，使用对应的 `aspnet` 运行时镜像，并在发布说明中写明最低运行时版本。升级后重新跑客户端登录、端口映射和管理页面这三条安装路径。

## 18. 个人部署脚本用未认证的 HTTP GET 触发远程重启

- **严重程度：** 中
- **涉及文件：** `src/deploy.cmd` 全文

```bat
xcopy  %~dp0..\build\nspserver_v1.2 z:\nsmart /y /e /i
curl "http://2017studio.imwork.net:7002/index.html?processname=nspserver&action=restart"
```

脚本把构建结果拷到 `z:\nsmart`，再请求一个明文 HTTP 地址，用查询参数指定进程名和 `restart`。仓库里看不到 `7002` 端口上的服务端实现，因此该 URL 是否真的没有鉴权，**待确认**。可以确认的是：脚本本身没有凭据，动作为 GET，协议是 HTTP。

- **潜在影响：** 若该地址仍按脚本字面执行重启，网络路径上的人或能访问该端口的人都可以触发重启。HTTP 上也无法确认响应来自预期主机。脚本还把作者的部署主机名留在仓库里。
- **修复建议：** 从发布用分支删除个人脚本，或改成不入库的本地文件。远程重启使用 SSH 或带双向 TLS 的管理接口，动作用 POST，并要求一次性令牌。不要用查询字符串表达重启。

补充：`azure-pipelines.yml` 第 71–74 行和第 81–84 行使用 `docker login -u $(dhName) -p $(dhPwd)`。仓库没有把密码写死，Azure DevOps 的秘密变量通常会屏蔽日志，但 `-p` 仍会进入本机进程列表。建议改为 `--password-stdin`。此项本身不单列成更高严重程度。

## 19. 未登录即可查询端口和版本，异常响应带回堆栈

- **严重程度：** 低
- **涉及文件：**
  - `src/NSmartProxy/Extension/HttpServer_APIs.cs` 第 183–204 行：`GetServerPorts`、`GetVersionInfo` 只有 `[API]`，没有 `[Secure]`
  - `src/NSmartProxy.Infrastructure/Extensions/HttpServer/HttpServer.cs` 第 269–275 行：异常消息和 `StackTrace` 写入 JSON 响应

`GetServerPorts` 返回反向端口、配置端口和管理端口。任意异常的调用栈会返回给浏览器。

- **潜在影响：** 未登录的扫描者可以确认服务类型和端口布局。堆栈会暴露路径、类名和部署结构。
- **修复建议：** `GetServerPorts` 改为登录后可用。对外只返回通用错误号，详细异常只写服务器日志。

## 20. 管理页面静态文件无需登录即可下载

- **严重程度：** 低
- **涉及文件：** `src/NSmartProxy.Infrastructure/Extensions/HttpServer/HttpServer.cs` 第 129–155 行

URL 带扩展名时直接按文件返回。注释写着「TODO 权限控制（只是控制 html 权限而已）」，没有实现。`users.html`、`config.html` 和对应脚本都会被缓存并对外提供。接口在 Release 下仍有 `[Secure]` 检查，静态页本身不包含用户库。

- **潜在影响：** 未登录的人可以阅读管理功能的接口名称和页面结构，便于继续探测问题 2、3、15。
- **修复建议：** 除 `login.html` 和它依赖的样式脚本外，其余 html/js 在服务端检查会话后再返回。

## 21. System.Text.Json 8.0.0 落在已知拒绝服务公告的版本区间

- **严重程度：** 低
- **涉及文件：** `src/NSmartProxyWinform/packages.config` 第 61 行，`System.Text.Json` 版本 `8.0.0`。WinForms 服务工程的 `packages.config` 也有同样引用。

版本区间核对结果：

- **CVE-2024-30105**：公告中的 `System.Text.Json` 包范围包含 `8.0.0` 到 `8.0.3`，修复版本为 `8.0.4`。触发条件是对不可信输入调用 `JsonSerializer.DeserializeAsyncEnumerable`。
- **CVE-2024-43485**：使用 `[JsonExtensionData]` 反序列化不可信 JSON 时可能被算法复杂度攻击。包范围覆盖 8.0.0 起的早期 8.0.x，后续运行时补丁在更高的 8.0 版本。

全仓库 `*.cs` 中没有 `DeserializeAsyncEnumerable`，也没有 `JsonExtensionData`。业务配置解析使用的是 Newtonsoft.Json。因此这两个 CVE 是否能被本程序触发，**待确认**。可以确认的是 WinForms 工程声明的包版本落在公告给出的受影响区间。

- **潜在影响：** 若传递依赖或以后的代码走到上述 API，不可信 JSON 可以造成进程拒绝服务。
- **修复建议：** 把直接和传递引用的 `System.Text.Json` 升到当前 8.0 的安全补丁版本（至少高于上述公告的修复版本），并用 `dotnet list package --vulnerable` 复查。

## 22. 自带 appsettings.json 含 // 注释，当前解析器可能无法加载

- **严重程度：** 低
- **涉及文件：**
  - `src/NSmartProxy.ServerHost/appsettings.json` 第 2–6 行
  - `src/NSmartProxyClient/appsettings.json`、`src/NSmartProxyWinform/appsettings.json`、`plugins/NSmartProxyFTP/appsettings.json` 同样含 `//` 注释
  - `src/NSmartProxy.Infrastructure/ConfigHelper.cs` 第 35–42 行：`JsonConvert.DeserializeObject<T>(str)`，没有设置 `CommentHandling.Ignore`
  - `ServerHost.cs` 第 91–93 行：文件存在时走 `ReadAllConfig`，不走 `ConfigurationBuilder`

Newtonsoft.Json 的默认设置不接受注释。本环境没有 .NET SDK，没有实际执行反序列化，因此「按仓库文件原样启动是否立刻抛错」**待确认**。`Microsoft.Extensions.Configuration.Json` 默认允许注释，但服务端真正采用的配置对象来自 `ConfigHelper`，不是 `ServerHost.InitLogConfig` 里那个 `ConfigurationBuilder`。

- **潜在影响：** 若解析失败，按文档解压后的服务端会在 `ServerHost` 的重试循环里反复异常，安装者可能改用缺失配置文件的路径，从而落入 `new NSPServerConfig()`，匿名登录保持开启。配置文件不能被正常加载，也会让后面的安全配置无法下发。
- **修复建议：** 默认配置改成不带注释的合法 JSON，说明放到文档中。解析处显式允许注释并记录失败原因，不要在解析失败时静默使用最宽松的默认值。

---

## 安装部署安全加固清单

按新环境安装时建议按下面顺序做。清单是部署侧可以立刻执行的措施；源码级修复见各问题的建议。

1. 不要使用出厂口令 `admin` / `admin`。第一次启动后立刻改成随机长密码，并确认用户库里不再是无盐 SHA-256 的 `admin` 哈希。
2. 在成功加载的服务端配置中写入 `"supportAnonymousLogin": false`。不要依赖页面上的开关作为唯一防线。
3. 管理端口不要映射到公网。用防火墙限制 `12309`，只允许本机或运维网段，前面加 HTTPS。
4. 把 `nsmart_sec_key`、`nsmart_user.db`、`.usercache`、`./ca`、`./temp` 的权限收成仅运行账户可读写。密钥文件一旦可能泄露，应删除并重新生成，同时使旧令牌失效。
5. 发布用的客户端 `appsettings.json` 把 `Clients` 留空。需要映射 `3389`、`22`、`445`、`5900` 时单独评审。
6. 不要使用 FTP 插件仓库里的 `admin` / `654123` 和 `d:\`。
7. 容器改用非 root 用户和仍受支持的运行时镜像。不要把 `12300-22300` 整段映射到宿主机。
8. Windows 服务不要用 LocalSystem。Linux 上为程序建立专用用户，不要长期 `sudo` 前台运行。仓库没有 systemd 单元，需要自行编写时使用 `User=`、`NoNewPrivileges=true`、`ProtectSystem=strict`。
9. 客户端登录不要把口令放在命令行。限制 `.usercache` 权限，或在不受信任的机器上删除它。
10. 只发布 Release 构建。Debug 构建会跳过 Web 鉴权。
11. 升级 jQuery 到 3.5.0 之后的版本，升级 FTP 插件的 log4net 到 2.0.10 之后，并把运行时从 .NET 6 / .NET Core 2.2 迁到仍受支持的版本。
12. 删除或停用 `src/deploy.cmd` 里的明文重启 URL。镜像仓库登录改用标准输入传递密码。
13. 安装完成后从外网分别访问管理端口、反向端口和一条隧道，确认管理接口要求登录、匿名注册被拒绝、隧道流量不是被中间网络明文看到的业务口令。
