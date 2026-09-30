; ============================================================
;  WarThunderTelemetry —— Inno Setup 安装包脚本
;
;  编译：
;    "C:\Users\1\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer\setup.iss
;  或双击项目根目录的 _installer-run.cmd
;
;  产物：
;    publish\WarThunderTelemetry-Setup-<版本>.exe
;
;  打包源目录 publish\WarThunderTelemetry\ 需先用 ./_publish-run.cmd 生成。
; ============================================================

#define AppName        "战雷遥测悬浮辅助"
#define AppNameEn      "WarThunderTelemetry"
#define AppVersion     "1.1.0"
#define AppPublisher   "dadnawda"
#define AppURL         "https://github.com/dadnawda/WarThunderTelemetry"
#define AppExeName     "WarThunderTelemetry.exe"

; 安装源目录（相对本 .iss 所在目录）
#define SourceDir      "..\publish\WarThunderTelemetry"

[Setup]
; ---- 身份 ----
; AppId 一旦发布就不要再改，否则升级会被当成另一个软件、装出两份
AppId={{9607525A-2638-4343-B3CC-8B02AE8BF083}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases

; ---- 安装位置：按用户安装，不弹 UAC ----
; 程序本身不需要管理员权限（读游戏本地 8111 端口 + 写自己的配置到 %LOCALAPPDATA%），
; 所以装到用户目录最省事，普通用户也能装。
; 想改成装到 Program Files（会弹 UAC）就把下面两行换成：
;   PrivilegesRequired=admin
;   DefaultDirName={autopf}\{#AppNameEn}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppNameEn}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; ---- 架构 ----
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; ---- 外观 ----
WizardStyle=modern
SetupIconFile=..\src\WarThunderTelemetry.App\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
ShowLanguageDialog=auto

; ---- 输出 ----
OutputDir=..\publish
OutputBaseFilename={#AppNameEn}-Setup-{#AppVersion}
; lzma2/ultra64 压得最小，代价是编译慢（168MB 大约要几分钟）
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4

; ---- 行为 ----
; 升级时若旧版本还在运行，自动关掉它（否则文件被占用装不进去）
CloseApplications=yes
; 记住上次的安装目录（默认就是开启的，这里显式写出便于日后查阅）
UsePreviousAppDir=yes

; ---- 卸载 ----
CreateUninstallRegKey=yes

[Languages]
; 简体中文语言包来自 Inno Setup 官方仓库，安装器没自带，所以放在 installer\ 下引用
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 递归打包整个发布目录。
; flags: ignoreversion 让升级时总是覆盖；递归 flags 用 recursesubdirs/createallsubdirs。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 装完给个"立即运行"勾选（默认勾上）
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 悬浮窗位置等配置写在 %LOCALAPPDATA%\WarThunderTelemetry\settings.json。
; 卸载时**不**删它 —— 重装后位置偏好还在。
; 若确实要一起清掉，把下面两行取消注释：
; Type: filesandordirs; Name: "{localappdata}\WarThunderTelemetry"

[Messages]
; 覆盖几处提示，把"这是干什么的"说清楚
chinese.WelcomeLabel2=即将把 [name/ver] 安装到您的电脑。%n%n这是一个《战争雷霆》的遥测悬浮辅助工具：%n读取游戏本地 8111 端口的数据，用置顶透明悬浮窗显示飞行与导弹发射参数。%n%n它不修改游戏文件、不注入游戏进程。%n%n建议先关闭正在运行的游戏和本程序，再继续。
english.WelcomeLabel2=This will install [name/ver] on your computer.%n%nA telemetry overlay for War Thunder: reads the game's local port 8111 and shows flight and missile launch data in an always-on-top transparent overlay.%n%nIt does not modify game files or inject into the game process.%n%nIt is recommended to close the game and this app before continuing.
